namespace SpecAgent.Agents.Llm;

/// <summary>One isolated LLM call made on behalf of one agent. No shared conversation between agents.</summary>
public sealed record AgentCall
{
    public required string Agent { get; init; }
    public required string System { get; init; }
    public required string User { get; init; }
    public int MaxTokens { get; init; } = 16000;
}

public interface ILlmClient
{
    /// <summary>Calls the model routed for <see cref="AgentCall.Agent"/> and returns schema-validated JSON as T.</summary>
    Task<T> CompleteJsonAsync<T>(AgentCall call, CancellationToken ct = default) where T : class;
}

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);
