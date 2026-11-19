namespace SpecAgent.Agents.Llm;

/// <summary>
/// Model routing: strong reasoning models for routing/judging/verification, cheaper ones for
/// routine checks. Bound from "ModelRouting" in appsettings.json — change models without code.
/// </summary>
public sealed class ModelRoutingOptions
{
    public const string Section = "ModelRouting";

    public AgentModel Default { get; set; } = new() { Model = "claude-opus-5-5", Effort = "medium" };
    public Dictionary<string, AgentModel> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AgentModel For(string agent) => Agents.TryGetValue(agent, out var m) ? m : Default;
}

public sealed class AgentModel
{
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>low | medium | high | xhigh | max</summary>
    public string Effort { get; set; } = "medium";
}
