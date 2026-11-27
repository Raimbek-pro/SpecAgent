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
                "kb.search" or "rag.search" => await SearchKbAsync(args["query"], ct),
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

    /// <summary>Resolves the current SpecAgent version (git short commit or assembly version) for the report passport.</summary>
    public static string ResolveAgentVersion()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SPECAGENT_VERSION");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(root);
            while (dir is not null)
            {
                var gitHead = Path.Combine(dir.FullName, ".git", "HEAD");
                if (File.Exists(gitHead))
                {
                    try
                    {
                        var head = File.ReadAllText(gitHead).Trim();
                        string? sha = null;
                        if (head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
                        {
                            var refPath = head[4..].Trim().Replace('/', Path.DirectorySeparatorChar);
                            var fullRef = Path.Combine(dir.FullName, ".git", refPath);
                            if (File.Exists(fullRef))
                            {
                                sha = File.ReadAllText(fullRef).Trim();
                            }
                            else
                            {
                                var packed = Path.Combine(dir.FullName, ".git", "packed-refs");
                                if (File.Exists(packed))
                                {
                                    var refKey = head[4..].Trim();
                                    foreach (var line in File.ReadLines(packed))
                                    {
                                        if (line.Length > 41 && !line.StartsWith('#') && line.EndsWith(refKey, StringComparison.Ordinal))
                                        {
                                            sha = line.Split(' ')[0].Trim();
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        else if (head.Length >= 7)
                        {
                            sha = head;
                        }

                        if (!string.IsNullOrWhiteSpace(sha) && sha.Length >= 7)
                            return $"SpecAgent (commit `{sha[..7]}`)";
                    }
                    catch { /* fallback to assembly version */ }
                    break;
                }
                dir = dir.Parent;
            }
        }

        var ver = typeof(ToolGateway).Assembly.GetName().Version;
        return ver is not null ? $"SpecAgent v{ver.Major}.{ver.Minor}.{ver.Build}" : "SpecAgent";
    }

    /// <summary>Renders an official BCC-HUB Markdown audit report (ISO 29148 / IEEE 830).</summary>
    public static string RenderReportMarkdown(TzDocument doc, AuditReport report, string modelName, string? agentVersion = null)
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

        var version = string.IsNullOrWhiteSpace(agentVersion) ? ResolveAgentVersion() : agentVersion;

        sb.AppendLine($"# Отчёт о проверке требований: {doc.Title}");
        sb.AppendLine();
        sb.AppendLine("## 1. Паспорт прогона");
        sb.AppendLine("| Параметр | Значение |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Документ** | `{doc.SourceFileName}` ({doc.Title}) |");
        sb.AppendLine($"| **Дата прогона (UTC)** | {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} |");
        sb.AppendLine($"| **Версия агента** | {version} |");
        sb.AppendLine($"| **Сессия** | `{report.SessionId}` |");
        sb.AppendLine($"| **Домен** | {report.Domain} |");
        sb.AppendLine($"| **Модель** | `{modelName}` |");
        var patchedSections = report.Patches.Where(p => !string.IsNullOrWhiteSpace(p.After)).Select(p => p.SectionId).ToHashSet();
        var remPenalty = report.Findings.Where(f => !patchedSections.Contains(f.SectionId))
            .Sum(f => f.Severity switch { Severity.Blocker => 15, Severity.Critical => 8, Severity.Major => 4, _ => 1 });
        var afterScore = Math.Max(0, 100 - remPenalty);
        var scoreText = report.Patches.Count > 0
            ? $"**{report.QualityScore} / 100 → {afterScore} / 100** после автоисправлений (+{afterScore - report.QualityScore})"
            : $"**{report.QualityScore} / 100**";

        sb.AppendLine($"| **Оценка качества (Score)** | {scoreText} |");
        sb.AppendLine();

        sb.AppendLine("## 2. Как агент нарезал требования");
        sb.AppendLine("| ID в отчёте | ID / раздел в документе | Краткая формулировка |");
        sb.AppendLine("| :--- | :--- | :--- |");
        foreach (var s in doc.Sections)
        {
            var docRef = string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title.Replace("|", "\\|");
            var snippet = s.Text.Replace("\r", "").Replace("\n", " ").Replace("|", "\\|").Trim();
            if (snippet.Length == 0) snippet = "*(заголовок раздела)*";
            else if (snippet.Length > 110) snippet = snippet[..110] + "…";
            sb.AppendLine($"| `{s.Id}` | {docRef} | {snippet} |");
        }
        sb.AppendLine();

        var findingsBySection = report.Findings
            .GroupBy(f => f.SectionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var contentSections = doc.Sections.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        var cleanSectionsCount = contentSections.Count(s => !findingsBySection.ContainsKey(s.Id));

        sb.AppendLine("## 3. Сводка нарушений (ISO/IEC/IEEE 29148 & IEEE 830)");
        sb.AppendLine($"- **Разделов / требований нарезано:** {doc.Sections.Count} (содержательных: {contentSections.Count}, без замечаний: {cleanSectionsCount})");
        sb.AppendLine($"- **Всего подтверждённых дефектов:** {report.Findings.Count} (Blocker: {blockers}, Critical: {criticals}, Major: {majors}, Minor: {minors})");
        sb.AppendLine($"- **Отброшено неподтверждённых/дублей:** {report.RejectedRawFindingIds.Count}");
        sb.AppendLine($"- **Сгенерировано патчей:** {report.Patches.Count} (верифицировано: {report.Patches.Count(p => p.Verified)})");

        var criticalDefects = report.Findings.Where(f => f.Severity is Severity.Blocker or Severity.Critical).ToList();
        if (criticalDefects.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Список критичных дефектов (Blocker / Critical):**");
            foreach (var f in criticalDefects)
                sb.AppendLine($"- **[{f.Severity}] {f.Id} (`{f.SectionId}`):** {f.Title}");
        }
        sb.AppendLine();

        sb.AppendLine("## 4. Детальный разбор дефектов");
        sb.AppendLine();
        sb.AppendLine("### 4.1. Проверка каждого требования");
        sb.AppendLine("| ID | Раздел / требование | Статус | Выявленные дефекты |");
        sb.AppendLine("| :--- | :--- | :--- | :--- |");
        foreach (var s in contentSections)
        {
            var label = string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title.Replace("|", "\\|");
            if (findingsBySection.TryGetValue(s.Id, out var secFindings) && secFindings.Count > 0)
            {
                var maxSev = secFindings.Max(f => f.Severity);
                var refs = string.Join(", ", secFindings.Select(f => $"{f.Id} [{f.Severity}]"));
                sb.AppendLine($"| `{s.Id}` | {label} | Нарушения ({maxSev}) | {refs} |");
            }
            else
            {
                sb.AppendLine($"| `{s.Id}` | {label} | OK | Замечаний не выявлено |");
            }
        }
        sb.AppendLine();

        var patches = report.Patches.ToDictionary(p => p.FindingId);
        foreach (var f in report.Findings)
        {
            sb.AppendLine($"#### [{f.Severity}] {f.Id} — {f.Title} (раздел `{f.SectionId}`)");
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
                var patchBody = p.After.Trim();
                var patchFence = FenceFor(patchBody);
                sb.AppendLine($"{patchFence}markdown");
                sb.AppendLine(patchBody);
                sb.AppendLine(patchFence);
                if (!string.IsNullOrWhiteSpace(p.Gherkin))
                {
                    var gherkinBody = p.Gherkin.Trim();
                    var gherkinFence = FenceFor(gherkinBody);
                    sb.AppendLine("- **Критерии приёмки (Gherkin):**");
                    sb.AppendLine($"{gherkinFence}gherkin");
                    sb.AppendLine(gherkinBody);
                    sb.AppendLine(gherkinFence);
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("### 4.2. Проверка всего набора требований");
        var consistencyFindings = report.Findings.Where(f =>
            f.Agents.Contains("logic", StringComparer.OrdinalIgnoreCase) ||
            (f.StandardRef?.Contains("Consistency", StringComparison.OrdinalIgnoreCase) ?? false) ||
            f.Title.Contains("противореч", StringComparison.OrdinalIgnoreCase) ||
            f.Title.Contains("согласован", StringComparison.OrdinalIgnoreCase) ||
            f.Title.Contains("типов", StringComparison.OrdinalIgnoreCase) ||
            f.Title.Contains("HTTP", StringComparison.OrdinalIgnoreCase) ||
            f.Title.Contains("статус", StringComparison.OrdinalIgnoreCase)).ToList();
        var completenessFindings = report.Findings.Where(f =>
            f.Agents.Contains("distributed", StringComparer.OrdinalIgnoreCase) ||
            f.Agents.Contains("compliance", StringComparer.OrdinalIgnoreCase) ||
            (f.StandardRef?.Contains("Completeness", StringComparison.OrdinalIgnoreCase) ?? false) ||
            f.Title.Contains("отсутств", StringComparison.OrdinalIgnoreCase) ||
            f.Title.Contains("заглуш", StringComparison.OrdinalIgnoreCase) ||
            f.Title.Contains("TBD", StringComparison.OrdinalIgnoreCase)).ToList();
        var verifiabilityFindings = report.Findings.Where(f =>
            f.Agents.Contains("qa", StringComparer.OrdinalIgnoreCase) ||
            (f.StandardRef?.Contains("Unambiguous", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (f.StandardRef?.Contains("Verifiable", StringComparison.OrdinalIgnoreCase) ?? false) ||
            f.Title.Contains("размыт", StringComparison.OrdinalIgnoreCase) ||
            f.Title.Contains("NFR", StringComparison.OrdinalIgnoreCase) ||
            f.Title.Contains("тестирован", StringComparison.OrdinalIgnoreCase)).ToList();

        static string FormatRefs(List<Finding> list) => list.Count == 0
            ? "критичных системных расхождений не выявлено."
            : $"выявлено {list.Count} замечаний — " + string.Join("; ", list.Select(f => $"{f.Id} (`{f.SectionId}`, {f.Severity})")) + ".";

        sb.AppendLine($"- **Согласованность набора (ISO/IEC/IEEE 29148 п. 5.2.6 / IEEE 830 п. 4.3.4):** {FormatRefs(consistencyFindings)}");
        sb.AppendLine($"- **Полнота и интеграционная готовность (ISO/IEC/IEEE 29148 п. 5.2.6 / IEEE 830 п. 4.3.3):** {FormatRefs(completenessFindings)}");
        sb.AppendLine($"- **Проверяемость и однозначность формулировок (ISO/IEC/IEEE 29148 п. 5.2.5 / IEEE 830 п. 4.3.2):** {FormatRefs(verifiabilityFindings)}");
        sb.AppendLine($"- **Покрытие автоисправлениями:** сгенерировано {report.Patches.Count} патчей для {patchedSections.Count} проблемных разделов (из {findingsBySection.Count} разделов с замечаниями).");
        sb.AppendLine();

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
    public static string RenderPatchedDocumentMarkdown(TzDocument doc, AuditReport report, bool includeGherkin = false)
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
            var isDupTitle = string.Equals(s.Title.Trim(), doc.Title.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!isDupTitle)
            {
                sb.AppendLine($"## {s.Id} {s.Title}".TrimEnd());
                sb.AppendLine();
            }
            if (patchBySection.TryGetValue(s.Id, out var patch) && !string.IsNullOrWhiteSpace(patch.After))
            {
                sb.AppendLine(patch.After.Trim());
                if (includeGherkin && !string.IsNullOrWhiteSpace(patch.Gherkin))
                {
                    sb.AppendLine();
                    sb.AppendLine("### Критерии приёмки (Gherkin)");
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

    private async Task<string> SearchKbAsync(string query, CancellationToken ct) =>
        string.Join("\n\n", (await kb.SearchAsync(query, ct: ct)).Select(s => $"[{s.Source} › {s.Heading}]\n{s.Text}")) is { Length: > 0 } r
            ? r : "(knowledge base: nothing relevant)";

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string FenceFor(string content)
    {
        var maxRun = 0;
        var run = 0;
        foreach (var ch in content)
        {
            if (ch == '`')
            {
                run++;
                if (run > maxRun) maxRun = run;
            }
            else
            {
                run = 0;
            }
        }
        return new string('`', Math.Max(3, maxRun + 1));
    }
}

public sealed class ToolError(string message) : Exception(message);
