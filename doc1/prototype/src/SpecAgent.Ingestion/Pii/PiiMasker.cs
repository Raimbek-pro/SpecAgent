using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Findings;

namespace SpecAgent.Ingestion.Pii;

/// <summary>
/// Deterministic PII masking — runs BEFORE any text reaches an LLM. Regex + checksums
/// (ИИН/БИН mod-11, PAN Luhn) so we don't mask random 12-digit numbers.
/// Equal values get equal tokens inside one session, so agents can still reason
/// ("[IIN_1] is used in 3.2 and 5.1").
/// </summary>
public sealed partial class PiiMasker(IPiiVault vault)
{
    private sealed record Rule(string Kind, Regex Pattern, Func<string, bool>? Validate = null);

    [GeneratedRegex(@"\bKZ\d{2}[A-Z0-9]{16}\b", RegexOptions.IgnoreCase)] private static partial Regex Iban();
    [GeneratedRegex(@"\b(?:\d[ -]?){12,18}\d\b")] private static partial Regex Pan();
    [GeneratedRegex(@"\b\d{12}\b")] private static partial Regex Iin();
    // Explicitly labeled IIN/BIN (e.g. "ИИН тестового клиента: 950115350123") — mask even if test checksum is synthetic.
    [GeneratedRegex(@"(?i)((?:ИИН|БИН|IIN|BIN)[^\d\r\n]{0,30})(\d{12})\b")] private static partial Regex LabeledIin();
    // International E.164 / formatted phones (+1, +44, +7, +998, etc.) and local CIS 8-xxx numbers.
    [GeneratedRegex(@"(?<!\d)(?:\+\d{1,3}[\s\-()]*(?:\d[\s\-()]*){8,13}\d|8[\s\-(]*\d{3}[\s\-)]*\d{3}[\s\-]*\d{2}[\s\-]*\d{2})(?!\d)")] private static partial Regex Phone();
    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+\.[\w.-]+\b")] private static partial Regex Email();
    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")] private static partial Regex Ipv4();
    [GeneratedRegex(@"(?i)\b(?:api[_-]?key|secret|token|password|пароль)\s*[:=]\s*\S{6,}")] private static partial Regex Secret();

    // Order matters: longer / more specific patterns first.
    private static readonly Rule[] Rules =
    [
        new("SECRET", Secret()),
        new("IBAN", Iban()),
        new("PAN", Pan(), v => Luhn(Digits(v))),
        new("IIN", Iin(), IsValidIin),
        new("PHONE", Phone(), v => Digits(v).Length is >= 10 and <= 15),
        new("EMAIL", Email()),
        new("IP", Ipv4()),
    ];

    public static readonly string[] AllKinds = ["SECRET", "IBAN", "PAN", "IIN", "PHONE", "EMAIL", "IP"];

    public bool Enabled { get; set; } = true;
    public HashSet<string> EnabledKinds { get; set; } = new(AllKinds, StringComparer.OrdinalIgnoreCase);

    public async Task<TzDocument> MaskAsync(string sessionId, TzDocument doc, CancellationToken ct = default)
    {
        if (!Enabled) return doc;
        var title = await MaskAsync(sessionId, doc.Title, ct);
        var sections = new List<TzSection>(doc.Sections.Count);
        foreach (var s in doc.Sections)
        {
            sections.Add(s with
            {
                Title = await MaskAsync(sessionId, s.Title, ct),
                Text = await MaskAsync(sessionId, s.Text, ct),
            });
        }
        return doc with { Title = title, Sections = sections };
    }

    public async Task<string> MaskAsync(string sessionId, string text, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrEmpty(text)) return text;
        var kinds = EnabledKinds;
        if (kinds.Contains("IIN"))
        {
            text = await ReplaceAsync(text, LabeledIin(), async m =>
                m.Groups[1].Value + await vault.TokenizeAsync(sessionId, "IIN", m.Groups[2].Value, ct));
        }
        foreach (var rule in Rules)
        {
            if (!kinds.Contains(rule.Kind)) continue;
            text = await ReplaceAsync(text, rule.Pattern, async m =>
            {
                if (m.Value.StartsWith('[')) return m.Value;
                if (rule.Validate is { } validate && !validate(m.Value)) return m.Value;
                return await vault.TokenizeAsync(sessionId, rule.Kind, m.Value, ct);
            });
        }
        return text;
    }

    public Task<string> DemaskAsync(string sessionId, string text, CancellationToken ct = default) =>
        vault.DetokenizeAsync(sessionId, text, ct);

    public async Task<TzDocument> DemaskAsync(string sessionId, TzDocument doc, CancellationToken ct = default)
    {
        var title = await DemaskAsync(sessionId, doc.Title, ct);
        var sections = new List<TzSection>(doc.Sections.Count);
        foreach (var s in doc.Sections)
        {
            sections.Add(s with
            {
                Title = await DemaskAsync(sessionId, s.Title, ct),
                Text = await DemaskAsync(sessionId, s.Text, ct),
            });
        }
        return doc with { Title = title, Sections = sections };
    }

    public async Task<AuditReport> DemaskAsync(string sessionId, AuditReport report, bool includeFindings = true, CancellationToken ct = default)
    {
        IReadOnlyList<Finding> findings = report.Findings;
        if (includeFindings)
        {
            var demaskedFindings = new List<Finding>(report.Findings.Count);
            foreach (var f in report.Findings)
            {
                demaskedFindings.Add(f with
                {
                    Quote = await DemaskAsync(sessionId, f.Quote, ct),
                    Title = await DemaskAsync(sessionId, f.Title, ct),
                    Description = await DemaskAsync(sessionId, f.Description, ct),
                    Recommendation = await DemaskAsync(sessionId, f.Recommendation, ct),
                    Rationale = await DemaskAsync(sessionId, f.Rationale, ct),
                });
            }
            findings = demaskedFindings;
        }

        var patches = new List<Patch>(report.Patches.Count);
        foreach (var p in report.Patches)
        {
            patches.Add(p with
            {
                Before = await DemaskAsync(sessionId, p.Before, ct),
                After = await DemaskAsync(sessionId, p.After, ct),
                Gherkin = await DemaskAsync(sessionId, p.Gherkin, ct),
            });
        }

        return report with { Findings = findings, Patches = patches };
    }

    private static async Task<string> ReplaceAsync(string input, Regex regex, Func<Match, Task<string>> evaluator)
    {
        var matches = regex.Matches(input);
        if (matches.Count == 0) return input;

        var sb = new StringBuilder(input.Length);
        var lastIndex = 0;
        foreach (Match m in matches)
        {
            sb.Append(input, lastIndex, m.Index - lastIndex);
            sb.Append(await evaluator(m));
            lastIndex = m.Index + m.Length;
        }
        sb.Append(input, lastIndex, input.Length - lastIndex);
        return sb.ToString();
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    public static bool Luhn(string digits)
    {
        if (digits.Length is < 13 or > 19) return false;
        int sum = 0; bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// <summary>Kazakhstan ИИН/БИН checksum (weights 1..11, fallback 3..11,1,2; mod 11).</summary>
    public static bool IsValidIin(string iin)
    {
        if (iin.Length != 12 || !iin.All(char.IsDigit)) return false;
        int[] d = iin.Select(c => c - '0').ToArray();
        int Check(int[] w) => Enumerable.Range(0, 11).Sum(i => d[i] * w[i]) % 11;
        int c = Check([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        if (c == 10) c = Check([3, 4, 5, 6, 7, 8, 9, 10, 11, 1, 2]);
        return c != 10 && c == d[11];
    }
}

/// <summary>token ↔ value map. The ONLY component that holds real PII.</summary>
public interface IPiiVault
{
    Task<string> TokenizeAsync(string sessionId, string kind, string value, CancellationToken ct = default);
    Task<string> DetokenizeAsync(string sessionId, string text, CancellationToken ct = default);
    Task<int> CountAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// In-process vault. For production put it behind its own service/DB with encryption at rest;
/// the interface stays the same.
/// </summary>
public sealed partial class InMemoryPiiVault : IPiiVault
{
    private sealed class SessionVault
    {
        public readonly Dictionary<string, string> ValueToToken = new();
        public readonly Dictionary<string, string> TokenToValue = new();
        public readonly Dictionary<string, int> Counters = new();
    }

    private readonly ConcurrentDictionary<string, SessionVault> _vaults = new();

    [GeneratedRegex(@"\[(?:SECRET|IBAN|PAN|IIN|PHONE|EMAIL|IP)_\d+\]")] private static partial Regex Token();

    public Task<string> TokenizeAsync(string sessionId, string kind, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var v = _vaults.GetOrAdd(sessionId, _ => new SessionVault());
        lock (v)
        {
            var key = $"{kind}:{value}";
            if (v.ValueToToken.TryGetValue(key, out var existing)) return Task.FromResult(existing);
            var n = v.Counters[kind] = v.Counters.GetValueOrDefault(kind) + 1;
            var token = $"[{kind}_{n}]";
            v.ValueToToken[key] = token;
            v.TokenToValue[token] = value;
            return Task.FromResult(token);
        }
    }

    public Task<string> DetokenizeAsync(string sessionId, string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_vaults.TryGetValue(sessionId, out var v)) return Task.FromResult(text);
        lock (v) return Task.FromResult(Token().Replace(text, m => v.TokenToValue.GetValueOrDefault(m.Value, m.Value)));
    }

    public Task<int> CountAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_vaults.TryGetValue(sessionId, out var v) ? v.TokenToValue.Count : 0);
    }
}
