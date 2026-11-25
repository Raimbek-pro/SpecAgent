using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SpecAgent.Agents.Knowledge;

public interface IEmbeddingClient
{
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Free-tier embedding client supporting Google Gemini (gemini-embedding-001) and local Ollama
/// (e.g. qwen3-embedding:0.6b). Returns null on error so KnowledgeBase falls back gracefully.
/// </summary>
public sealed class FreeEmbeddingClient(
    IConfiguration config,
    ILogger<FreeEmbeddingClient> logger) : IEmbeddingClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var ollamaEndpoint = config["Embeddings:OllamaEndpoint"];
        if (!string.IsNullOrWhiteSpace(ollamaEndpoint))
            return await EmbedOllamaAsync(ollamaEndpoint, config["Embeddings:Model"] ?? "qwen3-embedding:0.6b", text, ct);

        var apiKeyEnv = config["Llm:ApiKeyEnv"] ?? "GEMINI_API_KEY";
        var apiKey = Environment.GetEnvironmentVariable(apiKeyEnv) ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        return await EmbedGeminiAsync(apiKey, config["Embeddings:Model"] ?? "gemini-embedding-001", text, ct);
    }

    private async Task<float[]?> EmbedGeminiAsync(string apiKey, string model, string text, CancellationToken ct)
    {
        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:embedContent?key={Uri.EscapeDataString(apiKey)}";
            var payload = new { content = new { parts = new[] { new { text } } } };
            using var resp = await Http.PostAsJsonAsync(url, payload, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var doc = await resp.Content.ReadFromJsonAsync<GeminiEmbedResponse>(cancellationToken: ct);
            return doc?.Embedding?.Values;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Gemini embedding skipped: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<float[]?> EmbedOllamaAsync(string endpoint, string model, string text, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.PostAsJsonAsync(endpoint, new { model, input = text }, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var doc = await resp.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken: ct);
            return doc?.Data?.FirstOrDefault()?.Embedding;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Ollama embedding skipped: {Message}", ex.Message);
            return null;
        }
    }

    private sealed record GeminiEmbedResponse([property: JsonPropertyName("embedding")] GeminiEmbedding? Embedding);
    private sealed record GeminiEmbedding([property: JsonPropertyName("values")] float[]? Values);
    private sealed record OllamaEmbedResponse([property: JsonPropertyName("data")] List<OllamaDatum>? Data);
    private sealed record OllamaDatum([property: JsonPropertyName("embedding")] float[]? Embedding);
}
