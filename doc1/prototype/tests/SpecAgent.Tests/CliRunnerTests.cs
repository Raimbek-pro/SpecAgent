using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Knowledge;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Tools;
using SpecAgent.Api.Cli;
using SpecAgent.Core.Events;
using SpecAgent.Ingestion;

namespace SpecAgent.Tests;

public class CliRunnerTests
{
    [Fact]
    public void TryParseArgs_supports_long_short_and_equals_forms()
    {
        Assert.True(AuditCliRunner.IsCliMode(["--input", "spec.pdf", "--output", "report.md"]));
        Assert.False(AuditCliRunner.IsCliMode(["--urls", "http://localhost:5199"]));

        Assert.True(AuditCliRunner.TryParseArgs(
            ["--input=spec.pdf", "-o", "out/report.md", "--fixed", "out/fixed.md", "--with-gherkin"],
            out var opts, out var err, out var help));

        Assert.Null(err);
        Assert.False(help);
        Assert.NotNull(opts);
        Assert.EndsWith("spec.pdf", opts.InputPath);
        Assert.EndsWith(Path.Combine("out", "report.md"), opts.OutputPath);
        Assert.EndsWith(Path.Combine("out", "fixed.md"), opts.FixedOutputPath);
        Assert.True(opts.IncludeGherkinInFixed);
    }

    [Fact]
    public void TryParseArgs_returns_error_when_input_missing()
    {
        Assert.False(AuditCliRunner.TryParseArgs(["--output", "report.md"], out var opts, out var err, out var help));
        Assert.Null(opts);
        Assert.False(help);
        Assert.Contains("--input", err);
    }

    [Fact]
    public async Task RunAsync_generates_demasked_bcc_markdown_report_and_fixed_spec()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"specagent-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var reportPath = Path.Combine(tempDir, "nested", "report.md");
            var fixedPath = Path.Combine(tempDir, "nested", "fixed.md");

            var services = BuildTestServiceProvider();
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await AuditCliRunner.RunAsync(
                services,
                new CliOptions(ParserTests.DemoSpecPath, reportPath, fixedPath, IncludeGherkinInFixed: true),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal("", stderr.ToString());
            Assert.True(File.Exists(reportPath));
            Assert.True(File.Exists(fixedPath));

            var reportMd = await File.ReadAllTextAsync(reportPath);
            Assert.Contains("## 1. Паспорт прогона", reportMd);
            Assert.Contains("## 2. Как агент нарезал требования", reportMd);
            Assert.Contains("## 3. Сводка нарушений", reportMd);
            Assert.Contains("## 4. Детальный разбор дефектов", reportMd);
            Assert.Contains("## 5. Итог и вердикт (Go / No-Go)", reportMd);
            Assert.Contains("p2p-transfer-bad.md", reportMd);
            // PII from section 5.2 must be demasked back in the final report table.
            Assert.Contains("900101300037", reportMd);
            Assert.DoesNotContain("[IIN_1]", reportMd);

            var fixedMd = await File.ReadAllTextAsync(fixedPath);
            Assert.Contains("Исправленная версия — SpecAgent", fixedMd);
            Assert.Contains("Idempotency-Key", fixedMd);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_returns_nonzero_when_input_file_does_not_exist()
    {
        var services = BuildTestServiceProvider();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await AuditCliRunner.RunAsync(
            services,
            new CliOptions(Path.Combine(Path.GetTempPath(), "does-not-exist-spec.pdf"), Path.Combine(Path.GetTempPath(), "r.md")),
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("not found", stderr.ToString());
    }

    private static ServiceProvider BuildTestServiceProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModelRouting:Default:Model"] = "fake-test-model",
                ["Patches:MinSeverity"] = "Critical",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<ModelRoutingOptions>(config.GetSection(ModelRoutingOptions.Section));
        services.Configure<PatchOptions>(config.GetSection("Patches"));
        services.AddSpecAgentIngestion();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<IKnowledgeBase>(new MarkdownKnowledgeBase(Path.Combine(TestPaths.RepoRoot, "knowledge")));
        services.AddSingleton<ILlmClient, FakeLlmClient>();
        services.AddSingleton<PromptLibrary>();
        services.AddSingleton<ToolGateway>();
        services.AddSingleton<SupervisorAgent>();
        services.AddSingleton<MetaCriticAgent>();
        services.AddSingleton<PatchLoop>();
        services.AddScoped<AuditOrchestrator>();
        return services.BuildServiceProvider();
    }
}
