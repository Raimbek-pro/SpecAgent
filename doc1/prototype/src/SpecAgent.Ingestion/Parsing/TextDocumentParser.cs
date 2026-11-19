using SpecAgent.Core.Documents;

namespace SpecAgent.Ingestion.Parsing;

/// <summary>.md / .txt — handy for the benchmark dataset. Markdown "#" headings are honoured.</summary>
public sealed class TextDocumentParser : IDocumentParser
{
    public bool CanParse(string fileName) =>
        fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    public TzDocument Parse(Stream content, string fileName)
    {
        using var reader = new StreamReader(content);
        var builder = new SectionBuilder();
        while (reader.ReadLine() is { } line)
        {
            var hashes = line.TakeWhile(c => c == '#').Count();
            if (hashes is > 0 and <= 6 && line.Length > hashes && line[hashes] == ' ')
                builder.AddLine(line[(hashes + 1)..], hashes);
            else
                builder.AddLine(line);
        }
        return builder.Build(fileName);
    }
}
