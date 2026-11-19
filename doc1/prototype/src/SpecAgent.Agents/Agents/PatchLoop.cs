using System.Text;
using Microsoft.Extensions.Logging;
using SpecAgent.Agents.Llm;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Agents;

public sealed class PatchOptions
{
    public Severity MinSeverity { get; set; } = Severity.Critical;
    public int MaxPatches { get; set; } = 8;
    public int MaxRounds { get; set; } = 2;
    public int Parallelism { get; set; } = 3;
}

/// <summary>
/// Level 4. Writer drafts a fix → Verifier checks it against neighbouring sections → if not ok,
/// the Writer gets the issues back. Capped rounds, so the loop always terminates.
/// </summary>
public sealed class PatchLoop(ILlmClient llm, PromptLibrary prompts, IEventStore events, ILogger<PatchLoop> logger)
{
    public const string Writer = "patch-writer";
    public const string Verifier = "patch-verifier";

    public async Task<IReadOnlyList<Patch>> RunAsync(string sessionId, TzDocument doc, IReadOnlyList<Finding> findings,
        PatchOptions options, CancellationToken ct)
    {
        var targets = findings.Where(f => f.Severity >= options.MinSeverity).Take(options.MaxPatches).ToList();
        using var gate = new SemaphoreSlim(options.Parallelism);
        var tasks = targets.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try { return await PatchOneAsync(sessionId, doc, f, options.MaxRounds, ct); }
            catch (LlmException ex)
            {
                logger.LogWarning(ex, "Patch for {Finding} failed", f.Id);
                await events.AppendAsync(sessionId, EventTypes.PatchVerified, Writer, $"{f.Id}: patch failed — {ex.Message}", ct: ct);
                return null;
            }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks)).OfType<Patch>().ToList();
    }

    private async Task<Patch?> PatchOneAsync(string sessionId, TzDocument doc, Finding f, int maxRounds, CancellationToken ct)
    {
        var section = doc.Find(f.SectionId);
        if (section is null) return null;

        var idx = doc.Sections.ToList().IndexOf(section);
        var neighbours = doc.Sections.Skip(Math.Max(0, idx - 1)).Take(3).Where(s => s != section).Select(s => s.Id).ToList();

        var issues = new List<string>();
        PatchDraft draft = new();
        VerifierOutput verdict = new() { Ok = false };
        int round = 0;

        while (round < maxRounds)
        {
            round++;
            var writerInput = new StringBuilder()
                .AppendLine(Describe(f))
                .AppendLine(doc.ToPromptText([section.Id]));
            if (issues.Count > 0)
                writerInput.AppendLine("<verifier_issues>").AppendJoin('\n', issues.Select(i => "- " + i)).AppendLine().AppendLine("</verifier_issues>");

            draft = await llm.CompleteJsonAsync<PatchDraft>(new AgentCall
            {
                Agent = Writer, System = prompts.System("patch-writer"), User = writerInput.ToString(),
            }, ct);
            await events.AppendAsync(sessionId, EventTypes.PatchDrafted, Writer, $"{f.Id} round {round}: draft for section {section.Id}", ct: ct);

            // Deterministic pre-check before spending a verifier call.
            if (string.IsNullOrWhiteSpace(draft.After) || draft.After.Trim() == section.Text.Trim())
            {
                issues = ["Патч пустой или не отличается от исходного текста раздела."];
                continue;
            }

            verdict = await llm.CompleteJsonAsync<VerifierOutput>(new AgentCall
            {
                Agent = Verifier,
                System = prompts.System("patch-verifier"),
                User = $"{Describe(f)}\n<original>\n{section.Text}\n</original>\n<proposed>\n{draft.After}\n</proposed>\n" +
                       $"<neighbours>\n{doc.ToPromptText(neighbours)}\n</neighbours>",
                MaxTokens = 8000,
            }, ct);
            await events.AppendAsync(sessionId, EventTypes.PatchVerified, Verifier,
                verdict.Ok ? $"{f.Id} round {round}: ok" : $"{f.Id} round {round}: {verdict.Issues.Count} issue(s)", verdict, ct);

            if (verdict.Ok) break;
            issues = verdict.Issues;
        }

        return new Patch
        {
            FindingId = f.Id, SectionId = section.Id, Before = section.Text, After = draft.After,
            Gherkin = draft.Gherkin, Verified = verdict.Ok, Rounds = round, VerifierNotes = verdict.Issues,
        };
    }

    private static string Describe(Finding f) =>
        $"<defect id=\"{f.Id}\" severity=\"{f.Severity}\" section=\"{f.SectionId}\">\n{f.Title}\n{f.Description}\n" +
        $"Цитата: «{f.Quote}»\nРекомендация: {f.Recommendation}\n</defect>";
}
