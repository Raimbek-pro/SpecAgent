using System.Text.RegularExpressions;
using SpecAgent.Agents.Knowledge;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;

namespace SpecAgent.Agents.Tools;

/// <summary>
/// The "hands": the single execute() door through which agents touch anything outside their own
/// context. Every call is written to the session log (TOOL_CALLED), tools are read-only, and no
/// credentials ever pass through here — so a prompt injection inside a ТЗ has nothing to steal.
/// </summary>
public sealed class ToolGateway(IEventStore events, IKnowledgeBase kb)
{
    public async Task<string> ExecuteAsync(string sessionId, string agent, string tool,
        IReadOnlyDictionary<string, string> args, TzDocument doc, CancellationToken ct = default)
    {
        string result;
        try
        {
            result = tool switch
            {
                "tz.read_span" => ReadSpan(doc, args["sectionId"]),
                "tz.search" => SearchTz(doc, args["query"]),
                "kb.search" => SearchKb(args["query"]),
                _ => throw new ToolError($"Unknown tool '{tool}'"),
            };
        }
        catch (Exception ex) when (ex is ToolError or KeyNotFoundException)
        {
            result = $"ToolError: {ex.Message}";
        }

        await events.AppendAsync(sessionId, EventTypes.ToolCalled, agent,
            $"{tool}({string.Join(", ", args.Select(a => $"{a.Key}={Trim(a.Value, 60)}"))}) → {result.Length} chars",
            new { tool, args }, ct);
        return result;
    }

    private static string ReadSpan(TzDocument doc, string sectionId) =>
        doc.Find(sectionId)?.Text ?? throw new ToolError($"Section '{sectionId}' not found");

    private static string SearchTz(TzDocument doc, string query)
    {
        var hits = doc.Sections
            .Where(s => Regex.IsMatch(s.Title + "\n" + s.Text, Regex.Escape(query), RegexOptions.IgnoreCase))
            .Select(s => $"[{s.Id}] {s.Title}");
        return string.Join('\n', hits) is { Length: > 0 } r ? r : "(no matches)";
    }

    private string SearchKb(string query) =>
        string.Join("\n\n", kb.Search(query).Select(s => $"[{s.Source} › {s.Heading}]\n{s.Text}")) is { Length: > 0 } r
            ? r : "(knowledge base: nothing relevant)";

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public sealed class ToolError(string message) : Exception(message);
