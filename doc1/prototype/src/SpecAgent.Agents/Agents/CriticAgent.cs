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
        var kb = await tools.ExecuteAsync(sessionId, Name, "kb.search",
            new Dictionary<string, string> { ["query"] = profile.KbQuery }, doc, ct);

        var user = new StringBuilder()
            .AppendLine("<knowledge_base>").AppendLine(kb).AppendLine("</knowledge_base>").AppendLine()
            .AppendLine($"<document title=\"{doc.Title}\">")
            .AppendLine(doc.ToPromptText(sectionIds))
            .AppendLine("</document>")
            .AppendLine().AppendLine("Report the defects you find in the sections above.")
            .ToString();

        var output = await llm.CompleteJsonAsync<CriticOutput>(new AgentCall
        {
            Agent = Name,
            System = prompts.System(profile.Prompt),
            User = user,
        }, ct);

        return output.Findings.Select((f, i) => ToRaw(f, $"{Name}-{i + 1}")).ToList();
    }

    /// <summary>Human-in-the-loop: "@security проверь этот эндпоинт на IDOR".</summary>
    public async Task<AskOutput> AskAsync(TzDocument doc, string sectionId, string question, CancellationToken ct)
    {
        var section = doc.Find(sectionId) ?? throw new ToolError($"Section '{sectionId}' not found");
        return await llm.CompleteJsonAsync<AskOutput>(new AgentCall
        {
            Agent = Name,
            System = prompts.System(profile.Prompt) + "\n\n" + prompts.Get("ask"),
            User = $"{doc.ToPromptText([section.Id])}\n\n<analyst_question>\n{question}\n</analyst_question>",
            MaxTokens = 8000,
        }, ct);
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
