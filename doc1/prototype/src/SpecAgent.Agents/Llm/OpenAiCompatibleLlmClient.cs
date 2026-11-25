#pragma warning disable OPENAI001
using System.ClientModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace SpecAgent.Agents.Llm;

/// <summary>
/// Universal OpenAI-compatible LLM client for Kimi (Moonshot AI), Google Gemini, and Qwen (Model Studio / vLLM).
/// Uses StrictJsonSchema for guaranteed structured outputs, configurable parallel concurrency, and retry/fallback.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly IConfiguration _config;
    private readonly IOptions<ModelRoutingOptions> _routing;
    private readonly ILogger<OpenAiCompatibleLlmClient> _logger;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly object _paceLock = new();
    private DateTime _lastCallUtc = DateTime.MinValue;

    public OpenAiCompatibleLlmClient(
        IConfiguration config,
        IOptions<ModelRoutingOptions> routing,
        ILogger<OpenAiCompatibleLlmClient> logger)
    {
        _config = config;
        _routing = routing;
        _logger = logger;
        var maxConcurrency = int.TryParse(config["Llm:MaxConcurrency"], out var c) && c > 0 ? c : 3;
        _concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async Task<T> CompleteJsonAsync<T>(AgentCall call, CancellationToken ct = default) where T : class
    {
        var route = _routing.Value.For(call.Agent);
        var endpoint = _config["Llm:Endpoint"] ?? "https://api.moonshot.ai/v1";
        var apiKeyEnv = _config["Llm:ApiKeyEnv"] ?? "MOONSHOT_API_KEY";
        var apiKey = Environment.GetEnvironmentVariable(apiKeyEnv)
                     ?? Environment.GetEnvironmentVariable("MOONSHOT_API_KEY")
                     ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                     ?? Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY")
                     ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                     ?? throw new LlmException($"API key not found. Set {apiKeyEnv} in .env or environment.");

        var minDelayMs = int.TryParse(_config["Llm:MinDelayMs"], out var d) ? d : 200;
        var fallbackModel = _config["Llm:FallbackModel"];
        var isMoonshot = endpoint.Contains("moonshot", StringComparison.OrdinalIgnoreCase);

        var modelsToTry = string.IsNullOrWhiteSpace(fallbackModel) || fallbackModel == route.Model
            ? new[] { route.Model }
            : new[] { route.Model, fallbackModel };

        Exception? lastError = null;

        foreach (var model in modelsToTry)
        {
            const int maxRetries = 3;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                await _concurrencyGate.WaitAsync(ct);
                try
                {
                    await PaceStartAsync(minDelayMs, ct);

                    var started = DateTime.UtcNow;
                    var chat = new ChatClient(
                        model: model,
                        credential: new ApiKeyCredential(apiKey),
                        options: new OpenAIClientOptions
                        {
                            Endpoint = new Uri(endpoint),
                            NetworkTimeout = TimeSpan.FromMinutes(4),
                        });

                    var options = new ChatCompletionOptions
                    {
                        MaxOutputTokenCount = call.MaxTokens,
                        ReasoningEffortLevel = ParseReasoningEffort(route.Effort, isMoonshot),
                        ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                            jsonSchemaFormatName: typeof(T).Name,
                            jsonSchema: BinaryData.FromObjectAsJson(StrictJsonSchema.For<T>()),
                            jsonSchemaIsStrict: true),
                    };

                    _logger.LogInformation("Calling {Agent} -> {Model} (effort={Effort})...", call.Agent, model, route.Effort);

                    List<ChatMessage> messages =
                    [
                        ChatMessage.CreateSystemMessage(call.System),
                        ChatMessage.CreateUserMessage(call.User),
                    ];

                    ClientResult<ChatCompletion> result = await chat.CompleteChatAsync(messages, options, ct);
                    var completion = result.Value;

                    if (completion.FinishReason == ChatFinishReason.Length)
                        throw new LlmException($"{call.Agent}: output truncated at max_tokens={call.MaxTokens}");

                    var text = completion.Content.FirstOrDefault()?.Text ?? "";
                    var usage = completion.Usage;
                    _logger.LogInformation("{Agent} -> {Model} in {Ms} ms, in={In} out={Out}",
                        call.Agent, model, (int)(DateTime.UtcNow - started).TotalMilliseconds,
                        usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0);

                    return StrictJsonSchema.Deserialize<T>(text);
                }
                catch (ClientResultException ex) when (ex.Status == 429 && ex.Message.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                {
                    lastError = ex;
                    _logger.LogWarning("{Agent} hit daily quota on {Model}; switching to fallback immediately.", call.Agent, model);
                    break;
                }
                catch (ClientResultException ex) when (ex.Status is 429 or 503 or 502 or 500 && attempt < maxRetries)
                {
                    lastError = ex;
                    var backoffMs = 10000 * attempt;
                    _logger.LogWarning("{Agent} hit HTTP {Status} on {Model} (attempt {Attempt}/{Max}); retrying in {Delay} ms",
                        call.Agent, ex.Status, model, attempt, maxRetries, backoffMs);
                    await Task.Delay(backoffMs, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    lastError = ex;
                    _logger.LogWarning("{Agent} failed on {Model}: {Message}", call.Agent, model, ex.Message);
                    break;
                }
                finally
                {
                    _concurrencyGate.Release();
                }
            }
        }

        throw new LlmException($"{call.Agent}: LLM call failed across all models: {lastError?.Message}", lastError!);
    }

    private async Task PaceStartAsync(int minDelayMs, CancellationToken ct)
    {
        if (minDelayMs <= 0) return;
        int waitMs;
        lock (_paceLock)
        {
            var elapsed = (DateTime.UtcNow - _lastCallUtc).TotalMilliseconds;
            waitMs = elapsed < minDelayMs ? (int)(minDelayMs - elapsed) : 0;
            _lastCallUtc = DateTime.UtcNow.AddMilliseconds(waitMs);
        }
        if (waitMs > 0) await Task.Delay(waitMs, ct);
    }

    private static ChatReasoningEffortLevel? ParseReasoningEffort(string effort, bool isMoonshot)
    {
        var norm = effort.ToLowerInvariant();
        if (isMoonshot)
        {
            // Kimi K3 valid efforts: low | high | max
            return norm switch
            {
                "high" => ChatReasoningEffortLevel.High,
                "max" => new ChatReasoningEffortLevel("max"),
                _ => ChatReasoningEffortLevel.Low,
            };
        }

        return norm switch
        {
            "minimal" => new ChatReasoningEffortLevel("minimal"),
            "low" => ChatReasoningEffortLevel.Low,
            "medium" => ChatReasoningEffortLevel.Medium,
            "high" => ChatReasoningEffortLevel.High,
            _ => ChatReasoningEffortLevel.Low,
        };
    }
}
