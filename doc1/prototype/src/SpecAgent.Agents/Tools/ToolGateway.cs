using System.Text;
using System.Text.RegularExpressions;
using SpecAgent.Agents.Knowledge;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Tools;

/// <summary>
/// The "hands": the single execute() door through which agents touch anything outside their own
/// context. Every call is written to the session log (TOOL_CALLED), tools are read-only, and no
/// credentials ever pass through here — so a prompt injection inside a ТЗ has nothing to steal.
/// </summary>
public sealed class ToolGateway(IEventStore events, IKnowledgeBase kb)
{
    public async Task<string> ExecuteAsync(string sessionId, string agent, string tool,
        IReadOnlyDictionary<string, string> args, TzDocument doc, CancellationToken ct = default)
    {
        string result;
        try
        {
            result = tool switch
            {
                "tz.read_span" => ReadSpan(doc, args["sectionId"]),
                "tz.search" => SearchTz(doc, args["query"]),
                "tz.list_sections" => ListSections(doc),
                "tz.check_quote" => CheckQuote(doc, args["sectionId"], args["quote"]),
                "kb.search" or "rag.search" => SearchKb(args["query"]),
                _ => throw new ToolError($"Unknown tool '{tool}'"),
            };
        }
        catch (Exception ex) when (ex is ToolError or KeyNotFoundException)
        {
            result = $"ToolError: {ex.Message}";
        }

        await events.AppendAsync(sessionId, EventTypes.ToolCalled, agent,
            $"{tool}({string.Join(", ", args.Select(a => $"{a.Key}={Trim(a.Value, 60)}"))}) → {result.Length} chars",
            new { tool, args }, ct);
        return result;
    }

    /// <summary>Renders an official BCC-HUB Markdown audit report (ISO 29148 / IEEE 830).</summary>
    public static string RenderReportMarkdown(TzDocument doc, AuditReport report, string modelName)
    {
        var sb = new StringBuilder();
        var blockers = report.Findings.Count(f => f.Severity == Severity.Blocker);
        var criticals = report.Findings.Count(f => f.Severity == Severity.Critical);
        var majors = report.Findings.Count(f => f.Severity == Severity.Major);
        var minors = report.Findings.Count(f => f.Severity == Severity.Minor);
        var goNoGo = blockers > 0 || criticals >= 2
            ? "**NO-GO** — документ НЕ готов к передаче в разработку и тестирование."
            : criticals == 1
                ? "**УСЛОВНЫЙ GO** — требуется устранить критическое замечание до релиза."
                : "**GO** — документ пригоден для передачи в разработку.";

        sb.AppendLine($"# Отчёт о проверке требований: {doc.Title}");
        sb.AppendLine();
        sb.AppendLine("## 1. Паспорт прогона");
        sb.AppendLine("| Параметр | Значение |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Документ** | `{doc.SourceFileName}` ({doc.Title}) |");
        sb.AppendLine($"| **Дата прогона (UTC)** | {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} |");
        sb.AppendLine($"| **Сессия** | `{report.SessionId}` |");
        sb.AppendLine($"| **Домен** | {report.Domain} |");
        sb.AppendLine($"| **Модель** | `{modelName}` |");
        sb.AppendLine($"| **Оценка качества (Score)** | **{report.QualityScore} / 100** |");
        sb.AppendLine();

        sb.AppendLine("## 2. Как агент нарезал требования");
        sb.AppendLine("| ID в отчёте | Раздел документа | Краткая формулировка |");
        sb.AppendLine("| :--- | :--- | :--- |");
        foreach (var s in doc.Sections)
        {
            var snippet = s.Text.Replace("\r", "").Replace("\n", " ").Replace("|", "\\|");
            if (snippet.Length > 110) snippet = snippet[..110] + "…";
            sb.AppendLine($"| `{s.Id}` | {s.Title.Replace("|", "\\|")} | {snippet} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 3. Сводка нарушений (ISO/IEC/IEEE 29148 & IEEE 830)");
        sb.AppendLine($"- **Разделов проверено:** {doc.Sections.Count}");
        sb.AppendLine($"- **Всего подтверждённых дефектов:** {report.Findings.Count} (Blocker: {blockers}, Critical: {criticals}, Major: {majors}, Minor: {minors})");
        sb.AppendLine($"- **Отброшено неподтверждённых/дублей:** {report.RejectedRawFindingIds.Count}");
        sb.AppendLine($"- **Сгенерировано патчей:** {report.Patches.Count} (верифицировано: {report.Patches.Count(p => p.Verified)})");
        sb.AppendLine();

        sb.AppendLine("## 4. Детальный разбор дефектов");
        var patches = report.Patches.ToDictionary(p => p.FindingId);
        foreach (var f in report.Findings)
        {
            sb.AppendLine($"### [{f.Severity}] {f.Id} — {f.Title} (раздел `{f.SectionId}`)");
            sb.AppendLine($"- **Агенты:** {string.Join(", ", f.Agents)}");
            if (!string.IsNullOrWhiteSpace(f.StandardRef))
                sb.AppendLine($"- **Стандарт:** {f.StandardRef}");
            sb.AppendLine($"- **Цитата:** > {f.Quote.Replace("\n", " ")}");
            sb.AppendLine($"- **Описание:** {f.Description}");
            if (!string.IsNullOrWhiteSpace(f.Recommendation))
                sb.AppendLine($"- **Рекомендация:** {f.Recommendation}");
            if (!string.IsNullOrWhiteSpace(f.Rationale))
                sb.AppendLine($"- **Вердикт арбитра:** {f.Rationale}");

            if (patches.TryGetValue(f.Id, out var p))
            {
                sb.AppendLine($"- **Исправление (патч, {(p.Verified ? "верифицирован" : "требует ручной проверки")}, раундов: {p.Rounds}):**");
                sb.AppendLine("```markdown");
                sb.AppendLine(p.After.Trim());
                sb.AppendLine("```");
                if (!string.IsNullOrWhiteSpace(p.Gherkin))
                {
                    sb.AppendLine("- **Критерии приёмки (Gherkin):**");
                    sb.AppendLine("```gherkin");
                    sb.AppendLine(p.Gherkin.Trim());
                    sb.AppendLine("```");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 5. Итог и вердикт (Go / No-Go)");
        sb.AppendLine(goNoGo);
        var topPriority = report.Findings.Where(f => f.Severity is Severity.Blocker or Severity.Critical).Take(5).ToList();
        if (topPriority.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Что исправить в первую очередь:**");
            foreach (var f in topPriority)
                sb.AppendLine($"1. **[{f.Severity}] `{f.SectionId}` — {f.Title}:** {f.Recommendation}");
        }

        return sb.ToString();
    }

    /// <summary>Applies generated patches to the original document and renders a fixed Markdown specification.</summary>
    public static string RenderPatchedDocumentMarkdown(TzDocument doc, AuditReport report)
    {
        var patchBySection = report.Patches
            .GroupBy(p => p.SectionId)
            .ToDictionary(g => g.Key, g => g.Last());

        var sb = new StringBuilder();
        sb.AppendLine($"# {doc.Title} *(Исправленная версия — SpecAgent)*");
        sb.AppendLine();
        sb.AppendLine($"> Автоматически применено исправлений: **{patchBySection.Count}** (исходный файл: `{doc.SourceFileName}`, сессия `{report.SessionId}`).");
        sb.AppendLine();

        foreach (var s in doc.Sections)
        {
            sb.AppendLine($"## {s.Id} {s.Title}");
            sb.AppendLine();
            if (patchBySection.TryGetValue(s.Id, out var patch) && !string.IsNullOrWhiteSpace(patch.After))
            {
                sb.AppendLine(patch.After.Trim());
                if (!string.IsNullOrWhiteSpace(patch.Gherkin))
                {
                    sb.AppendLine();
                    sb.AppendLine("### Критерии приёмки (добавлено SpecAgent)");
                    sb.AppendLine("```gherkin");
                    sb.AppendLine(patch.Gherkin.Trim());
                    sb.AppendLine("```");
                }
            }
            else
            {
                sb.AppendLine(s.Text.Trim());
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ReadSpan(TzDocument doc, string sectionId) =>
        doc.Find(sectionId)?.Text ?? throw new ToolError($"Section '{sectionId}' not found");

    private static string ListSections(TzDocument doc) =>
        string.Join('\n', doc.Sections.Select(s => $"[{s.Id}] {s.Title}"));

    private static string CheckQuote(TzDocument doc, string sectionId, string quote)
    {
        var text = ReadSpan(doc, sectionId);
        return text.Contains(quote, StringComparison.OrdinalIgnoreCase) ? "FOUND" : "NOT_FOUND";
    }

    private static string SearchTz(TzDocument doc, string query)
    {
        var hits = doc.Sections
            .Where(s => Regex.IsMatch(s.Title + "\n" + s.Text, Regex.Escape(query), RegexOptions.IgnoreCase))
            .Select(s => $"[{s.Id}] {s.Title}");
        return string.Join('\n', hits) is { Length: > 0 } r ? r : "(no matches)";
    }

    private string SearchKb(string query) =>
        string.Join("\n\n", kb.Search(query).Select(s => $"[{s.Source} › {s.Heading}]\n{s.Text}")) is { Length: > 0 } r
            ? r : "(knowledge base: nothing relevant)";

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public sealed class ToolError(string message) : Exception(message);
