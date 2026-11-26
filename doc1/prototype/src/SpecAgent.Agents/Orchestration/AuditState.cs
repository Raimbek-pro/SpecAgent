using System.Text.RegularExpressions;
using SpecAgent.Agents.Agents;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Orchestration;

public sealed record IngestedPayload(TzDocument Document, int PiiTokens);
public sealed record FindingsPayload(string Critic, IReadOnlyList<RawFinding> Findings);
public sealed record GroundedPayload(IReadOnlyList<string> Grounded, IReadOnlyList<string> Ungrounded);
public sealed record PatchesPayload(IReadOnlyList<Patch> Patches);
public sealed record ChatMessagePayload(string SectionId, string Agent, string Text);
public sealed record ChatMessage(long Seq, DateTimeOffset At, string Role, string Agent, string SectionId, string Text);

/// <summary>
/// Everything the orchestrator knows, rebuilt purely from the log. No in-memory state survives
/// between calls — that's what makes wake(sessionId) work after a crash.
/// </summary>
public sealed partial class AuditState
{
    [GeneratedRegex(@"^@(?<agent>\w+)\s+\[(?<sec>[^\]]+)\]\s*(?<q>.*)$", RegexOptions.Singleline)]
    private static partial Regex LegacyQuestionFormat();

    public TzDocument? Document { get; private set; }
    public bool Started { get; private set; }
    /// <summary>REPORT_READY seen. RUN_FAILED / RUN_CANCELLED are not "finished": a manual resume may retry them.</summary>
    public bool Finished { get; private set; }
    public bool Failed { get; private set; }
    public bool Cancelled { get; private set; }
    public RoutingDecision? Routing { get; private set; }
    public Dictionary<string, IReadOnlyList<RawFinding>> CriticFindings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailedCritics { get; } = new(StringComparer.OrdinalIgnoreCase);
    public JudgeResult? Judged { get; private set; }
    public IReadOnlyList<Patch>? Patches { get; private set; }
    public Dictionary<string, List<ChatMessage>> ChatsBySection { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long LastSeq { get; private set; }

    public static AuditState From(IEnumerable<AuditEvent> events)
    {
        var s = new AuditState();
        string? lastQuestionSection = null;
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
                case EventTypes.ReportReady: s.Finished = true; s.Cancelled = false; s.Failed = false; break;
                case EventTypes.RunFailed: s.Failed = true; s.Cancelled = false; break;
                case EventTypes.RunCancelled: s.Cancelled = true; s.Failed = false; break;
                case EventTypes.Resumed: s.Failed = false; s.Cancelled = false; break;
                case EventTypes.HumanQuestion:
                {
                    ChatMessage? msg = null;
                    if (e.Payload is { } pl && pl.TryGetProperty("sectionId", out _))
                    {
                        var cp = e.Get<ChatMessagePayload>();
                        msg = new ChatMessage(e.Seq, e.At, "user", cp.Agent, cp.SectionId, cp.Text);
                    }
                    else if (LegacyQuestionFormat().Match(e.Message) is { Success: true } m)
                    {
                        msg = new ChatMessage(e.Seq, e.At, "user", m.Groups["agent"].Value, m.Groups["sec"].Value, m.Groups["q"].Value);
                    }
                    if (msg is not null)
                    {
                        lastQuestionSection = msg.SectionId;
                        s.AddChat(msg);
                    }
                    break;
                }
                case EventTypes.AgentAnswer:
                {
                    ChatMessage? msg = null;
                    if (e.Payload is { } pl && pl.TryGetProperty("sectionId", out _))
                    {
                        var cp = e.Get<ChatMessagePayload>();
                        msg = new ChatMessage(e.Seq, e.At, "agent", cp.Agent, cp.SectionId, cp.Text);
                    }
                    else if (!string.IsNullOrWhiteSpace(lastQuestionSection))
                    {
                        msg = new ChatMessage(e.Seq, e.At, "agent", e.Agent, lastQuestionSection, e.Message);
                    }
                    if (msg is not null) s.AddChat(msg);
                    break;
                }
            }
        }
        return s;
    }

    private void AddChat(ChatMessage msg)
    {
        if (!ChatsBySection.TryGetValue(msg.SectionId, out var list))
            ChatsBySection[msg.SectionId] = list = [];
        list.Add(msg);
    }
}
