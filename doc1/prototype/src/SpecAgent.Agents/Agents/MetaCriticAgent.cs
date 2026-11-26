using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SpecAgent.Agents.Llm;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Agents;

public sealed record GroundingResult(IReadOnlyList<RawFinding> Grounded, IReadOnlyList<string> Ungrounded);

public sealed record JudgeResult(IReadOnlyList<Finding> Findings, IReadOnlyList<string> Rejected, int QualityScore, bool UsedLlm);

/// <summary>
/// Level 3. Turns the noisy union of critic outputs into a short, trustworthy list:
///   1. Ground (deterministic): the quote must exist verbatim in the document, else discard.
///   2. Judge (LLM): merge duplicates, reject false positives, resolve conflicts, set severity.
///   3. Score (deterministic): TZ Quality Score from the final severities.
/// Raw findings are never deleted — final findings link to them via MergedFrom.
/// </summary>
public sealed partial class MetaCriticAgent(ILlmClient llm, PromptLibrary prompts, ILogger<MetaCriticAgent> logger)
{
    public const string Name = "meta-critic";

    [GeneratedRegex(@"\s+")] private static partial Regex Ws();
    private static string Norm(string s) => Ws().Replace(s.Replace('ё', 'е').Replace('Ё', 'Е'), " ").Trim().ToLowerInvariant().Trim('«', '»', '"', '.', ' ');

    public static GroundingResult Ground(TzDocument doc, IEnumerable<RawFinding> raw)
    {
        var grounded = new List<RawFinding>();
        var rejected = new List<string>();
        foreach (var f in raw)
        {
            var q = Norm(f.Quote);
            if (q.Length < 4) { rejected.Add(f.Id); continue; }
            var section = doc.Find(f.SectionId);
            if (section is not null && Norm(section.Text + " " + section.Title).Contains(q)) { grounded.Add(f); continue; }
            // Right quote, wrong section id → fix the pointer instead of discarding.
            var actual = doc.Sections.FirstOrDefault(s => Norm(s.Text + " " + s.Title).Contains(q));
            if (actual is not null) grounded.Add(f with { SectionId = actual.Id });
            else rejected.Add(f.Id);
        }
        return new GroundingResult(grounded, rejected);
    }

    public async Task<JudgeResult> JudgeAsync(TzDocument doc, IReadOnlyList<RawFinding> grounded, CancellationToken ct)
    {
        if (grounded.Count == 0) return new JudgeResult([], [], 100, false);

        JudgeOutput output;
        try
        {
            var docIndex = DocumentIndex.Build(doc);
            output = await llm.CompleteJsonAsync<JudgeOutput>(new AgentCall
            {
                Agent = Name,
                System = prompts.System("meta-critic"),
                User = $"<document_index>\n{docIndex}\n</document_index>\n\n<document title=\"{doc.Title}\">\n{doc.ToPromptText()}\n</document>\n\n<raw_findings>\n" +
                       JsonSerializer.Serialize(grounded, EventJson.Options) + "\n</raw_findings>",
                MaxTokens = 32000,
            }, ct);
        }
        catch (LlmException ex)
        {
            logger.LogWarning(ex, "Meta-critic LLM failed; using deterministic merge");
            return DeterministicMerge(grounded);
        }

        var byId = grounded.ToDictionary(f => f.Id);
        var used = new HashSet<string>();
        var findings = new List<Finding>();
        var rejected = new List<string>();

        foreach (var v in output.Verdicts)
        {
            var ids = v.RawIds.Where(id => byId.ContainsKey(id) && used.Add(id)).ToList();
            if (ids.Count == 0) continue;
            if (!v.Accept) { rejected.AddRange(ids); continue; }
            var first = byId[ids[0]];
            findings.Add(new Finding
            {
                Id = $"F-{findings.Count + 1}",
                MergedFrom = ids,
                Agents = ids.Select(id => byId[id].Agent).Distinct().ToList(),
                SectionId = first.SectionId,
                Quote = first.Quote,
                Title = v.Title is { Length: > 0 } t ? t : first.Title,
                Description = v.Description is { Length: > 0 } d ? d : first.Description,
                Severity = v.Severity,
                Rationale = v.Rationale,
                Recommendation = v.Recommendation is { Length: > 0 } r ? r : first.Recommendation,
                StandardRef = ids.Select(id => byId[id].StandardRef).FirstOrDefault(s => s.Length > 0) ?? "",
            });
        }

        // Judge omitted a raw finding → keep it as-is. Silence is not a rejection.
        foreach (var f in grounded.Where(f => !used.Contains(f.Id)))
            findings.Add(FromRaw(f, $"F-{findings.Count + 1}", [f.Id], "Не рассмотрено арбитром — сохранено без изменений."));

        findings = Ordered(findings);
        return new JudgeResult(findings, rejected, Score(findings), true);
    }

    /// <summary>Offline fallback: merge findings with the same section + quote + category, keep max severity.</summary>
    public static JudgeResult DeterministicMerge(IReadOnlyList<RawFinding> grounded)
    {
        var findings = grounded
            .GroupBy(f => (f.SectionId, Norm(f.Quote), f.Category))
            .Select((g, i) =>
            {
                var top = g.OrderByDescending(f => f.SuggestedSeverity).First();
                return FromRaw(top, $"F-{i + 1}", g.Select(f => f.Id).ToList(),
                    g.Count() > 1 ? $"Объединено {g.Count()} замечания по одной цитате." : "Детерминированная проверка.") with
                {
                    Agents = g.Select(f => f.Agent).Distinct().ToList(),
                };
            })
            .ToList();
        findings = Ordered(findings);
        return new JudgeResult(findings, [], Score(findings), false);
    }

    private static List<Finding> Ordered(List<Finding> f) =>
        f.OrderByDescending(x => x.Severity).Select((x, i) => x with { Id = $"F-{i + 1}" }).ToList();

    private static Finding FromRaw(RawFinding f, string id, IReadOnlyList<string> mergedFrom, string rationale) => new()
    {
        Id = id, MergedFrom = mergedFrom, Agents = [f.Agent], SectionId = f.SectionId, Quote = f.Quote,
        Title = f.Title, Description = f.Description, Severity = f.SuggestedSeverity, Rationale = rationale,
        Recommendation = f.Recommendation, StandardRef = f.StandardRef,
    };

    /// <summary>TZ Quality Score 0–100. Deterministic so the same findings always give the same score.</summary>
    public static int Score(IEnumerable<Finding> findings) => Math.Clamp(100 - findings.Sum(f => f.Severity switch
    {
        Severity.Blocker => 15,
        Severity.Critical => 8,
        Severity.Major => 4,
        _ => 1,
    }), 0, 100);
}
