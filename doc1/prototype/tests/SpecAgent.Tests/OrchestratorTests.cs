using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Knowledge;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Tools;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;
using SpecAgent.Ingestion.Parsing;
using SpecAgent.Ingestion.Pii;

namespace SpecAgent.Tests;

public class OrchestratorTests
{
    private readonly InMemoryEventStore _events = new();
    private readonly FakeLlmClient _llm = new();

    private AuditOrchestrator NewWorker()
    {
        var prompts = new PromptLibrary();
        var kb = new MarkdownKnowledgeBase(Path.Combine(TestPaths.RepoRoot, "knowledge"));
        return new AuditOrchestrator(_events,
            new SupervisorAgent(_llm, prompts, NullLogger<SupervisorAgent>.Instance),
            new MetaCriticAgent(_llm, prompts, NullLogger<MetaCriticAgent>.Instance),
            new PatchLoop(_llm, prompts, _events, NullLogger<PatchLoop>.Instance),
            _llm, prompts, new ToolGateway(_events, kb),
            Options.Create(new PatchOptions()), NullLogger<AuditOrchestrator>.Instance);
    }

    private async Task<string> IngestDemoSpecAsync()
    {
        using var f = File.OpenRead(ParserTests.DemoSpecPath);
        var doc = new TextDocumentParser().Parse(f, "p2p-transfer-bad.md");
        var masked = await new PiiMasker(new InMemoryPiiVault()).MaskAsync("s-test", doc);
        await _events.AppendAsync("s-test", EventTypes.TzIngested, "ingestion", "test", new IngestedPayload(masked, 0));
        return "s-test";
    }

    private async Task<AuditReport> ReportAsync(string id) =>
        (await _events.ReadAsync(id)).Single(e => e.Type == EventTypes.ReportReady).Get<AuditReport>();

    [Fact]
    public async Task Full_pipeline_routes_fans_out_grounds_judges_and_patches()
    {
        var id = await IngestDemoSpecAsync();
        await NewWorker().RunAsync(id);

        var log = await _events.ReadAsync(id);
        var routed = log.Single(e => e.Type == EventTypes.Routed).Get<RoutingDecision>();

        // Supervisor LLM only asked for logic+qa; the deterministic floor added the money critics.
        Assert.True(routed.ForcedByRules);
        Assert.Contains(CriticNames.Compliance, routed.Routes.Keys);
        Assert.Contains(CriticNames.Distributed, routed.Routes.Keys);

        // Each critic wrote its own FINDINGS event (fan-in through the log), plus the rules critic.
        Assert.Equal(5, log.Count(e => e.Type == EventTypes.Findings));

        var report = await ReportAsync(id);
        // The hallucinated ISO 8583 quote was dropped by deterministic grounding.
        Assert.DoesNotContain(report.Findings, f => f.Title == "Галлюцинация");
        Assert.Contains("distributed-2", report.RejectedRawFindingIds);
        // The headline defect survived and was patched.
        Assert.Contains(report.Findings, f => f.SectionId == "4.3" && f.Agents.Contains(CriticNames.Distributed));
        Assert.NotEmpty(report.Patches);
        Assert.All(report.Patches, p => Assert.True(p.Verified));
    }

    [Fact]
    public async Task Crashed_worker_is_resumed_from_the_log_without_rerunning_critics()
    {
        var id = await IngestDemoSpecAsync();
        _llm.CrashOnceOn = MetaCriticAgent.Name;

        await Assert.ThrowsAsync<OperationCanceledException>(() => NewWorker().RunAsync(id));
        Assert.DoesNotContain(await _events.ReadAsync(id), e => EventTypes.Terminal.Contains(e.Type));
        Assert.Contains(id, await _events.ListOpenSessionsAsync());

        var criticCallsBefore = _llm.Calls.GetValueOrDefault(CriticNames.Distributed);

        await NewWorker().RunAsync(id); // a brand-new "brain" — no shared memory with the first one

        var log = await _events.ReadAsync(id);
        Assert.Single(log, e => e.Type == EventTypes.Resumed);
        Assert.Equal(criticCallsBefore, _llm.Calls[CriticNames.Distributed]); // critics did not run again
        Assert.Equal(1, _llm.Calls[SupervisorAgent.Name]);
        Assert.NotNull(await ReportAsync(id));
    }

    [Fact]
    public async Task Rule_based_critic_finds_headline_defects_offline()
    {
        using var f = File.OpenRead(ParserTests.DemoSpecPath);
        var doc = new TextDocumentParser().Parse(f, "p2p.md");
        var findings = RuleBasedCritic.Review(doc);

        Assert.Contains(findings, x => x.Category == "idempotency" && x.SuggestedSeverity == Severity.Blocker && x.SectionId == "4.3");
        Assert.Contains(findings, x => x.Category == "timeouts" && x.SectionId == "4.3");
        Assert.Contains(findings, x => x.Category == "pci-dss" && x.SectionId == "5.1");
        Assert.Contains(findings, x => x.Category == "data-types" && x.SectionId == "4.1");
        Assert.Contains(findings, x => x.Category == "ambiguity" && x.SectionId == "6.1");
        Assert.Empty(MetaCriticAgent.Ground(doc, findings).Ungrounded);
        // Offline merge must not fold two different defects that share one quote.
        var merged = MetaCriticAgent.DeterministicMerge(findings);
        Assert.Contains(merged.Findings, f => f.SectionId == "4.3" && f.Title.Contains("идемпотентность"));
        Assert.Contains(merged.Findings, f => f.SectionId == "4.3" && f.Title.Contains("таймауты"));
    }

    [Fact]
    public void Structured_output_schema_is_strict()
    {
        var schema = StrictJsonSchema.For<CriticOutput>();
        Assert.Equal("false", schema["additionalProperties"].GetRawText());
        var json = System.Text.Json.JsonSerializer.Serialize(schema);
        Assert.Contains("\"Blocker\"", json);
        Assert.Contains("\"sectionId\"", json);
    }

    [Fact]
    public async Task PatchLoop_preserves_draft_as_unverified_when_verifier_fails()
    {
        var id = await IngestDemoSpecAsync();
        _llm.FailWithLlmErrorOn = PatchLoop.Verifier;

        await NewWorker().RunAsync(id);

        var report = await ReportAsync(id);
        Assert.NotEmpty(report.Patches);
        Assert.All(report.Patches, p =>
        {
            Assert.False(p.Verified);
            Assert.Contains("Idempotency-Key", p.After);
            Assert.NotEmpty(p.VerifierNotes);
        });
    }

    [Fact]
    public void Rule_based_critic_catches_get_execute_pseudo_200_and_unmasked_pan_logging()
    {
        var builder = new SectionBuilder();
        builder.AddLine("1 Функциональные требования", 1);
        builder.AddLine("Ф-4 После подтверждения сервис отправляет запрос GET /api/v1/transfers/execute?from=1&to=2");
        builder.AddLine("2 Контракт API", 1);
        builder.AddLine("При ошибке валидации API возвращает HTTP 200 OK с телом {\"result\": \"ERROR\", \"message\": \"Недостаточно средств\"}");
        builder.AddLine("3 Логирование", 1);
        builder.AddLine("Шлюз сохраняет в общий лог полный JSON запроса, включая номер карты (PAN) и ИИН клиента.");
        builder.AddLine("4 Безопасное логирование", 1);
        builder.AddLine("Запрещено логировать полный PAN и ИИН; PAN маскируется (6+4) и логи шифруются.");
        var doc = builder.Build("synthetic.pdf");

        var findings = RuleBasedCritic.Review(doc);
        Assert.Contains(findings, f => f.SectionId == "Ф-4" && f.Category == "api-design" && f.SuggestedSeverity == Severity.Blocker);
        Assert.Contains(findings, f => f.SectionId == "2" && f.Category == "api-design" && f.SuggestedSeverity == Severity.Critical);
        Assert.Contains(findings, f => f.SectionId == "3" && f.Category == "pci-dss" && f.SuggestedSeverity == Severity.Blocker);
        Assert.DoesNotContain(findings, f => f.SectionId == "4" && f.Category == "pci-dss");
    }
}
