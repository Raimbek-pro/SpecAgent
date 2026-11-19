using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SpecAgent.Core.Documents;

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
    [GeneratedRegex(@"(?<!\d)(?:\+7|8)[\s\-(]*\d{3}[\s\-)]*\d{3}[\s\-]*\d{2}[\s\-]*\d{2}(?!\d)")] private static partial Regex Phone();
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
        new("PHONE", Phone()),
        new("EMAIL", Email()),
        new("IP", Ipv4()),
    ];

    public TzDocument Mask(string sessionId, TzDocument doc) => doc with
    {
        Title = Mask(sessionId, doc.Title),
        Sections = doc.Sections.Select(s => s with { Title = Mask(sessionId, s.Title), Text = Mask(sessionId, s.Text) }).ToList(),
    };

    public string Mask(string sessionId, string text)
    {
        foreach (var rule in Rules)
        {
            text = rule.Pattern.Replace(text, m =>
            {
                if (m.Value.StartsWith('[')) return m.Value;
                if (rule.Validate is { } validate && !validate(m.Value)) return m.Value;
                return vault.Tokenize(sessionId, rule.Kind, m.Value);
            });
        }
        return text;
    }

    public string Demask(string sessionId, string text) => vault.Detokenize(sessionId, text);

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
    string Tokenize(string sessionId, string kind, string value);
    string Detokenize(string sessionId, string text);
    int Count(string sessionId);
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

    public string Tokenize(string sessionId, string kind, string value)
    {
        var v = _vaults.GetOrAdd(sessionId, _ => new SessionVault());
        lock (v)
        {
            var key = $"{kind}:{value}";
            if (v.ValueToToken.TryGetValue(key, out var existing)) return existing;
            var n = v.Counters[kind] = v.Counters.GetValueOrDefault(kind) + 1;
            var token = $"[{kind}_{n}]";
            v.ValueToToken[key] = token;
            v.TokenToValue[token] = value;
            return token;
        }
    }

    public string Detokenize(string sessionId, string text)
    {
        if (!_vaults.TryGetValue(sessionId, out var v)) return text;
        lock (v) return Token().Replace(text, m => v.TokenToValue.GetValueOrDefault(m.Value, m.Value));
    }

    public int Count(string sessionId) => _vaults.TryGetValue(sessionId, out var v) ? v.TokenToValue.Count : 0;
}
