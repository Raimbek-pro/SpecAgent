using System.Text;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpecAgent.Agents.Telemetry;

namespace SpecAgent.Agents.Llm;

/// <summary>
/// Claude via the official Anthropic C# SDK. Every agent call uses structured outputs, so the
/// response is guaranteed to match the agent's C# contract. Server-side refusal fallbacks are on
/// for models that support them (Opus 5.5 / Sonnet 5.5): a declined request is re-served by a
/// fallback model inside the same call instead of failing the audit.
/// </summary>
public sealed class ClaudeLlmClient(
    AnthropicClient client,
    IOptions<ModelRoutingOptions> routing,
    ILogger<ClaudeLlmClient> logger,
    ISessionUsageStore? usageStore = null,
    SpecAgentMetrics? metrics = null) : ILlmClient
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    public async Task<T> CompleteJsonAsync<T>(AgentCall call, CancellationToken ct = default) where T : class
    {
        var route = routing.Value.For(call.Agent);
        var useFallback = SupportsServerSideFallback(route.Model);

        var parameters = new MessageCreateParams
        {
            Model = route.Model,
            MaxTokens = call.MaxTokens,
            System = call.System,
            Messages = [new BetaUserMessageParam { Content = call.User }],
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(route.Effort),
                Format = new BetaJsonOutputFormat { Schema = StrictJsonSchema.For<T>() },
            },
            Betas = useFallback ? [FallbackBeta] : null,
            Fallbacks = useFallback ? (BetaFallbacksParam)new Default() : null,
        };

        var started = DateTime.UtcNow;
        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LlmException($"{call.Agent}: Claude API call failed ({route.Model}): {ex.Message}", ex);
        }

        var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        var inTok = response.Usage.InputTokens;
        var outTok = response.Usage.OutputTokens;
        var costUsd = ModelPricing.EstimateCostUsd(route.Model, inTok, outTok);

        logger.LogInformation("{Agent} → {Model} in {Ms} ms, in={In} out={Out} cost=${Cost:F6} stop={Stop}",
            call.Agent, route.Model, elapsedMs, inTok, outTok, costUsd, response.StopReason);

        metrics?.RecordLlmCall(call.Agent, route.Model, success: response.StopReason != "refusal", inTok, outTok, costUsd, elapsedMs);
        if (usageStore is not null && AuditTelemetryContext.CurrentSessionId is { } sid)
            await usageStore.RecordUsageAsync(sid, call.Agent, route.Model, inTok, outTok, costUsd, elapsedMs, ct);

        if (response.StopReason == "refusal")
            throw new LlmException($"{call.Agent}: model declined the request ({response.StopDetails?.Category})");
        if (response.StopReason == "max_tokens")
            throw new LlmException($"{call.Agent}: output hit max_tokens={call.MaxTokens}; JSON is truncated");

        var text = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out var t)) text.Append(t.Text);

        return StrictJsonSchema.Deserialize<T>(text.ToString());
    }

    private static bool SupportsServerSideFallback(string model) =>
        model.StartsWith("claude-opus-5-5") || model.StartsWith("claude-sonnet-5-5") || model.StartsWith("claude-fable-5");

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
