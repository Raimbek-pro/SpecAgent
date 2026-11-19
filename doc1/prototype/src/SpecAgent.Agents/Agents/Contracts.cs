using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Agents;

// Output contracts of every LLM agent. These records ARE the JSON schemas sent to Claude
// (see StrictJsonSchema): changing a field here changes what the model must return.

public sealed record SupervisorOutput
{
    public string Domain { get; init; } = "";
    public string Complexity { get; init; } = "medium";
    public List<string> Critics { get; init; } = [];
    public List<SectionRoute> Routes { get; init; } = [];
    public string Rationale { get; init; } = "";
}

public sealed record SectionRoute
{
    public string Critic { get; init; } = "";
    public List<string> SectionIds { get; init; } = [];
}

public sealed record CriticOutput
{
    public List<CriticFinding> Findings { get; init; } = [];
}

public sealed record CriticFinding
{
    public string SectionId { get; init; } = "";
    public string Quote { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public Severity Severity { get; init; }
    public string Recommendation { get; init; } = "";
    public string StandardRef { get; init; } = "";
}

public sealed record JudgeOutput
{
    public List<JudgeVerdict> Verdicts { get; init; } = [];
}

public sealed record JudgeVerdict
{
    public List<string> RawIds { get; init; } = [];
    public bool Accept { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public Severity Severity { get; init; }
    public string Recommendation { get; init; } = "";
    public string Rationale { get; init; } = "";
}

public sealed record PatchDraft
{
    public string After { get; init; } = "";
    public string Gherkin { get; init; } = "";
}

public sealed record VerifierOutput
{
    public bool Ok { get; init; }
    public List<string> Issues { get; init; } = [];
}

public sealed record AskOutput
{
    public string Answer { get; init; } = "";
    public List<CriticFinding> Findings { get; init; } = [];
}

/// <summary>Supervisor decision as stored in the log.</summary>
public sealed record RoutingDecision(
    string Domain,
    string Complexity,
    Dictionary<string, List<string>> Routes,
    string Rationale,
    bool ForcedByRules);
