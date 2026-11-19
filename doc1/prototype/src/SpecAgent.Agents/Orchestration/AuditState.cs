using SpecAgent.Agents.Agents;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Orchestration;

public sealed record IngestedPayload(TzDocument Document, int PiiTokens);
public sealed record FindingsPayload(string Critic, IReadOnlyList<RawFinding> Findings);
public sealed record GroundedPayload(IReadOnlyList<string> Grounded, IReadOnlyList<string> Ungrounded);
public sealed record PatchesPayload(IReadOnlyList<Patch> Patches);

/// <summary>
/// Everything the orchestrator knows, rebuilt purely from the log. No in-memory state survives
/// between calls — that's what makes wake(sessionId) work after a crash.
/// </summary>
public sealed class AuditState
{
    public TzDocument? Document { get; private set; }
    public bool Started { get; private set; }
    /// <summary>REPORT_READY seen. RUN_FAILED is not "finished": a manual resume may retry it.</summary>
    public bool Finished { get; private set; }
    public bool Failed { get; private set; }
    public RoutingDecision? Routing { get; private set; }
    public Dictionary<string, IReadOnlyList<RawFinding>> CriticFindings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailedCritics { get; } = new(StringComparer.OrdinalIgnoreCase);
    public JudgeResult? Judged { get; private set; }
    public IReadOnlyList<Patch>? Patches { get; private set; }
    public long LastSeq { get; private set; }

    public static AuditState From(IEnumerable<AuditEvent> events)
    {
        var s = new AuditState();
        foreach (var e in events)
        {
            s.LastSeq = e.Seq;
            switch (e.Type)
            {
                case EventTypes.TzIngested: s.Document = e.Get<IngestedPayload>().Document; break;
                case EventTypes.RunStarted: s.Started = true; break;
                case EventTypes.Routed: s.Routing = e.Get<RoutingDecision>(); break;
                case EventTypes.Findings:
                    var p = e.Get<FindingsPayload>();
                    s.CriticFindings[p.Critic] = p.Findings;
                    s.FailedCritics.Remove(p.Critic);
                    break;
                case EventTypes.CriticFailed: s.FailedCritics.Add(e.Agent); break;
                case EventTypes.Judged: s.Judged = e.Get<JudgeResult>(); break;
                case EventTypes.Patches: s.Patches = e.Get<PatchesPayload>().Patches; break;
                case EventTypes.ReportReady: s.Finished = true; break;
                case EventTypes.RunFailed: s.Failed = true; break;
                case EventTypes.Resumed: s.Failed = false; break;
            }
        }
        return s;
    }
}
