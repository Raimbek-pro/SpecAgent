using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SpecAgent.Core.Documents;

namespace SpecAgent.Ingestion.Parsing;

/// <summary>.docx via OpenXML. Heading styles become sections; tables become Markdown tables.</summary>
public sealed class DocxDocumentParser : IDocumentParser
{
    public bool CanParse(string fileName) => fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);

    public TzDocument Parse(Stream content, string fileName)
    {
        // OpenXML needs a seekable stream.
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        buffer.Position = 0;

        using var doc = WordprocessingDocument.Open(buffer, false);
        var body = doc.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("Empty .docx");
        var builder = new SectionBuilder();

        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph p:
                    builder.AddLine(p.InnerText, HeadingLevel(p));
                    break;
                case Table t:
                    foreach (var line in TableToMarkdown(t)) builder.AddLine(line);
                    break;
            }
        }
        return builder.Build(fileName);
    }

    private static int? HeadingLevel(Paragraph p)
    {
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style is null) return null;
        // "Heading1", "Heading 2", localized "Заголовок1", "1" (some RU templates)
        var digits = new string(style.Where(char.IsDigit).ToArray());
        var isHeading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                        style.StartsWith("Заголовок", StringComparison.OrdinalIgnoreCase) ||
                        style.Equals("Title", StringComparison.OrdinalIgnoreCase);
        if (!isHeading) return null;
        return int.TryParse(digits, out var lvl) ? Math.Clamp(lvl, 1, 6) : 1;
    }

    private static IEnumerable<string> TableToMarkdown(Table table)
    {
        var rows = table.Elements<TableRow>()
            .Select(r => r.Elements<TableCell>().Select(c => c.InnerText.Replace("|", "\\|").Trim()).ToList())
            .Where(r => r.Count > 0)
            .ToList();
        if (rows.Count == 0) yield break;

        yield return "| " + string.Join(" | ", rows[0]) + " |";
        yield return "|" + string.Concat(Enumerable.Repeat(" --- |", rows[0].Count));
        foreach (var r in rows.Skip(1)) yield return "| " + string.Join(" | ", r) + " |";
    }
}
