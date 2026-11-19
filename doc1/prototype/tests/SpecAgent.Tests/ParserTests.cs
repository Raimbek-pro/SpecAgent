using SpecAgent.Ingestion.Parsing;

namespace SpecAgent.Tests;

public class ParserTests
{
    public static string DemoSpecPath => Path.Combine(TestPaths.RepoRoot, "dataset", "specs", "p2p-transfer-bad.md");

    [Fact]
    public void Markdown_spec_is_split_into_numbered_sections()
    {
        using var f = File.OpenRead(DemoSpecPath);
        var doc = new TextDocumentParser().Parse(f, "p2p-transfer-bad.md");

        Assert.Equal("ТЗ: P2P-переводы по номеру телефона", doc.Title);
        Assert.Contains(doc.Sections, s => s.Id == "4.3" && s.Text.Contains("повторяется"));
        Assert.Contains(doc.Sections, s => s.Id == "3" && s.Text.Contains("| REJECTED |"));
    }
}

public static class TestPaths
{
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SpecAgent.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
        }
    }
}
