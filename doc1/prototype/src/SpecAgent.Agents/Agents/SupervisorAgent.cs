using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SpecAgent.Agents.Llm;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Agents;

/// <summary>
/// Level 1. Classifies the ТЗ and decides which critics read which sections.
/// An LLM proposes; a deterministic floor makes sure money/PII documents always get the
/// compliance and distributed-systems critics, whatever the model says.
/// </summary>
public sealed partial class SupervisorAgent(ILlmClient llm, PromptLibrary prompts, ILogger<SupervisorAgent> logger)
{
    public const string Name = "supervisor";

    [GeneratedRegex(@"(?i)плат[её]ж|перевод|карт[аыу]|сч[её]т|списан|баланс|кредит|payment|transfer|card|PAN|IBAN|ИИН|\[(IIN|PAN|IBAN)_\d+\]")]
    private static partial Regex MoneyOrPii();

    public async Task<RoutingDecision> RouteAsync(TzDocument doc, CancellationToken ct)
    {
        var allIds = doc.Sections.Select(s => s.Id).ToList();
        var mustHaveRisk = MoneyOrPii().IsMatch(doc.ToPromptText());

        SupervisorOutput? output = null;
        try
        {
            output = await llm.CompleteJsonAsync<SupervisorOutput>(new AgentCall
            {
                Agent = Name,
                System = prompts.System("supervisor"),
                User = Outline(doc),
                MaxTokens = 8000,
            }, ct);
        }
        catch (LlmException ex)
        {
            logger.LogWarning(ex, "Supervisor LLM failed; falling back to deterministic routing");
        }

        var routes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in output?.Routes ?? [])
        {
            if (!CriticNames.AllLlm.Contains(r.Critic)) continue;
            var ids = r.SectionIds.Where(id => doc.Find(id) is not null).ToList();
            routes[r.Critic] = ids.Count > 0 ? ids : allIds;
        }
        foreach (var c in output?.Critics ?? [])
            if (CriticNames.AllLlm.Contains(c)) routes.TryAdd(c, allIds);

        // Deterministic floor.
        var forced = false;
        foreach (var c in new[] { CriticNames.Logic, CriticNames.Qa })
            forced |= routes.TryAdd(c, allIds);
        if (mustHaveRisk)
            foreach (var c in new[] { CriticNames.Compliance, CriticNames.Distributed })
                forced |= routes.TryAdd(c, allIds);

        return new RoutingDecision(
            Domain: output?.Domain is { Length: > 0 } d ? d : (mustHaveRisk ? "финансовый сервис" : "не определён"),
            Complexity: output?.Complexity ?? "medium",
            Routes: routes,
            Rationale: output?.Rationale ?? "LLM unavailable — all critics on all sections.",
            ForcedByRules: forced);
    }

    /// <summary>Table of contents + first lines: enough to route, cheap to send.</summary>
    private static string Outline(TzDocument doc)
    {
        var sb = new StringBuilder($"Document: {doc.Title}\nSections:\n");
        foreach (var s in doc.Sections)
        {
            var preview = s.Text.Length > 300 ? s.Text[..300] + "…" : s.Text;
            sb.AppendLine($"<section id=\"{s.Id}\" title=\"{s.Title}\">\n{preview}\n</section>");
        }
        return sb.ToString();
    }
}
