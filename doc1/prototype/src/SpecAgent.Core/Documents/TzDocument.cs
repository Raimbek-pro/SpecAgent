namespace SpecAgent.Core.Documents;

/// <summary>
/// Structured "AST" of a specification (ТЗ). Agents reference sections by <see cref="TzSection.Id"/>,
/// so every finding can be pinned to an exact place in the original document.
/// </summary>
public sealed record TzDocument(string Title, string SourceFileName, IReadOnlyList<TzSection> Sections)
{
    public TzSection? Find(string sectionId) =>
        Sections.FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Full text with section markers — what the Meta-Critic grounds quotes against.</summary>
    public string ToPromptText(IEnumerable<string>? onlySectionIds = null)
    {
        var filter = onlySectionIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = Sections
            .Where(s => filter is null || filter.Count == 0 || filter.Contains(s.Id))
            .Select(s => $"<section id=\"{s.Id}\" title=\"{s.Title}\">\n{s.Text}\n</section>");
        return string.Join("\n\n", parts);
    }
}

/// <param name="Id">Stable id, usually the heading number ("3.2") or a generated "s-7".</param>
/// <param name="Level">Heading depth: 1 for "3", 2 for "3.2" …</param>
public sealed record TzSection(string Id, string Title, int Level, string Text);
