using SpecAgent.Core.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SpecAgent.Ingestion.Parsing;

/// <summary>.pdf via PdfPig. PDFs carry no heading styles, so sections come from "3.2 Title" numbering.</summary>
public sealed class PdfDocumentParser : IDocumentParser
{
    public bool CanParse(string fileName) => fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public TzDocument Parse(Stream content, string fileName)
    {
        using var pdf = PdfDocument.Open(content);
        var builder = new SectionBuilder();
        foreach (var page in pdf.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            foreach (var line in text.Split('\n')) builder.AddLine(line);
        }
        return builder.Build(fileName);
    }
}
