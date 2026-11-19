using SpecAgent.Core.Documents;

namespace SpecAgent.Ingestion.Parsing;

public interface IDocumentParser
{
    bool CanParse(string fileName);
    TzDocument Parse(Stream content, string fileName);
}

public sealed class DocumentParserRegistry(IEnumerable<IDocumentParser> parsers)
{
    public TzDocument Parse(Stream content, string fileName)
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(fileName))
                     ?? throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(fileName)} (use .docx, .pdf, .md or .txt)");
        return parser.Parse(content, fileName);
    }
}
