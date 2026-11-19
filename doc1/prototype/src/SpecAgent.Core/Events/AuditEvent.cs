using System.Text.Json;

namespace SpecAgent.Core.Events;

/// <summary>
/// One entry of the append-only session log. The log IS the state of an audit:
/// any worker can be killed at any time and a fresh one resumes by replaying it.
/// Payloads only ever contain masked text — real PII lives in the vault.
/// </summary>
public sealed record AuditEvent(
    long Seq,
    string SessionId,
    string Type,
    string Agent,
    DateTimeOffset At,
    string Message,
    JsonElement? Payload);

public static class EventTypes
{
    public const string TzIngested = "TZ_INGESTED";
    public const string RunStarted = "RUN_STARTED";
    public const string Resumed = "RESUMED";
    public const string Routed = "ROUTED";
    public const string CriticStarted = "CRITIC_STARTED";
    public const string ToolCalled = "TOOL_CALLED";
    public const string Findings = "FINDINGS";
    public const string CriticFailed = "CRITIC_FAILED";
    public const string Grounded = "GROUNDED";
    public const string Judged = "JUDGED";
    public const string PatchDrafted = "PATCH_DRAFTED";
    public const string PatchVerified = "PATCH_VERIFIED";
    public const string Patches = "PATCHES";
    public const string ReportReady = "REPORT_READY";
    public const string RunFailed = "RUN_FAILED";
    public const string HumanQuestion = "HUMAN_QUESTION";
    public const string AgentAnswer = "AGENT_ANSWER";

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string> { ReportReady, RunFailed };
}
