using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace SpecAgent.Agents.Knowledge;

/// <summary>
/// Semantic vector knowledge base backed by Qdrant (when running) or local cached cosine similarity
/// over free embeddings (gemini-embedding-001 / Ollama), with BM25 keyword fallback.
/// </summary>
public sealed class SemanticKnowledgeBase : IKnowledgeBase
{
    private sealed record VectorChunk(string Id, string Source, string Heading, string Text, float[]? Vector);

    private readonly string _directory;
    private readonly string _cachePath;
    private readonly IEmbeddingClient _embeddings;
    private MarkdownKnowledgeBase _keywordFallback;
    private readonly QdrantClient? _qdrant;
    private readonly ILogger<SemanticKnowledgeBase> _logger;

    public string DirectoryPath => _directory;

    public async Task ReloadAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            _initialized = false;
            _chunks.Clear();
            _keywordFallback = new MarkdownKnowledgeBase(_directory);
        }
        finally
        {
            _initLock.Release();
        }
        await EnsureInitializedAsync();
    }

    private readonly List<VectorChunk> _chunks = [];
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _qdrantReady;
    private const string CollectionName = "specagent_kb";

    public SemanticKnowledgeBase(
        string directory,
        IEmbeddingClient embeddings,
        IConfiguration config,
        ILogger<SemanticKnowledgeBase> logger)
    {
        _directory = directory;
        _cachePath = Path.Combine(directory, ".embeddings-cache.json");
        _embeddings = embeddings;
        _keywordFallback = new MarkdownKnowledgeBase(directory);
        _logger = logger;

        var qdrantHost = config["Qdrant:Host"];
        if (!string.IsNullOrWhiteSpace(qdrantHost))
        {
            var port = int.TryParse(config["Qdrant:Port"], out var p) ? p : 6334;
            _qdrant = new QdrantClient(qdrantHost, port);
        }
    }

    public IReadOnlyList<KbSnippet> Search(string query, int top = 6) =>
        SearchAsync(query, top).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<KbSnippet>> SearchAsync(string query, int top)
    {
        await EnsureInitializedAsync();

        var qVec = await _embeddings.EmbedAsync(query);
        if (qVec is null)
            return _keywordFallback.Search(query, top);

        if (_qdrantReady && _qdrant is not null)
        {
            try
            {
                var hits = await _qdrant.SearchAsync(CollectionName, qVec, limit: (ulong)top);
                if (hits.Count > 0)
                {
                    return hits.Select(h => new KbSnippet(
                        h.Payload["source"].StringValue,
                        h.Payload["heading"].StringValue,
                        h.Payload["text"].StringValue,
                        Math.Round(h.Score, 3))).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Qdrant search failed, falling back to local vector store: {Message}", ex.Message);
            }
        }

        // Local in-memory cosine similarity over cached vectors.
        var scored = _chunks
            .Where(c => c.Vector is not null && c.Vector.Length == qVec.Length)
            .Select(c => (c, score: Cosine(qVec, c.Vector!)))
            .OrderByDescending(x => x.score)
            .Take(top)
            .Select(x => new KbSnippet(x.c.Source, x.c.Heading, x.c.Text, Math.Round(x.score, 3)))
            .ToList();

        return scored.Count > 0 ? scored : _keywordFallback.Search(query, top);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            var cache = LoadCache();
            var dirty = false;

            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*.md", SearchOption.AllDirectories))
                {
                    var heading = Path.GetFileNameWithoutExtension(file);
                    var buf = new List<string>();
                    async Task FlushAsync()
                    {
                        var text = string.Join('\n', buf).Trim();
                        buf.Clear();
                        if (text.Length == 0) return;

                        var hash = Sha256($"{file}:{heading}:{text}");
                        if (!cache.TryGetValue(hash, out var vec))
                        {
                            vec = await _embeddings.EmbedAsync($"{heading}\n{text}");
                            if (vec is not null)
                            {
                                cache[hash] = vec;
                                dirty = true;
                            }
                        }
                        _chunks.Add(new VectorChunk(hash, Path.GetFileName(file), heading, text, vec));
                    }

                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.StartsWith('#'))
                        {
                            await FlushAsync();
                            heading = line.TrimStart('#', ' ');
                        }
                        else buf.Add(line);
                    }
                    await FlushAsync();
                }
            }

            if (dirty) SaveCache(cache);
            await TrySyncQdrantAsync();
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task TrySyncQdrantAsync()
    {
        if (_qdrant is null) return;
        var vectorized = _chunks.Where(c => c.Vector is not null).ToList();
        if (vectorized.Count == 0) return;

        try
        {
            var dim = (ulong)vectorized[0].Vector!.Length;
            var exists = await _qdrant.CollectionExistsAsync(CollectionName);
            if (!exists)
                await _qdrant.CreateCollectionAsync(CollectionName, new VectorParams { Size = dim, Distance = Distance.Cosine });

            var points = vectorized.Select((c, i) => new PointStruct
            {
                Id = (ulong)(i + 1),
                Vectors = c.Vector!,
                Payload =
                {
                    ["source"] = c.Source,
                    ["heading"] = c.Heading,
                    ["text"] = c.Text,
                }
            }).ToList();

            await _qdrant.UpsertAsync(CollectionName, points);
            _qdrantReady = true;
            _logger.LogInformation("Synced {Count} knowledge chunks to Qdrant collection '{Collection}'", points.Count, CollectionName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Qdrant unavailable ({Message}); using local cached vector search", ex.Message);
        }
    }

    private Dictionary<string, float[]> LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
                return JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(_cachePath)) ?? new();
        }
        catch { /* ignore corrupt cache */ }
        return new();
    }

    private void SaveCache(Dictionary<string, float[]> cache)
    {
        try { File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache)); }
        catch { /* non-fatal */ }
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
}
