using System.Text;
using SpecAgent.Agents.Llm;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Agents;

/// <summary>
/// Batch Checklist Pass: checks all mandatory domain rules (ISO 29148 + FinTech) in a single
/// lightweight LLM call instead of 1 call per rule, keeping free-tier quota safe.
/// </summary>
public sealed class ChecklistAgent(ILlmClient llm, PromptLibrary prompts, string knowledgeDir)
{
    public const string Name = "checklist";

    public async Task<IReadOnlyList<RawFinding>> ReviewAsync(TzDocument doc, CancellationToken ct)
    {
        var checklistPath = Path.Combine(knowledgeDir, "mandatory-checklist.md");
        var checklistText = File.Exists(checklistPath)
            ? await File.ReadAllTextAsync(checklistPath, ct)
            : "";

        if (string.IsNullOrWhiteSpace(checklistText))
            return [];

        var user = new StringBuilder()
            .AppendLine("<mandatory_checklist>").AppendLine(checklistText).AppendLine("</mandatory_checklist>").AppendLine()
            .AppendLine($"<document title=\"{doc.Title}\">")
            .AppendLine(doc.ToPromptText())
            .AppendLine("</document>")
            .AppendLine().AppendLine("Verify every mandatory item CHK-01..CHK-12 in one pass. Report only missing or violated items.")
            .ToString();

        var output = await llm.CompleteJsonAsync<CriticOutput>(new AgentCall
        {
            Agent = Name,
            System = prompts.System("checklist"),
            User = user,
            MaxTokens = 8000,
        }, ct);

        return output.Findings.Select((f, i) => new RawFinding
        {
            Id = $"{Name}-{i + 1}",
            Agent = Name,
            SectionId = f.SectionId,
            Quote = f.Quote,
            Title = f.Title,
            Description = f.Description,
            Category = string.IsNullOrWhiteSpace(f.Category) ? "checklist" : f.Category,
            SuggestedSeverity = f.Severity,
            Recommendation = f.Recommendation,
            StandardRef = f.StandardRef,
        }).ToList();
    }
}
