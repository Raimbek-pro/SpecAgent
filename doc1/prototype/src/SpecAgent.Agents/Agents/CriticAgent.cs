using System.Text;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Tools;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Agents;

/// <summary>Static description of one level-2 specialist.</summary>
public sealed record CriticProfile(string Name, string Prompt, string KbQuery);

/// <summary>
/// Level 2. One instance per specialist, each with its own prompt, its own slice of the ТЗ and
/// its own knowledge-base context. Critics never see each other's output — independence is what
/// makes the Meta-Critic's merge meaningful.
/// </summary>
public sealed class CriticAgent(CriticProfile profile, ILlmClient llm, PromptLibrary prompts, ToolGateway tools)
{
    public static readonly IReadOnlyList<CriticProfile> Profiles =
    [
        new(CriticNames.Logic, "critic.logic", "статусы state machine API контракт типы данных Decimal"),
        new(CriticNames.Qa, "critic.qa", "критерии приёмки Gherkin SLA размытые формулировки тестирование"),
        new(CriticNames.Compliance, "critic.compliance", "PCI DSS PAN CVV персональные данные НБРК АРРФР KYC AML RBAC 3DS логирование"),
        new(CriticNames.Distributed, "critic.distributed", "идемпотентность таймаут ретрай Saga компенсация двойное списание сверка"),
    ];

    public string Name => profile.Name;

    public async Task<IReadOnlyList<RawFinding>> ReviewAsync(string sessionId, TzDocument doc,
        IReadOnlyList<string> sectionIds, CancellationToken ct)
    {
        var dynamicQuery = BuildDynamicKbQuery(doc, sectionIds, profile.KbQuery);
        var kb = await tools.ExecuteAsync(sessionId, Name, "kb.search",
            new Dictionary<string, string> { ["query"] = dynamicQuery }, doc, ct);

        var docIndex = DocumentIndex.Build(doc);

        var user = new StringBuilder()
            .AppendLine("<knowledge_base>").AppendLine(kb).AppendLine("</knowledge_base>").AppendLine()
            .AppendLine("<document_index>").AppendLine(docIndex).AppendLine("</document_index>").AppendLine()
            .AppendLine($"<document title=\"{doc.Title}\">")
            .AppendLine(doc.ToPromptText(sectionIds))
            .AppendLine("</document>")
            .AppendLine().AppendLine("Report the defects you find in the sections above. Use <document_index> to catch cross-section contradictions and <knowledge_base> to enforce bank/ISO rules.")
            .ToString();

        var output = await llm.CompleteJsonAsync<CriticOutput>(new AgentCall
        {
            Agent = Name,
            System = prompts.System(profile.Prompt),
            User = user,
            MaxTokens = 4096,
        }, ct);

        return output.Findings.Select((f, i) => ToRaw(f, $"{Name}-{i + 1}")).ToList();
    }

    /// <summary>Human-in-the-loop: "@compliance проверь этот эндпоинт на IDOR" with full audit & RAG context.</summary>
    public async Task<AskOutput> AskAsync(TzDocument doc, string sectionId, string question, CancellationToken ct,
        string? sessionId = null, IReadOnlyList<Finding>? sectionFindings = null, Patch? sectionPatch = null)
    {
        var section = doc.Find(sectionId) ?? throw new ToolError($"Section '{sectionId}' not found");
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var kbQuery = BuildDynamicKbQuery(doc, [sectionId], $"{profile.KbQuery} {question}");
            var kb = await tools.ExecuteAsync(sessionId, Name, "kb.search",
                new Dictionary<string, string> { ["query"] = kbQuery }, doc, ct);
            sb.AppendLine("<knowledge_base>").AppendLine(kb).AppendLine("</knowledge_base>").AppendLine();
        }

        sb.AppendLine(doc.ToPromptText([section.Id])).AppendLine();

        if (sectionFindings is { Count: > 0 })
        {
            sb.AppendLine("<confirmed_findings>");
            foreach (var f in sectionFindings)
                sb.AppendLine($"- [{f.Severity} {f.Id}] {f.Title}: {f.Description} (Рекомендация: {f.Recommendation})");
            sb.AppendLine("</confirmed_findings>").AppendLine();
        }

        if (sectionPatch is not null && !string.IsNullOrWhiteSpace(sectionPatch.After))
        {
            sb.AppendLine("<applied_patch>").AppendLine(sectionPatch.After).AppendLine("</applied_patch>").AppendLine();
        }

        sb.AppendLine("<analyst_question>").AppendLine(question).AppendLine("</analyst_question>");

        return await llm.CompleteJsonAsync<AskOutput>(new AgentCall
        {
            Agent = Name,
            System = prompts.System(profile.Prompt) + "\n\n" + prompts.Get("ask"),
            User = sb.ToString(),
            MaxTokens = 4096,
        }, ct);
    }

    private static string BuildDynamicKbQuery(TzDocument doc, IReadOnlyList<string> sectionIds, string baseQuery)
    {
        var titles = sectionIds
            .Select(doc.Find)
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Title))
            .Select(s => s!.Title.Trim())
            .Distinct()
            .Take(4);
        var parts = new[] { doc.Title, string.Join(" ", titles), baseQuery }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var combined = string.Join(" | ", parts);
        return combined.Length <= 320 ? combined : combined[..320];
    }

    public RawFinding ToRaw(CriticFinding f, string id) => new()
    {
        Id = id,
        Agent = Name,
        SectionId = f.SectionId,
        Quote = f.Quote,
        Title = f.Title,
        Description = f.Description,
        Category = f.Category,
        SuggestedSeverity = f.Severity,
        Recommendation = f.Recommendation,
        StandardRef = f.StandardRef,
    };
}
