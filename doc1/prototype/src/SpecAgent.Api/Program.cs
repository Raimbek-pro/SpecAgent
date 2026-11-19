using System.Text.Json;
using SpecAgent.Agents;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Storage;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;
using SpecAgent.Ingestion;
using SpecAgent.Ingestion.Parsing;
using SpecAgent.Ingestion.Pii;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSpecAgentIngestion();
builder.Services.AddSpecAgents(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

if (app.Services.GetService<PostgresEventStore>() is { } pg) await pg.EnsureSchemaAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api/audits");

// 1. Upload → parse → mask → TZ_INGESTED → enqueue. Returns immediately; progress is streamed over SSE.
api.MapPost("/", async (IFormFile file, DocumentParserRegistry parsers, PiiMasker masker, IEventStore events,
    IPiiVault vault, AuditQueue queue, CancellationToken ct) =>
{
    TzDocument parsed;
    try
    {
        await using var stream = file.OpenReadStream();
        parsed = parsers.Parse(stream, file.FileName);
    }
    catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var sessionId = $"s-{Guid.NewGuid():N}"[..14];
    var masked = masker.Mask(sessionId, parsed);
    await events.AppendAsync(sessionId, EventTypes.TzIngested, "ingestion",
        $"{masked.Sections.Count} sections, {vault.Count(sessionId)} PII values masked",
        new IngestedPayload(masked, vault.Count(sessionId)), ct);
    await queue.EnqueueAsync(sessionId);
    return Results.Accepted($"/api/audits/{sessionId}", new { sessionId });
}).DisableAntiforgery();

// 2. Live execution trace (Server-Sent Events) straight from the session log.
api.MapGet("/{id}/events", async (string id, IEventStore events, HttpContext http, CancellationToken ct) =>
{
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    long after = long.TryParse(http.Request.Headers["Last-Event-ID"], out var last) ? last : 0;
    while (!ct.IsCancellationRequested)
    {
        var batch = await events.ReadAsync(id, after, ct);
        foreach (var e in batch)
        {
            // Payloads can be large (whole document); the trace only needs the headline.
            var dto = new { e.Seq, e.Type, e.Agent, e.At, e.Message };
            await http.Response.WriteAsync($"id: {e.Seq}\nevent: audit\ndata: {JsonSerializer.Serialize(dto, EventJson.Options)}\n\n", ct);
            after = e.Seq;
            if (EventTypes.Terminal.Contains(e.Type)) { await http.Response.Body.FlushAsync(ct); return; }
        }
        await http.Response.Body.FlushAsync(ct);
        await Task.Delay(300, ct);
    }
});

// 3. Document for the left pane (demasked: the analyst is allowed to see their own data).
api.MapGet("/{id}/document", async (string id, IEventStore events, PiiMasker masker, CancellationToken ct) =>
{
    var state = AuditState.From(await events.ReadAsync(id, ct: ct));
    if (state.Document is not { } doc) return Results.NotFound();
    return Results.Ok(doc with
    {
        Sections = doc.Sections.Select(s => s with { Title = masker.Demask(id, s.Title), Text = masker.Demask(id, s.Text) }).ToList(),
    });
});

// 4. Final report — the Demasker is the only place real PII leaves the system.
api.MapGet("/{id}", async (string id, IEventStore events, PiiMasker masker, CancellationToken ct) =>
{
    var log = await events.ReadAsync(id, ct: ct);
    if (log.Count == 0) return Results.NotFound();
    var ready = log.LastOrDefault(e => e.Type == EventTypes.ReportReady);
    if (ready is null)
        return Results.Ok(new { status = log.Any(e => e.Type == EventTypes.RunFailed) ? "failed" : "running", events = log.Count });

    var report = ready.Get<AuditReport>();
    string D(string s) => masker.Demask(id, s);
    return Results.Ok(new
    {
        status = "done",
        report = report with
        {
            Findings = report.Findings.Select(f => f with
            {
                Quote = D(f.Quote), Title = D(f.Title), Description = D(f.Description), Recommendation = D(f.Recommendation),
            }).ToList(),
            Patches = report.Patches.Select(p => p with { Before = D(p.Before), After = D(p.After), Gherkin = D(p.Gherkin) }).ToList(),
        },
    });
});

// 5. Raw findings + agent trail behind one final finding ("Agent Logs / Debate" tab).
api.MapGet("/{id}/findings/{findingId}/trace", async (string id, string findingId, IEventStore events, PiiMasker masker, CancellationToken ct) =>
{
    var state = AuditState.From(await events.ReadAsync(id, ct: ct));
    var finding = state.Judged?.Findings.FirstOrDefault(f => f.Id == findingId);
    if (finding is null) return Results.NotFound();
    var raw = state.CriticFindings.Values.SelectMany(f => f).Where(f => finding.MergedFrom.Contains(f.Id))
        .Select(f => f with { Quote = masker.Demask(id, f.Quote), Description = masker.Demask(id, f.Description) });
    return Results.Ok(new { finding.Id, finding.Rationale, finding.Agents, raw });
});

// 6. Human-in-the-loop: "@security проверь этот эндпоинт на IDOR".
api.MapPost("/{id}/ask", async (string id, AskRequest req, AuditOrchestrator orchestrator, PiiMasker masker, CancellationToken ct) =>
{
    try
    {
        var answer = await orchestrator.AskAsync(id, req.Agent.TrimStart('@'), req.SectionId, masker.Mask(id, req.Question), ct);
        return Results.Ok(answer with { Answer = masker.Demask(id, answer.Answer) });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 7. Manual wake(): re-enqueue a session (e.g. after RUN_FAILED caused by an outage).
api.MapPost("/{id}/resume", async (string id, AuditQueue queue) =>
{
    await queue.EnqueueAsync(id);
    return Results.Accepted();
});

app.Run();

public sealed record AskRequest(string Agent, string SectionId, string Question);

public partial class Program;
