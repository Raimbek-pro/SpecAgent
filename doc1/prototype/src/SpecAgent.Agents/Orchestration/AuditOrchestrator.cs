using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Tools;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Orchestration;

/// <summary>
/// The stateless "brain" harness. RunAsync(sessionId) is both start and wake/resume: it rebuilds
/// state from the log, skips every stage that already has its event, and continues from there.
/// Critics run in parallel (fan-out) and each writes its own FINDINGS event (fan-in via the log).
/// </summary>
public sealed class AuditOrchestrator(
    IEventStore events,
    SupervisorAgent supervisor,
    MetaCriticAgent metaCritic,
    PatchLoop patchLoop,
    ILlmClient llm,
    PromptLibrary prompts,
    ToolGateway tools,
    IOptions<PatchOptions> patchOptions,
    ILogger<AuditOrchestrator> logger,
    ChecklistAgent? checklist = null)
{
    public const string Name = "orchestrator";

    public async Task RunAsync(string sessionId, CancellationToken ct = default)
    {
        var state = AuditState.From(await events.ReadAsync(sessionId, ct: ct));
        if (state.Finished) return;
        var doc = state.Document ?? throw new InvalidOperationException($"Session {sessionId} has no TZ_INGESTED event");

        if (state.Started)
            await events.AppendAsync(sessionId, EventTypes.Resumed, Name, $"wake({sessionId}) from event {state.LastSeq}", ct: ct);
        else
            await events.AppendAsync(sessionId, EventTypes.RunStarted, Name, $"session {sessionId}: {doc.Sections.Count} sections", ct: ct);

        try
        {
            // ── Level 1: route ────────────────────────────────────────────────
            var routing = state.Routing;
            if (routing is null)
            {
                routing = await supervisor.RouteAsync(doc, ct);
                await events.AppendAsync(sessionId, EventTypes.Routed, SupervisorAgent.Name,
                    $"{routing.Domain} ({routing.Complexity}) → {string.Join(", ", routing.Routes.Keys)}" +
                    (routing.ForcedByRules ? " [rules added critics]" : ""), routing, ct);
            }

            // ── Level 2: critics fan out (only the ones without a FINDINGS event yet) ──
            if (!state.CriticFindings.ContainsKey(CriticNames.Rules))
            {
                var rules = RuleBasedCritic.Review(doc);
                await events.AppendAsync(sessionId, EventTypes.Findings, CriticNames.Rules,
                    $"{rules.Count} raw (deterministic)", new FindingsPayload(CriticNames.Rules, rules), ct);
                state.CriticFindings[CriticNames.Rules] = rules;
            }

            var pending = routing.Routes.Where(r => !state.CriticFindings.ContainsKey(r.Key)).ToList();
            var tasks = pending.Select(r => RunCriticAsync(sessionId, doc, r.Key, r.Value, ct)).ToList();
            if (checklist is not null && !state.CriticFindings.ContainsKey(ChecklistAgent.Name))
                tasks.Add(RunChecklistAsync(sessionId, doc, checklist, ct));

            var results = await Task.WhenAll(tasks);
            foreach (var (critic, findings) in results)
                if (findings is not null) state.CriticFindings[critic] = findings;

            // ── Level 3: ground + judge + score ──────────────────────────────
            var judged = state.Judged;
            if (judged is null)
            {
                var raw = state.CriticFindings.Values.SelectMany(f => f).ToList();
                var grounding = MetaCriticAgent.Ground(doc, raw);
                await events.AppendAsync(sessionId, EventTypes.Grounded, MetaCriticAgent.Name,
                    $"{grounding.Grounded.Count} of {raw.Count} quotes found in the document",
                    new GroundedPayload(grounding.Grounded.Select(f => f.Id).ToList(), grounding.Ungrounded), ct);

                judged = await metaCritic.JudgeAsync(doc, grounding.Grounded, ct);
                judged = judged with { Rejected = [.. judged.Rejected, .. grounding.Ungrounded] };
                var bySev = judged.Findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key).Select(g => $"{g.Count()} {g.Key}");
                await events.AppendAsync(sessionId, EventTypes.Judged, MetaCriticAgent.Name,
                    $"{raw.Count} → {judged.Findings.Count} findings ({string.Join(", ", bySev)}), score {judged.QualityScore}" +
                    (judged.UsedLlm ? "" : " [deterministic merge]"), judged, ct);
            }

            // ── Level 4: patch + verify ──────────────────────────────────────
            var patches = state.Patches;
            if (patches is null)
            {
                patches = await patchLoop.RunAsync(sessionId, doc, judged.Findings, patchOptions.Value, ct);
                await events.AppendAsync(sessionId, EventTypes.Patches, PatchLoop.Writer,
                    $"{patches.Count} patches, {patches.Count(p => p.Verified)} verified", new PatchesPayload(patches), ct);
            }

            var report = new AuditReport
            {
                SessionId = sessionId,
                Domain = routing.Domain,
                QualityScore = judged.QualityScore,
                Findings = judged.Findings,
                Patches = patches,
                RejectedRawFindingIds = judged.Rejected,
            };
            await events.AppendAsync(sessionId, EventTypes.ReportReady, Name,
                $"report ready: score {report.QualityScore}, {report.Findings.Count} findings", report, ct);
        }
        catch (OperationCanceledException) { throw; } // worker shutdown → session stays open → resumed later
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit {Session} failed", sessionId);
            await events.AppendAsync(sessionId, EventTypes.RunFailed, Name, ex.Message, ct: CancellationToken.None);
        }
    }

    private async Task<(string Critic, IReadOnlyList<RawFinding>? Findings)> RunCriticAsync(
        string sessionId, Core.Documents.TzDocument doc, string critic, List<string> sectionIds, CancellationToken ct)
    {
        var profile = CriticAgent.Profiles.First(p => p.Name == critic);
        var agent = new CriticAgent(profile, llm, prompts, tools);
        await events.AppendAsync(sessionId, EventTypes.CriticStarted, critic, $"reading {sectionIds.Count} section(s)", ct: ct);
        try
        {
            var findings = await agent.ReviewAsync(sessionId, doc, sectionIds, ct);
            await events.AppendAsync(sessionId, EventTypes.Findings, critic, $"{findings.Count} raw",
                new FindingsPayload(critic, findings), ct);
            return (critic, findings);
        }
        catch (LlmException ex)
        {
            // One critic failing must not kill the audit: others' findings are already in the log.
            await events.AppendAsync(sessionId, EventTypes.CriticFailed, critic, ex.Message, ct: ct);
            await events.AppendAsync(sessionId, EventTypes.Findings, critic, "0 raw (critic failed)",
                new FindingsPayload(critic, []), ct);
            return (critic, []);
        }
    }

    private async Task<(string Critic, IReadOnlyList<RawFinding>? Findings)> RunChecklistAsync(
        string sessionId, Core.Documents.TzDocument doc, ChecklistAgent agent, CancellationToken ct)
    {
        await events.AppendAsync(sessionId, EventTypes.CriticStarted, ChecklistAgent.Name, "batch checklist pass (12 rules)", ct: ct);
        try
        {
            var findings = await agent.ReviewAsync(doc, ct);
            await events.AppendAsync(sessionId, EventTypes.Findings, ChecklistAgent.Name, $"{findings.Count} raw",
                new FindingsPayload(ChecklistAgent.Name, findings), ct);
            return (ChecklistAgent.Name, findings);
        }
        catch (LlmException ex)
        {
            await events.AppendAsync(sessionId, EventTypes.CriticFailed, ChecklistAgent.Name, ex.Message, ct: ct);
            await events.AppendAsync(sessionId, EventTypes.Findings, ChecklistAgent.Name, "0 raw (checklist failed)",
                new FindingsPayload(ChecklistAgent.Name, []), ct);
            return (ChecklistAgent.Name, []);
        }
    }

    /// <summary>Human-in-the-loop: ask one critic about one section. Answer is appended to the same log.</summary>
    public async Task<AskOutput> AskAsync(string sessionId, string critic, string sectionId, string question, CancellationToken ct)
    {
        var state = AuditState.From(await events.ReadAsync(sessionId, ct: ct));
        var doc = state.Document ?? throw new InvalidOperationException("Unknown session");
        var profile = CriticAgent.Profiles.FirstOrDefault(p => p.Name.Equals(critic, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"Unknown agent '@{critic}'. Use one of: {string.Join(", ", CriticNames.AllLlm)}");

        await events.AppendAsync(sessionId, EventTypes.HumanQuestion, "analyst", $"@{critic} [{sectionId}] {question}", ct: ct);
        var secFindings = state.Judged?.Findings.Where(f => f.SectionId == sectionId).ToList();
        var secPatch = state.Patches?.LastOrDefault(p => p.SectionId == sectionId);
        var answer = await new CriticAgent(profile, llm, prompts, tools).AskAsync(doc, sectionId, question, ct, sessionId, secFindings, secPatch);
        await events.AppendAsync(sessionId, EventTypes.AgentAnswer, profile.Name, answer.Answer, answer, ct);
        return answer;
    }
}
