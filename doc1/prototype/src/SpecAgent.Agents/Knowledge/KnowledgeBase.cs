using System.Text.RegularExpressions;

namespace SpecAgent.Agents.Knowledge;

public sealed record KbSnippet(string Source, string Heading, string Text, double Score);

public interface IKnowledgeBase
{
    IReadOnlyList<KbSnippet> Search(string query, int top = 6);
}

/// <summary>
/// Keyword (BM25-lite) search over /knowledge/*.md, chunked by heading. Deterministic and offline —
/// good enough for a curated checklist of a few hundred items. Swap for Qdrant (embeddings) behind
/// the same interface when the corpus grows.
/// </summary>
public sealed partial class MarkdownKnowledgeBase : IKnowledgeBase
{
    private sealed record Chunk(string Source, string Heading, string Text, HashSet<string> Terms);

    private readonly List<Chunk> _chunks = [];

    [GeneratedRegex(@"[\p{L}\d]{3,}")] private static partial Regex Word();

    public MarkdownKnowledgeBase(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories))
        {
            string heading = Path.GetFileNameWithoutExtension(file);
            var buf = new List<string>();
            void Flush()
            {
                var text = string.Join('\n', buf).Trim();
                if (text.Length > 0) _chunks.Add(new Chunk(Path.GetFileName(file), heading, text, Terms(heading + " " + text)));
                buf.Clear();
            }
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith('#')) { Flush(); heading = line.TrimStart('#', ' '); }
                else buf.Add(line);
            }
            Flush();
        }
    }

    public int ChunkCount => _chunks.Count;

    public IReadOnlyList<KbSnippet> Search(string query, int top = 6)
    {
        var q = Terms(query);
        if (q.Count == 0) return [];
        return _chunks
            .Select(c => (c, score: q.Count(t => c.Terms.Contains(t)) / Math.Sqrt(c.Terms.Count + 1)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(top)
            .Select(x => new KbSnippet(x.c.Source, x.c.Heading, x.c.Text, Math.Round(x.score, 3)))
            .ToList();
    }

    // Crude stemming: lowercase + cut to 6 chars handles most Russian inflection ("платежа"/"платежи").
    private static HashSet<string> Terms(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value.Length > 6 ? m.Value[..6] : m.Value).ToHashSet();
}
