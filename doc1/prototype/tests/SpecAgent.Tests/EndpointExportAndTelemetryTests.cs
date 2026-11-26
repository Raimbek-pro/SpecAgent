using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Storage;
using SpecAgent.Agents.Telemetry;
using SpecAgent.Core.Events;

namespace SpecAgent.Tests;

public class EndpointExportAndTelemetryTests
{
    [Fact]
    public void Migration_004_session_telemetry_exists_and_matches_schema()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "migrations", "004_session_telemetry.sql");
        Assert.True(File.Exists(path), $"Expected migration file at {path}");
        var sql = File.ReadAllText(path);
        Assert.Contains("input_tokens", sql);
        Assert.Contains("output_tokens", sql);
        Assert.Contains("estimated_cost_usd", sql);
        Assert.Contains("llm_calls", sql);
        Assert.Contains(PostgresSessionUsageStore.MigrationVersion, sql);
    }

    [Fact]
    public async Task Export_endpoints_return_valid_pdf_docx_and_honor_gherkin_toggle_and_metrics()
    {
        var tempKb = Path.Combine(Path.GetTempPath(), $"specagent-p3-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempKb);
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
                    services.Replace(ServiceDescriptor.Singleton<ILlmClient, FakeLlmClient>());
                });
            });

            using var client = factory.CreateClient();

            // 1. Upload demo spec and wait for deterministic FakeLlmClient audit to finish.
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(ParserTests.DemoSpecPath)), "file", "p2p-transfer-bad.md");
            var postRes = await client.PostAsync("/api/audits", form);
            Assert.Equal(HttpStatusCode.Accepted, postRes.StatusCode);

            var accepted = await postRes.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = accepted.GetProperty("sessionId").GetString()!;

            string status = "running";
            for (int i = 0; i < 60; i++)
            {
                var poll = await client.GetFromJsonAsync<JsonElement>($"/api/audits/{sessionId}");
                status = poll.GetProperty("status").GetString()!;
                if (status == "done") break;
                await Task.Delay(100);
            }
            Assert.Equal("done", status);

            // 2. Export fixed.pdf → verify valid %PDF- bytes.
            var pdfRes = await client.GetAsync($"/api/audits/{sessionId}/export/fixed.pdf?download=1");
            Assert.Equal(HttpStatusCode.OK, pdfRes.StatusCode);
            Assert.Equal("application/pdf", pdfRes.Content.Headers.ContentType?.MediaType);
            var pdfBytes = await pdfRes.Content.ReadAsByteArrayAsync();
            Assert.True(pdfBytes.Length > 1000);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdfBytes, 0, 5));

            // 3. Export fixed.docx with IncludeGherkinInFixed = false → valid OpenXML, no Gherkin block.
            await client.PutAsJsonAsync("/api/settings", new
            {
                patches = new { minSeverity = "Critical", maxPatches = 8, maxRounds = 2, parallelism = 3, includeGherkinInFixed = false },
            });

            var docxNoGherkinRes = await client.GetAsync($"/api/audits/{sessionId}/export/fixed.docx");
            Assert.Equal(HttpStatusCode.OK, docxNoGherkinRes.StatusCode);
            var docxNoGherkinBytes = await docxNoGherkinRes.Content.ReadAsByteArrayAsync();
            Assert.True(docxNoGherkinBytes.Length > 500);
            Assert.Equal(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, docxNoGherkinBytes[..4]);
            var docxTextNoGherkin = ReadDocxText(docxNoGherkinBytes);
            Assert.Contains("Idempotency-Key", docxTextNoGherkin);
            Assert.DoesNotContain("Given", docxTextNoGherkin);

            // 4. Toggle IncludeGherkinInFixed = true → fixed.md and fixed.docx now include Gherkin scenarios.
            await client.PutAsJsonAsync("/api/settings", new
            {
                patches = new { minSeverity = "Critical", maxPatches = 8, maxRounds = 2, parallelism = 3, includeGherkinInFixed = true },
            });

            var fixedMd = await client.GetStringAsync($"/api/audits/{sessionId}/export/fixed.md");
            Assert.Contains("Критерии приёмки (Gherkin)", fixedMd);
            Assert.Contains("Given", fixedMd);

            var docxWithGherkinBytes = await client.GetByteArrayAsync($"/api/audits/{sessionId}/export/fixed.docx");
            var docxTextWithGherkin = ReadDocxText(docxWithGherkinBytes);
            Assert.Contains("Given", docxTextWithGherkin);

            // 5. Knowledge base path-traversal rejection.
            var traversal1 = await client.DeleteAsync("/api/knowledge/..%2F..%2Fappsettings.json");
            Assert.Equal(HttpStatusCode.BadRequest, traversal1.StatusCode);

            var traversal2 = await client.DeleteAsync("/api/knowledge/..%2F..%2Fescape.md");
            Assert.Equal(HttpStatusCode.BadRequest, traversal2.StatusCode);

            var nonMd = await client.DeleteAsync("/api/knowledge/secret.txt");
            Assert.Equal(HttpStatusCode.BadRequest, nonMd.StatusCode);

            // 6. Prometheus /metrics endpoint exposes completed audit telemetry.
            var metricsRes = await client.GetAsync("/metrics");
            Assert.Equal(HttpStatusCode.OK, metricsRes.StatusCode);
            var metricsBody = await metricsRes.Content.ReadAsStringAsync();
            Assert.Contains("specagent_audits_started_total 1", metricsBody);
            Assert.Contains("specagent_audits_completed_total 1", metricsBody);

            // 7. OpenAPI spec + Scalar interactive docs endpoints are live.
            var openApiRes = await client.GetAsync("/openapi/v1.json");
            Assert.Equal(HttpStatusCode.OK, openApiRes.StatusCode);
            var openApiJson = await openApiRes.Content.ReadAsStringAsync();
            Assert.Contains("SpecAgent API", openApiJson);
            Assert.Contains("/api/audits", openApiJson);
            Assert.Contains("/api/settings", openApiJson);
            Assert.Contains("/api/knowledge", openApiJson);
            Assert.Contains("/api/admin/audit-log", openApiJson);

            var scalarRes = await client.GetAsync("/scalar/v1");
            Assert.Equal(HttpStatusCode.OK, scalarRes.StatusCode);
            var scalarHtml = await scalarRes.Content.ReadAsStringAsync();
            Assert.Contains("SpecAgent API", scalarHtml);
        }
        finally
        {
            if (Directory.Exists(tempKb)) Directory.Delete(tempKb, recursive: true);
        }
    }

    [Fact]
    public async Task Postgres_session_usage_store_accumulates_tokens_and_cost_when_db_available()
    {
        var cs = Environment.GetEnvironmentVariable("SPECAGENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;

        await using var dataSource = NpgsqlDataSource.Create(cs);
        var events = new PostgresEventStore(dataSource);
        var usage = new PostgresSessionUsageStore(dataSource);
        await events.EnsureSchemaAsync();
        await usage.EnsureSchemaAsync();

        var sessionId = $"s-tel-{Guid.NewGuid():N}"[..16];
        await events.AppendAsync(sessionId, EventTypes.RunStarted, "orchestrator", "telemetry test");

        var cost1 = ModelPricing.EstimateCostUsd("kimi-k3", 10_000, 2_000);
        var cost2 = ModelPricing.EstimateCostUsd("kimi-k3", 5_000, 1_000);
        Assert.True(cost1 > 0 && cost2 > 0);

        await usage.RecordUsageAsync(sessionId, "logic", "kimi-k3", 10_000, 2_000, cost1, 1200);
        await usage.RecordUsageAsync(sessionId, "meta-critic", "kimi-k3", 5_000, 1_000, cost2, 800);

        var total = await usage.GetUsageAsync(sessionId);
        Assert.Equal(15_000, total.InputTokens);
        Assert.Equal(3_000, total.OutputTokens);
        Assert.Equal(cost1 + cost2, total.EstimatedCostUsd);
        Assert.Equal(2, total.LlmCalls);
    }

    private static string ReadDocxText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        return doc.MainDocumentPart?.Document?.Body?.InnerText ?? "";
    }
}
