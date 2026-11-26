using System.Text;
using System.Text.RegularExpressions;
using SpecAgent.Core.Documents;

namespace SpecAgent.Agents.Agents;

/// <summary>
/// Deterministic (0-token) cross-section entity index built from the full TzDocument.
/// Surfaces statuses/enums, HTTP endpoints, numeric limits/timeouts, and cross-references
/// across ALL sections so critics reading a slice can still catch cross-section contradictions
/// (e.g. DECLINED in Ф-5 vs REJECTED in section 3, mismatched limits, or broken references).
/// </summary>
public static partial class DocumentIndex
{
    [GeneratedRegex(@"\b[A-Z][A-Z0-9_]{2,24}\b")]
    private static partial Regex UpperStatusPattern();

    [GeneratedRegex(@"\b(?:GET|POST|PUT|PATCH|DELETE)\s+/[\w/\-{}.:?=&]+", RegexOptions.IgnoreCase)]
    private static partial Regex EndpointPattern();

    [GeneratedRegex(@"\b\d[\d\s]{0,12}(?:%|₸|тенге|KZT|USD|EUR|с|сек|секунд|мин|минут|час|часов|дн|дней|раз|раза|попыток|ms|мс|RPS|MB|МБ)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LimitPattern();

    private static readonly HashSet<string> StopTokens = new(StringComparer.Ordinal)
    {
        "API", "HTTP", "HTTPS", "JSON", "XML", "PDF", "DOCX", "URL", "URI", "UTF", "UUID", "JWT",
        "SMS", "OTP", "PII", "PAN", "CVV", "CVV2", "IBAN", "KYC", "AML", "RBAC", "SLA", "NFR", "RPS",
        "ISO", "IEEE", "PCI", "DSS", "TLS", "AES", "SQL", "REST", "SOAP", "GRPC", "TBD", "TODO", "FAQ",
        "GET", "POST", "PUT", "PATCH", "DELETE", "KZT", "USD", "EUR", "MCC", "PIN", "TIN", "BIN", "IIN",
    };

    public static string Build(TzDocument doc)
    {
        var statuses = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var endpoints = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var limits = new List<string>();

        foreach (var s in doc.Sections)
        {
            var combined = $"{s.Title}\n{s.Text}";

            foreach (Match m in UpperStatusPattern().Matches(combined))
            {
                var tok = m.Value;
                if (StopTokens.Contains(tok) || tok.All(char.IsDigit)) continue;
                if (!statuses.TryGetValue(tok, out var set)) statuses[tok] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(s.Id);
            }

            foreach (Match m in EndpointPattern().Matches(combined))
            {
                var ep = Regex.Replace(m.Value.Trim(), @"\s+", " ");
                if (!endpoints.TryGetValue(ep, out var set)) endpoints[ep] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(s.Id);
            }

            foreach (Match m in LimitPattern().Matches(combined))
            {
                if (limits.Count >= 24) break;
                var val = Regex.Replace(m.Value.Trim(), @"\s+", " ");
                var entry = $"[{s.Id}] {val}";
                if (!limits.Contains(entry)) limits.Add(entry);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("Sections: " + string.Join(", ", doc.Sections.Select(s => string.IsNullOrWhiteSpace(s.Title) ? s.Id : $"{s.Id} ({Trim(s.Title, 32)})")));

        if (statuses.Count > 0)
        {
            sb.AppendLine("Statuses/Enums: " + string.Join("; ", statuses.Take(25)
                .Select(kv => $"{kv.Key} [{string.Join(",", kv.Value)}]")));
        }

        if (endpoints.Count > 0)
        {
            sb.AppendLine("Endpoints: " + string.Join("; ", endpoints.Take(15)
                .Select(kv => $"{kv.Key} [{string.Join(",", kv.Value)}]")));
        }

        if (limits.Count > 0)
        {
            sb.AppendLine("Numeric limits/SLA: " + string.Join("; ", limits));
        }

        return sb.ToString().Trim();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
