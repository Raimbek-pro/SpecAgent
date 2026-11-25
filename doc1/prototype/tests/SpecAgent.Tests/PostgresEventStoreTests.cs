using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Knowledge;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Storage;
using SpecAgent.Agents.Tools;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;
using SpecAgent.Ingestion.Parsing;
using SpecAgent.Ingestion.Pii;

namespace SpecAgent.Tests;

public class PostgresEventStoreTests
{
    [Fact]
    public void Migration_sql_file_exists_and_matches_schema()
    {
        var migrationPath = Path.Combine(TestPaths.RepoRoot, "migrations", "001_init_audit_store.sql");
        Assert.True(File.Exists(migrationPath), $"Expected migration file at {migrationPath}");

        var sql = File.ReadAllText(migrationPath);
        Assert.Contains("CREATE TABLE IF NOT EXISTS audit_sessions", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS audit_events", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS schema_migrations", sql);
        Assert.Contains(PostgresEventStore.MigrationVersion, sql);
    }

    [Fact]
    public async Task Postgres_event_store_supports_concurrent_appends_and_crash_resume_when_db_available()
    {
        var cs = Environment.GetEnvironmentVariable("SPECAGENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return; // Set SPECAGENT_TEST_POSTGRES to run live DB verification.

        await using var dataSource = NpgsqlDataSource.Create(cs);
        var store = new PostgresEventStore(dataSource);
        await store.EnsureSchemaAsync();
        await store.EnsureSchemaAsync(); // Verify idempotency.

        var sessionId = $"s-pg-{Guid.NewGuid():N}"[..16];
        var llm = new FakeLlmClient { CrashOnceOn = MetaCriticAgent.Name };

        using var f = File.OpenRead(ParserTests.DemoSpecPath);
        var doc = new TextDocumentParser().Parse(f, "p2p-transfer-bad.md");
        var masked = new PiiMasker(new InMemoryPiiVault()).Mask(sessionId, doc);
        await store.AppendAsync(sessionId, EventTypes.TzIngested, "ingestion", "pg test", new IngestedPayload(masked, 0));

        AuditOrchestrator NewWorker()
        {
            var prompts = new PromptLibrary();
            var kb = new MarkdownKnowledgeBase(Path.Combine(TestPaths.RepoRoot, "knowledge"));
            return new AuditOrchestrator(store,
                new SupervisorAgent(llm, prompts, NullLogger<SupervisorAgent>.Instance),
                new MetaCriticAgent(llm, prompts, NullLogger<MetaCriticAgent>.Instance),
                new PatchLoop(llm, prompts, store, NullLogger<PatchLoop>.Instance),
                llm, prompts, new ToolGateway(store, kb),
                Options.Create(new PatchOptions()), NullLogger<AuditOrchestrator>.Instance);
        }

        // First run crashes during Meta-Critic after parallel critics wrote concurrently to Postgres.
        await Assert.ThrowsAsync<OperationCanceledException>(() => NewWorker().RunAsync(sessionId));

        var open = await store.ListOpenSessionsAsync();
        Assert.Contains(sessionId, open);

        var eventsBeforeResume = await store.ReadAsync(sessionId);
        Assert.Equal(Enumerable.Range(1, eventsBeforeResume.Count).Select(i => (long)i),
            eventsBeforeResume.Select(e => e.Seq));
        var criticCallsBefore = llm.Calls.GetValueOrDefault(CriticNames.Distributed);

        // Fresh worker resumes from Postgres without re-running critics.
        await NewWorker().RunAsync(sessionId);

        Assert.DoesNotContain(sessionId, await store.ListOpenSessionsAsync());
        Assert.Equal(criticCallsBefore, llm.Calls[CriticNames.Distributed]);
        var allEvents = await store.ReadAsync(sessionId);
        Assert.Contains(allEvents, e => e.Type == EventTypes.Resumed);
        Assert.Contains(allEvents, e => e.Type == EventTypes.ReportReady);
    }
}
