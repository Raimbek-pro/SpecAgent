using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Storage;
using SpecAgent.Agents.Tools;
using SpecAgent.Api.Export;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;
using SpecAgent.Ingestion.Parsing;
using SpecAgent.Ingestion.Pii;

namespace SpecAgent.Api.Cli;

public sealed record CliOptions(
    string InputPath,
    string OutputPath,
    string? FixedOutputPath = null,
    bool IncludeGherkinInFixed = false);

/// <summary>
/// Single-command CLI entry point (BCC-HUB requirement):
///   dotnet run --project src/SpecAgent.Api -- --input docs/requirements.pdf --output report.md
/// Reuses the full pipeline: DocumentParserRegistry → PiiMasker → AuditOrchestrator → Demasker → Markdown report.
/// </summary>
public static class AuditCliRunner
{
    public const string UsageText = """
        SpecAgent CLI — Single-Command FinTech Requirements Auditor

        Usage:
          dotnet run --project src/SpecAgent.Api -- --input <path> --output <report.md> [options]

        Options:
          -i, --input <path>     Path to input specification (.pdf, .docx, .md, .txt) [required]
          -o, --output <path>    Path to output Markdown audit report (default: <name>-report.md)
              --fixed <path>     Optional path to export patched spec (.md, .pdf, or .docx)
              --with-gherkin     Include Gherkin acceptance criteria in --fixed output
          -h, --help             Show this help message
        """;

    public static bool IsCliMode(string[] args) =>
        args.Any(a =>
            a is "--input" or "-i" or "--output" or "-o" or "--fixed" or "--help" or "-h" or "audit" ||
            a.StartsWith("--input=", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--fixed=", StringComparison.OrdinalIgnoreCase));

    public static bool TryParseArgs(string[] args, out CliOptions? options, out string? error, out bool showHelp)
    {
        options = null;
        error = null;
        showHelp = false;

        string? input = null;
        string? output = null;
        string? fixedOut = null;
        bool includeGherkin = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                showHelp = true;
                return true;
            }
            if (arg.Equals("audit", StringComparison.OrdinalIgnoreCase))
                continue;
            if (arg is "--with-gherkin")
            {
                includeGherkin = true;
                continue;
            }

            if (TryReadValue(args, ref i, "--input", "-i", out var v)) { input = v; continue; }
            if (TryReadValue(args, ref i, "--output", "-o", out v)) { output = v; continue; }
            if (TryReadValue(args, ref i, "--fixed", null, out v)) { fixedOut = v; continue; }

            if (arg.StartsWith('-'))
            {
                error = $"Unknown option: {arg}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Missing required option: --input <path>";
            return false;
        }

        var (fullInput, baseDir) = ResolveInputPath(input);
        if (string.IsNullOrWhiteSpace(output))
        {
            var baseName = Path.GetFileNameWithoutExtension(fullInput);
            output = Path.Combine(baseDir, $"{baseName}-report.md");
        }
        else if (!Path.IsPathRooted(output))
        {
            output = Path.GetFullPath(Path.Combine(baseDir, output));
        }

        string? resolvedFixed = null;
        if (!string.IsNullOrWhiteSpace(fixedOut))
        {
            resolvedFixed = Path.IsPathRooted(fixedOut)
                ? Path.GetFullPath(fixedOut)
                : Path.GetFullPath(Path.Combine(baseDir, fixedOut));
        }

        options = new CliOptions(
            InputPath: fullInput,
            OutputPath: Path.GetFullPath(output),
            FixedOutputPath: resolvedFixed,
            IncludeGherkinInFixed: includeGherkin);
        return true;
    }

    /// <summary>
    /// Resolves relative --input paths even when `dotnet run --project src/SpecAgent.Api` sets CWD
    /// to the project folder (src/SpecAgent.Api) instead of the caller's working directory.
    /// </summary>
    private static (string FullInputPath, string BaseDir) ResolveInputPath(string input)
    {
        var cwd = Directory.GetCurrentDirectory();
        if (Path.IsPathRooted(input))
            return (Path.GetFullPath(input), cwd);

        var direct = Path.GetFullPath(Path.Combine(cwd, input));
        if (File.Exists(direct))
            return (direct, cwd);

        var dir = new DirectoryInfo(cwd).Parent;
        while (dir is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir.FullName, input));
            if (File.Exists(candidate))
                return (candidate, dir.FullName);
            dir = dir.Parent;
        }

        return (direct, cwd);
    }

    public static async Task<int> RunAsync(
        IServiceProvider services,
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct = default)
    {
        if (!File.Exists(options.InputPath))
        {
            await stderr.WriteLineAsync($"Error: input file not found: {options.InputPath}");
            return 2;
        }

        var sw = Stopwatch.StartNew();

        if (services.GetService<PostgresEventStore>() is { } pg)
            await pg.EnsureSchemaAsync(ct);

        var parsers = services.GetRequiredService<DocumentParserRegistry>();
        var masker = services.GetRequiredService<PiiMasker>();
        var vault = services.GetRequiredService<IPiiVault>();
        var events = services.GetRequiredService<IEventStore>();
        var config = services.GetRequiredService<IConfiguration>();
        var modelRouting = services.GetService<IOptions<ModelRoutingOptions>>();

        TzDocument parsed;
        try
        {
            await using var stream = File.OpenRead(options.InputPath);
            parsed = parsers.Parse(stream, Path.GetFileName(options.InputPath));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            await stderr.WriteLineAsync($"Error parsing input file: {ex.Message}");
            return 2;
        }

        var sessionId = $"s-cli-{Guid.NewGuid():N}"[..16];
        var masked = await masker.MaskAsync(sessionId, parsed, ct);
        var piiCount = await vault.CountAsync(sessionId, ct);

        await stdout.WriteLineAsync($"[SpecAgent] Session {sessionId} | Input: {Path.GetFileName(options.InputPath)} ({masked.Sections.Count} sections, {piiCount} PII values masked)");

        await events.AppendAsync(sessionId, EventTypes.TzIngested, "ingestion",
            $"{masked.Sections.Count} sections, {piiCount} PII values masked",
            new IngestedPayload(masked, piiCount), ct);

        using (var scope = services.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<AuditOrchestrator>();
            await orchestrator.RunAsync(sessionId, ct);
        }

        var log = await events.ReadAsync(sessionId, ct: ct);
        var ready = log.LastOrDefault(e => e.Type == EventTypes.ReportReady);
        if (ready is null)
        {
            var failed = log.LastOrDefault(e => e.Type == EventTypes.RunFailed);
            await stderr.WriteLineAsync($"Error: audit did not complete. {failed?.Message ?? "No REPORT_READY event found."}");
            return 1;
        }

        var demaskedDoc = await masker.DemaskAsync(sessionId, masked, ct);
        var demaskedReport = await masker.DemaskAsync(sessionId, ready.Get<AuditReport>(), includeFindings: true, ct);

        var modelName = FormatModelSummary(modelRouting?.Value, config);
        var markdown = ToolGateway.RenderReportMarkdown(demaskedDoc, demaskedReport, modelName);

        EnsureParentDirectory(options.OutputPath);
        await File.WriteAllTextAsync(options.OutputPath, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);

        if (!string.IsNullOrWhiteSpace(options.FixedOutputPath))
        {
            EnsureParentDirectory(options.FixedOutputPath);
            var ext = Path.GetExtension(options.FixedOutputPath).ToLowerInvariant();
            switch (ext)
            {
                case ".pdf":
                    await File.WriteAllBytesAsync(options.FixedOutputPath,
                        DocumentExporter.RenderPdf(demaskedDoc, demaskedReport, options.IncludeGherkinInFixed), ct);
                    break;
                case ".docx":
                    await File.WriteAllBytesAsync(options.FixedOutputPath,
                        DocumentExporter.RenderDocx(demaskedDoc, demaskedReport, options.IncludeGherkinInFixed), ct);
                    break;
                default:
                    await File.WriteAllTextAsync(options.FixedOutputPath,
                        ToolGateway.RenderPatchedDocumentMarkdown(demaskedDoc, demaskedReport, options.IncludeGherkinInFixed),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
                    break;
            }
            await stdout.WriteLineAsync($"[SpecAgent] Patched spec written to: {options.FixedOutputPath}");
        }

        sw.Stop();
        var blockers = demaskedReport.Findings.Count(f => f.Severity == Severity.Blocker);
        var criticals = demaskedReport.Findings.Count(f => f.Severity == Severity.Critical);
        var majors = demaskedReport.Findings.Count(f => f.Severity == Severity.Major);
        var minors = demaskedReport.Findings.Count(f => f.Severity == Severity.Minor);

        await stdout.WriteLineAsync(
            $"[SpecAgent] Done in {sw.Elapsed.TotalSeconds:F1}s | Score: {demaskedReport.QualityScore}/100 | " +
            $"Findings: {demaskedReport.Findings.Count} (Blocker={blockers}, Critical={criticals}, Major={majors}, Minor={minors}) | " +
            $"Patches: {demaskedReport.Patches.Count}");
        await stdout.WriteLineAsync($"[SpecAgent] Report written to: {options.OutputPath}");

        return 0;
    }

    /// <summary>
    /// Resolves the knowledge directory robustly regardless of whether the CLI is invoked from
    /// the repo root, doc1/prototype, or src/SpecAgent.Api.
    /// </summary>
    public static string ResolveKnowledgeDirectory(IConfiguration config, string contentRoot)
    {
        var configured = config["Knowledge:Path"] ?? "knowledge";
        if (Path.IsPathRooted(configured) && Directory.Exists(configured))
            return configured;

        var direct = Path.GetFullPath(Path.Combine(contentRoot, configured));
        if (Directory.Exists(direct))
            return direct;

        var searchRoots = new[] { contentRoot, AppContext.BaseDirectory };
        foreach (var root in searchRoots)
        {
            var dir = new DirectoryInfo(root);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "knowledge");
                if (Directory.Exists(candidate)) return candidate;
                var nested = Path.Combine(dir.FullName, "doc1", "prototype", "knowledge");
                if (Directory.Exists(nested)) return nested;
                dir = dir.Parent;
            }
        }

        return direct;
    }

    private static bool TryReadValue(string[] args, ref int index, string longFlag, string? shortFlag, out string? value)
    {
        var arg = args[index];
        var prefix = longFlag + "=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[prefix.Length..];
            return true;
        }
        if (arg.Equals(longFlag, StringComparison.OrdinalIgnoreCase) ||
            (shortFlag is not null && arg.Equals(shortFlag, StringComparison.OrdinalIgnoreCase)))
        {
            if (index + 1 < args.Length)
            {
                value = args[++index];
                return true;
            }
            value = "";
            return true;
        }
        value = null;
        return false;
    }

    public static string FormatModelSummary(ModelRoutingOptions? routing, IConfiguration config)
    {
        var defaultModel = routing?.Default.Model
                           ?? config["ModelRouting:Default:Model"]
                           ?? "deterministic-rules";
        if (routing?.Agents is not { Count: > 0 } agents)
            return defaultModel;

        var overrides = agents
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Model) &&
                         !string.Equals(kv.Value.Model, defaultModel, StringComparison.OrdinalIgnoreCase))
            .GroupBy(kv => kv.Value.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.Key).OrderBy(k => k))})")
            .ToList();

        return overrides.Count == 0
            ? defaultModel
            : $"{defaultModel} + {string.Join("; ", overrides)}";
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }
}
