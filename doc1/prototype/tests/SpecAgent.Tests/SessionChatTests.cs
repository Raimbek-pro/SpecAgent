using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Storage;
using SpecAgent.Core.Events;

namespace SpecAgent.Tests;

public class SessionChatTests
{
    private sealed class CapturingAskLlmClient : ILlmClient
    {
        private readonly FakeLlmClient _inner = new();
        public ConcurrentQueue<AgentCall> AskCalls { get; } = new();

        public Task<T> CompleteJsonAsync<T>(AgentCall call, CancellationToken ct = default) where T : class
        {
            if (typeof(T) == typeof(AskOutput))
            {
                AskCalls.Enqueue(call);
                var reply = call.Agent == "compliance"
                    ? "Проверьте принадлежность карты пользователю (IDOR) и маскируйте [IIN_1]."
                    : "Добавьте заголовок Idempotency-Key (UUID) и храните результат 24 часа.";
                return Task.FromResult((T)(object)new AskOutput { Answer = reply });
            }
            return _inner.CompleteJsonAsync<T>(call, ct);
        }
    }

    [Fact]
    public async Task Multi_turn_chat_persists_history_passes_context_to_llm_and_demasks_pii()
    {
        var tempKb = Path.Combine(Path.GetTempPath(), $"specagent-chat-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempKb);
        var capturingLlm = new CapturingAskLlmClient();

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
                    services.Replace(ServiceDescriptor.Singleton<ILlmClient>(capturingLlm));
                });
            });

            using var client = factory.CreateClient();

            // 1. Run demo spec audit to completion.
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(ParserTests.DemoSpecPath)), "file", "p2p-transfer-bad.md");
            var startRes = await client.PostAsync("/api/audits", form);
            Assert.Equal(HttpStatusCode.Accepted, startRes.StatusCode);
            var sessionId = (await startRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;

            for (int i = 0; i < 60; i++)
            {
                var poll = await client.GetFromJsonAsync<JsonElement>($"/api/audits/{sessionId}");
                if (poll.GetProperty("status").GetString() == "done") break;
                await Task.Delay(100);
            }

            // 2. Turn 1: Ask @compliance with real IIN (900101300037 — already vaulted as [IIN_1]).
            var turn1Res = await client.PostAsJsonAsync($"/api/audits/{sessionId}/ask", new
            {
                agent = "@compliance",
                sectionId = "4.3",
                question = "Проверь ИИН 900101300037 на IDOR в этом разделе",
            });
            Assert.Equal(HttpStatusCode.OK, turn1Res.StatusCode);
            var turn1 = await turn1Res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("compliance", turn1.GetProperty("agent").GetString());
            Assert.Equal("4.3", turn1.GetProperty("sectionId").GetString());
            // Response must be demasked back to real IIN.
            Assert.Contains("900101300037", turn1.GetProperty("answer").GetString());

            // 3. Turn 2: Follow-up to @distributed on the SAME section 4.3.
            var turn2Res = await client.PostAsJsonAsync($"/api/audits/{sessionId}/ask", new
            {
                agent = "@distributed",
                sectionId = "4.3",
                question = "А как защититься от двойного списания?",
            });
            Assert.Equal(HttpStatusCode.OK, turn2Res.StatusCode);

            // Verify Turn 2 LLM prompt received <conversation_history> with Turn 1 (masked, no raw PII).
            var calls = capturingLlm.AskCalls.ToArray();
            Assert.Equal(2, calls.Length);
            Assert.DoesNotContain("<conversation_history>", calls[0].User);
            Assert.Contains("<conversation_history>", calls[1].User);
            Assert.Contains("[user -> @compliance]: Проверь ИИН [IIN_1] на IDOR", calls[1].User);
            Assert.Contains("[@compliance]: Проверьте принадлежность карты", calls[1].User);
            Assert.DoesNotContain("900101300037", calls[1].User);

            // 4. GET /api/audits/{id}/chat returns full chronological thread (demasked).
            var chatRes = await client.GetAsync($"/api/audits/{sessionId}/chat");
            Assert.Equal(HttpStatusCode.OK, chatRes.StatusCode);
            var chatMap = await chatRes.Content.ReadFromJsonAsync<JsonElement>();
            var thread = chatMap.GetProperty("4.3").EnumerateArray().ToList();
            Assert.Equal(4, thread.Count);
            Assert.Equal("user", thread[0].GetProperty("role").GetString());
            Assert.Equal("compliance", thread[0].GetProperty("agent").GetString());
            Assert.Contains("900101300037", thread[0].GetProperty("text").GetString());
            Assert.Equal("agent", thread[1].GetProperty("role").GetString());
            Assert.Contains("900101300037", thread[1].GetProperty("text").GetString());
            Assert.Equal("user", thread[2].GetProperty("role").GetString());
            Assert.Equal("distributed", thread[2].GetProperty("agent").GetString());
            Assert.Equal("agent", thread[3].GetProperty("role").GetString());
            Assert.Contains("Idempotency-Key", thread[3].GetProperty("text").GetString());
        }
        finally
        {
            if (Directory.Exists(tempKb)) Directory.Delete(tempKb, recursive: true);
        }
    }

    [Fact]
    public void AuditState_parses_legacy_human_question_and_agent_answer_without_payload()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            new AuditEvent(1, "s-leg", EventTypes.HumanQuestion, "analyst", now, "@compliance [5.1] Проверь CVV", null),
            new AuditEvent(2, "s-leg", EventTypes.AgentAnswer, "compliance", now.AddSeconds(1), "CVV хранить нельзя", null),
        };

        var state = AuditState.From(events);
        Assert.True(state.ChatsBySection.TryGetValue("5.1", out var list));
        Assert.Equal(2, list.Count);
        Assert.Equal("user", list[0].Role);
        Assert.Equal("compliance", list[0].Agent);
        Assert.Equal("Проверь CVV", list[0].Text);
        Assert.Equal("agent", list[1].Role);
        Assert.Equal("CVV хранить нельзя", list[1].Text);
    }

    [Fact]
    public async Task Postgres_event_store_persists_chat_history_across_replays_when_db_available()
    {
        var cs = Environment.GetEnvironmentVariable("SPECAGENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;

        await using var dataSource = NpgsqlDataSource.Create(cs);
        var store = new PostgresEventStore(dataSource);
        await store.EnsureSchemaAsync();

        var sessionId = $"s-cht-{Guid.NewGuid():N}"[..16];
        try
        {
            await store.AppendAsync(sessionId, EventTypes.HumanQuestion, "analyst", "@logic [3] Есть противоречие?",
                new ChatMessagePayload("3", "logic", "Есть противоречие?"));
            await store.AppendAsync(sessionId, EventTypes.AgentAnswer, "logic", "Да, DECLINED vs REJECTED.",
                new ChatMessagePayload("3", "logic", "Да, DECLINED vs REJECTED."));

            var state = AuditState.From(await store.ReadAsync(sessionId));
            Assert.True(state.ChatsBySection.TryGetValue("3", out var msgs));
            Assert.Equal(2, msgs.Count);
            Assert.Equal("Есть противоречие?", msgs[0].Text);
            Assert.Equal("Да, DECLINED vs REJECTED.", msgs[1].Text);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand("DELETE FROM audit_sessions WHERE session_id = @s");
            cleanup.Parameters.AddWithValue("s", sessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
