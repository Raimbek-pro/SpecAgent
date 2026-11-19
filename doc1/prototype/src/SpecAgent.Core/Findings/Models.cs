using System.Text.Json.Serialization;

namespace SpecAgent.Core.Findings;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity { Minor = 0, Major = 1, Critical = 2, Blocker = 3 }

/// <summary>Names of the domain critics (level 2). Used by the Supervisor for routing.</summary>
public static class CriticNames
{
    public const string Logic = "logic";
    public const string Qa = "qa";
    public const string Compliance = "compliance";
    public const string Distributed = "distributed";
    public const string Rules = "rules"; // deterministic, no LLM

    public static readonly IReadOnlyList<string> AllLlm = [Logic, Qa, Compliance, Distributed];
}

/// <summary>A finding exactly as one critic reported it. Never mutated, never deleted.</summary>
public sealed record RawFinding
{
    public required string Id { get; init; }            // e.g. "distributed-3"
    public required string Agent { get; init; }
    public required string SectionId { get; init; }
    public required string Quote { get; init; }         // verbatim span from the ТЗ (masked)
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required Severity SuggestedSeverity { get; init; }
    public string Recommendation { get; init; } = "";
    public string StandardRef { get; init; } = "";      // "PCI DSS 3.4", "НБРК …", ""
}

/// <summary>A finding after the Meta-Critic: merged, grounded, scored.</summary>
public sealed record Finding
{
    public required string Id { get; init; }            // "F-1"
    public required IReadOnlyList<string> MergedFrom { get; init; } // RawFinding ids — links, not copies
    public required IReadOnlyList<string> Agents { get; init; }
    public required string SectionId { get; init; }
    public required string Quote { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required Severity Severity { get; init; }
    public required string Rationale { get; init; }     // why the judge kept it / chose this severity
    public string Recommendation { get; init; } = "";
    public string StandardRef { get; init; } = "";
}

public sealed record Patch
{
    public required string FindingId { get; init; }
    public required string SectionId { get; init; }
    public required string Before { get; init; }
    public required string After { get; init; }
    public string Gherkin { get; init; } = "";
    public required bool Verified { get; init; }
    public required int Rounds { get; init; }
    public IReadOnlyList<string> VerifierNotes { get; init; } = [];
}

public sealed record AuditReport
{
    public required string SessionId { get; init; }
    public required string Domain { get; init; }
    public required int QualityScore { get; init; }      // 0–100
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<Patch> Patches { get; init; }
    public required IReadOnlyList<string> RejectedRawFindingIds { get; init; }
}
