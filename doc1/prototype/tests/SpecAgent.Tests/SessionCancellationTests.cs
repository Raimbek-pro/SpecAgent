using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Storage;
using SpecAgent.Core.Events;

namespace SpecAgent.Tests;

public class SessionCancellationTests
{
    private sealed class BlockingFakeLlmClient : ILlmClient
    {
        private readonly FakeLlmClient _inner = new();
        public TaskCompletionSource EnteredSupervisor { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelledObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool BlockSupervisor = true;

        public async Task<T> CompleteJsonAsync<T>(AgentCall call, CancellationToken ct = default) where T : class
        {
            if (BlockSupervisor && call.Agent == "supervisor")
            {
                EnteredSupervisor.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    CancelledObserved.TrySetResult();
                    throw;
                }
            }
            return await _inner.CompleteJsonAsync<T>(call, ct);
        }
    }

    [Fact]
    public void Migration_005_session_cancel_status_exists()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "migrations", "005_session_cancel_status.sql");
        Assert.True(File.Exists(path), $"Expected migration file at {path}");
        var sql = File.ReadAllText(path);
        Assert.Contains("'cancelled'", sql);
        Assert.Contains("005_session_cancel_status", sql);
    }

    [Fact]
    public async Task Cancel_aborts_active_session_excludes_from_open_and_allows_manual_resume()
    {
        var tempKb = Path.Combine(Path.GetTempPath(), $"specagent-cancel-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempKb);
        var blockingLlm = new BlockingFakeLlmClient();

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("EventStore", "InMemory");
                builder.UseSetting("Knowledge:Path", tempKb);
                builder.UseSetting("Auth:Mode", "Demo");
                builder.ConfigureServices(services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<ILlmClient>(blockingLlm));
                });
            });

            using var client = factory.CreateClient();

            // 1. Unknown session → 404
            var missing = await client.PostAsync("/api/audits/s-does-not-exist/cancel", null);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // 2. Start an audit that blocks inside SupervisorAgent.
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(ParserTests.DemoSpecPath)), "file", "p2p-transfer-bad.md");
            var startRes = await client.PostAsync("/api/audits", form);
            Assert.Equal(HttpStatusCode.Accepted, startRes.StatusCode);
            var sessionId = (await startRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;

            await blockingLlm.EnteredSupervisor.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 2b. Calling resume while already actively running must be rejected with 409 Conflict.
            var activeResume = await client.PostAsync($"/api/audits/{sessionId}/resume", null);
            Assert.Equal(HttpStatusCode.Conflict, activeResume.StatusCode);

            // 3. Cancel active session → aborts in-flight token immediately.
            var cancelRes = await client.PostAsync($"/api/audits/{sessionId}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);
            var cancelBody = await cancelRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("cancelled", cancelBody.GetProperty("status").GetString());
            Assert.True(cancelBody.GetProperty("wasActive").GetBoolean());
            Assert.False(cancelBody.GetProperty("alreadyCancelled").GetBoolean());

            await blockingLlm.CancelledObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 4. Status is cancelled, idempotent re-cancel returns 200 alreadyCancelled=true, not in ListOpenSessionsAsync.
            var statusJson = await client.GetFromJsonAsync<JsonElement>($"/api/audits/{sessionId}");
            Assert.Equal("cancelled", statusJson.GetProperty("status").GetString());

            var secondCancel = await client.PostAsync($"/api/audits/{sessionId}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, secondCancel.StatusCode);
            Assert.True((await secondCancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyCancelled").GetBoolean());

            var store = factory.Services.GetRequiredService<IEventStore>();
            Assert.DoesNotContain(sessionId, await store.ListOpenSessionsAsync());

            var metrics = await client.GetStringAsync("/metrics");
            Assert.Contains("specagent_audits_cancelled_total 1", metrics);

            // 5. Resume the cancelled session and verify it completes cleanly.
            blockingLlm.BlockSupervisor = false;
            var resumeRes = await client.PostAsync($"/api/audits/{sessionId}/resume", null);
            Assert.Equal(HttpStatusCode.Accepted, resumeRes.StatusCode);

            string status = "running";
            for (int i = 0; i < 60; i++)
            {
                var poll = await client.GetFromJsonAsync<JsonElement>($"/api/audits/{sessionId}");
                status = poll.GetProperty("status").GetString()!;
                if (status == "done") break;
                await Task.Delay(100);
            }
            Assert.Equal("done", status);

            // 6. Cancelling an already completed session returns 409 Conflict.
            var cancelDone = await client.PostAsync($"/api/audits/{sessionId}/cancel", null);
            Assert.Equal(HttpStatusCode.Conflict, cancelDone.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempKb)) Directory.Delete(tempKb, recursive: true);
        }
    }

    [Fact]
    public async Task Cancel_marks_stuck_non_active_session_as_cancelled()
    {
        var tempKb = Path.Combine(Path.GetTempPath(), $"specagent-stuck-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempKb);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("EventStore", "InMemory");
                builder.UseSetting("Knowledge:Path", tempKb);
                builder.UseSetting("Auth:Mode", "Demo");
            });

            using var client = factory.CreateClient();
            var store = factory.Services.GetRequiredService<IEventStore>();

            // Simulate a stuck session (e.g. only RUN_STARTED, no worker currently running it).
            const string stuckId = "s-stuck-123456";
            await store.AppendAsync(stuckId, EventTypes.RunStarted, "orchestrator", "stuck session");
            Assert.Contains(stuckId, await store.ListOpenSessionsAsync());

            var cancelRes = await client.PostAsync($"/api/audits/{stuckId}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);
            var body = await cancelRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("cancelled", body.GetProperty("status").GetString());
            Assert.False(body.GetProperty("wasActive").GetBoolean());

            Assert.DoesNotContain(stuckId, await store.ListOpenSessionsAsync());
            var statusJson = await client.GetFromJsonAsync<JsonElement>($"/api/audits/{stuckId}");
            Assert.Equal("cancelled", statusJson.GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(tempKb)) Directory.Delete(tempKb, recursive: true);
        }
    }

    [Fact]
    public async Task Postgres_event_store_persists_cancelled_status_and_excludes_from_open_when_db_available()
    {
        var cs = Environment.GetEnvironmentVariable("SPECAGENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;

        await using var dataSource = NpgsqlDataSource.Create(cs);
        var store = new PostgresEventStore(dataSource);
        await store.EnsureSchemaAsync();

        var sessionId = $"s-cnl-{Guid.NewGuid():N}"[..16];
        try
        {
            await store.AppendAsync(sessionId, EventTypes.RunStarted, "orchestrator", "started");
            Assert.Contains(sessionId, await store.ListOpenSessionsAsync());

            await store.AppendAsync(sessionId, EventTypes.RunCancelled, "orchestrator", "cancelled by test");
            Assert.DoesNotContain(sessionId, await store.ListOpenSessionsAsync());

            var state = AuditState.From(await store.ReadAsync(sessionId));
            Assert.True(state.Cancelled);
            Assert.False(state.Finished);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand("DELETE FROM audit_sessions WHERE session_id = @s");
            cleanup.Parameters.AddWithValue("s", sessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
