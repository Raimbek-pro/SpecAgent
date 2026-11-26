using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Npgsql;
using Scalar.AspNetCore;
using SpecAgent.Agents;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Knowledge;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Storage;
using SpecAgent.Agents.Telemetry;
using SpecAgent.Agents.Tools;
using SpecAgent.Api.Auth;
using SpecAgent.Api.Cli;
using SpecAgent.Api.Export;
using SpecAgent.Api.Security;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Events;
using SpecAgent.Core.Findings;
using SpecAgent.Ingestion;
using SpecAgent.Ingestion.Parsing;
using SpecAgent.Ingestion.Pii;

LoadDotEnv();

if (AuditCliRunner.IsCliMode(args))
{
    if (!AuditCliRunner.TryParseArgs(args, out var cliOptions, out var parseError, out var showHelp))
    {
        Console.Error.WriteLine($"Error: {parseError}");
        Console.Error.WriteLine();
        Console.Error.WriteLine(AuditCliRunner.UsageText);
        return 2;
    }
    if (showHelp || cliOptions is null)
    {
        Console.WriteLine(AuditCliRunner.UsageText);
        return 0;
    }

    var cliBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ContentRootPath = Directory.GetCurrentDirectory(),
    });
    // Ensure appsettings.json from the build output is loaded even when invoked from repo root.
    cliBuilder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true);
    cliBuilder.Configuration.AddEnvironmentVariables();

    var resolvedKb = AuditCliRunner.ResolveKnowledgeDirectory(cliBuilder.Configuration, cliBuilder.Environment.ContentRootPath);
    cliBuilder.Configuration["Knowledge:Path"] = resolvedKb;

    cliBuilder.Services.AddSpecAgentIngestion();
    cliBuilder.Services.AddSpecAgents(cliBuilder.Configuration, cliBuilder.Environment.ContentRootPath);

    await using var cliApp = cliBuilder.Build();
    return await AuditCliRunner.RunAsync(cliApp.Services, cliOptions, Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSpecAgentIngestion();
builder.Services.AddSpecAgents(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSpecAgentAuth(builder.Configuration);
builder.Services.AddSpecAgentSecurity(builder.Configuration);
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "SpecAgent API";
        doc.Info.Version = "v1";
        doc.Info.Description = "Multi-Agent FinTech Requirements Auditor & Auto-Fixer API (ISO 29148, PCI DSS, NBK/ARRFR, Qdrant RAG, PostgreSQL Event Sourcing).";
        return Task.CompletedTask;
    });
});
var maxUploadBytes = builder.Configuration.GetValue<long?>("Security:MaxUploadBytes") ?? (25L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxUploadBytes + (1024 * 1024));
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = maxUploadBytes + (1024 * 1024));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

if (app.Services.GetService<PostgresEventStore>() is { } pg) await pg.EnsureSchemaAsync();
if (app.Services.GetService<PostgresAdminAuditLog>() is { } adminLog) await adminLog.EnsureSchemaAsync();
if (app.Services.GetService<PostgresPiiVault>() is { } piiVault) await piiVault.EnsureSchemaAsync();
if (app.Services.GetService<PostgresSystemSettingsStore>() is { } pgSettings) await pgSettings.EnsureSchemaAsync();
if (app.Services.GetService<PostgresSessionUsageStore>() is { } pgUsage) await pgUsage.EnsureSchemaAsync();

// Hydrate runtime settings from durable storage so Admin changes survive restarts/replicas.
if (await app.Services.GetRequiredService<ISystemSettingsStore>().LoadAsync() is { } persisted)
{
    ApplyPersistedSettings(
        persisted,
        app.Services.GetRequiredService<IOptions<PatchOptions>>().Value,
        app.Services.GetRequiredService<IOptions<ModelRoutingOptions>>().Value,
        app.Services.GetRequiredService<PiiMasker>());
}

app.UseSpecAgentSecurityHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors(SecurityExtensions.StrictCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference(o =>
{
    o.Title = "SpecAgent API — FinTech Requirements Auditor";
    o.Theme = ScalarTheme.DeepSpace;
}).AllowAnonymous();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithTags("Ops").WithSummary("Service health check").AllowAnonymous();

app.MapGet("/metrics", (SpecAgentMetrics metrics) =>
    Results.Text(metrics.RenderPrometheus(), "text/plain; version=0.0.4; charset=utf-8"))
    .WithTags("Ops").WithSummary("Prometheus metrics exposition").AllowAnonymous();

app.MapGet("/api/me", (ClaimsPrincipal user, AuthOptions authOpts) =>
{
    var current = CurrentUser.From(user, authOpts.EffectiveMode);
    return Results.Ok(new
    {
        authenticated = user.Identity?.IsAuthenticated ?? false,
        authMode = current.AuthMode,
        sub = current.Sub,
        name = current.Name,
        role = current.Role,
        isAdmin = current.IsAdmin,
    });
}).WithTags("Auth").WithSummary("Current authenticated user & role").RequireAuthorization(AuthPolicies.AnalystOrAdmin);

var api = app.MapGroup("/api/audits").WithTags("Audits").RequireAuthorization(AuthPolicies.AnalystOrAdmin);

// 0. List recent saved sessions (from Postgres) with document names, scores & token/cost telemetry.
api.MapGet("/recent", async (IServiceProvider sp, CancellationToken ct) =>
{
    var ds = sp.GetService<NpgsqlDataSource>();
    if (ds is null) return Results.Ok(Array.Empty<object>());
    await using var cmd = ds.CreateCommand("""
        SELECT s.session_id,
               s.status,
               s.last_seq,
               s.updated_at,
               (SELECT e.payload->'document'->>'sourceFileName' FROM audit_events e WHERE e.session_id = s.session_id AND e.type = 'TZ_INGESTED' LIMIT 1) AS file_name,
               (SELECT e.payload->'document'->>'title' FROM audit_events e WHERE e.session_id = s.session_id AND e.type = 'TZ_INGESTED' LIMIT 1) AS doc_title,
               (SELECT (e.payload->>'qualityScore')::int FROM audit_events e WHERE e.session_id = s.session_id AND e.type = 'REPORT_READY' ORDER BY e.seq DESC LIMIT 1) AS score,
               s.input_tokens,
               s.output_tokens,
               s.estimated_cost_usd,
               s.llm_calls
        FROM audit_sessions s
        ORDER BY s.updated_at DESC
        LIMIT 20
        """);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var list = new List<object>();
    while (await r.ReadAsync(ct))
    {
        list.Add(new
        {
            sessionId = r.GetString(0),
            status = r.GetString(1),
            events = r.GetInt64(2),
            updatedAt = r.GetDateTime(3),
            fileName = r.IsDBNull(4) ? r.GetString(0) : r.GetString(4),
            title = r.IsDBNull(5) ? "" : r.GetString(5),
            score = r.IsDBNull(6) ? (int?)null : r.GetInt32(6),
            inputTokens = r.GetInt64(7),
            outputTokens = r.GetInt64(8),
            estimatedCostUsd = r.GetDecimal(9),
            llmCalls = r.GetInt32(10),
        });
    }
    return Results.Ok(list);
});

// 1. Upload → parse → mask → TZ_INGESTED → enqueue. Returns immediately; progress is streamed over SSE.
var uploadsDir = Path.Combine(app.Environment.ContentRootPath, "uploads");
Directory.CreateDirectory(uploadsDir);

api.MapPost("/", async (IFormFile file, SecurityOptions secOpts, DocumentParserRegistry parsers, PiiMasker masker,
    IEventStore events, IPiiVault vault, AuditQueue queue, CancellationToken ct) =>
{
    if (file.Length > secOpts.MaxUploadBytes)
        return Results.Json(new { error = $"Файл превышает максимальный размер ({secOpts.MaxUploadBytes / (1024 * 1024)} МБ)." }, statusCode: 413);

    var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var bytes = ms.ToArray();

    var validation = UploadValidator.Validate(file.FileName, bytes, secOpts.MaxUploadBytes);
    if (!validation.IsValid)
        return Results.Json(new { error = validation.Error }, statusCode: validation.StatusCode);

    ms.Position = 0;

    TzDocument parsed;
    try
    {
        parsed = parsers.Parse(ms, file.FileName);
    }
    catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var sessionId = $"s-{Guid.NewGuid():N}"[..14];
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is ".pdf" or ".docx" or ".md" or ".txt")
        await File.WriteAllBytesAsync(Path.Combine(uploadsDir, $"{sessionId}{ext}"), bytes, ct);

    var masked = masker.Mask(sessionId, parsed);
    await events.AppendAsync(sessionId, EventTypes.TzIngested, "ingestion",
        $"{masked.Sections.Count} sections, {vault.Count(sessionId)} PII values masked",
        new IngestedPayload(masked, vault.Count(sessionId)), ct);
    await queue.EnqueueAsync(sessionId);
    return Results.Accepted($"/api/audits/{sessionId}", new { sessionId });
}).DisableAntiforgery().RequireRateLimiting(RateLimitPolicies.AuditUpload);

// 2. Live execution trace (Server-Sent Events) straight from the session log.
api.MapGet("/{id}/events", async (string id, IEventStore events, HttpContext http, CancellationToken ct) =>
{
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    long after = long.TryParse(http.Request.Headers["Last-Event-ID"], out var last) ? last : 0;
    while (!ct.IsCancellationRequested)
    {
        var batch = await events.ReadAsync(id, after, ct);
        for (int i = 0; i < batch.Count; i++)
        {
            var e = batch[i];
            // Payloads can be large (whole document); the trace only needs the headline.
            var dto = new { e.Seq, e.Type, e.Agent, e.At, e.Message };
            await http.Response.WriteAsync($"id: {e.Seq}\nevent: audit\ndata: {JsonSerializer.Serialize(dto, EventJson.Options)}\n\n", ct);
            after = e.Seq;
            if (EventTypes.Terminal.Contains(e.Type) && i == batch.Count - 1)
            {
                await http.Response.Body.FlushAsync(ct);
                return;
            }
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
    var state = AuditState.From(log);
    var ready = log.LastOrDefault(e => e.Type == EventTypes.ReportReady);
    if (ready is null)
    {
        var status = state.Cancelled ? "cancelled" : state.Failed ? "failed" : "running";
        return Results.Ok(new { status, events = log.Count });
    }

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
}).RequireRateLimiting(RateLimitPolicies.AgentAsk);

// 7. Manual wake(): re-enqueue a failed or cancelled session to continue from the last completed step.
api.MapPost("/{id}/resume", async (string id, IEventStore events, AuditQueue queue, CancellationToken ct) =>
{
    var log = await events.ReadAsync(id, ct: ct);
    if (log.Count == 0) return Results.NotFound(new { error = $"Сессия '{id}' не найдена." });
    var state = AuditState.From(log);
    if (state.Finished)
        return Results.Conflict(new { error = "Аудит уже завершён." });
    if (state.Cancelled || state.Failed)
        await events.AppendAsync(id, EventTypes.Resumed, AuditOrchestrator.Name, $"manual resume({id}) from event {state.LastSeq}", ct: ct);

    await queue.EnqueueAsync(id);
    return Results.Accepted($"/api/audits/{id}", new { sessionId = id, status = "running" });
}).WithSummary("Resume a failed or cancelled audit session");

// 7b. Cancel a running, queued, or stuck audit session without affecting other sessions.
api.MapPost("/{id}/cancel", async (string id, ClaimsPrincipal user, AuthOptions authOpts,
    IEventStore events, AuditSessionRegistry registry, SpecAgentMetrics metrics, CancellationToken ct) =>
{
    var log = await events.ReadAsync(id, ct: ct);
    if (log.Count == 0) return Results.NotFound(new { error = $"Сессия '{id}' не найдена." });

    var state = AuditState.From(log);
    if (state.Finished)
        return Results.Conflict(new { error = "Аудит уже завершён и не может быть отменён." });
    if (state.Cancelled)
        return Results.Ok(new { sessionId = id, status = "cancelled", alreadyCancelled = true, wasActive = false });

    var wasActive = registry.TryCancel(id);
    var actor = CurrentUser.From(user, authOpts.EffectiveMode);
    await events.AppendAsync(id, EventTypes.RunCancelled, AuditOrchestrator.Name,
        $"cancelled by {actor.Sub}", new { actorSub = actor.Sub, wasActive }, ct);
    metrics.RecordAuditCancelled();

    return Results.Ok(new { sessionId = id, status = "cancelled", alreadyCancelled = false, wasActive });
}).WithSummary("Cancel an active, queued, or stuck audit session");

// 8. Export official BCC-HUB Markdown audit report (demasked).
api.MapGet("/{id}/export/report.md", async (string id, IEventStore events, PiiMasker masker, IConfiguration config, CancellationToken ct) =>
{
    var log = await events.ReadAsync(id, ct: ct);
    var state = AuditState.From(log);
    var ready = log.LastOrDefault(e => e.Type == EventTypes.ReportReady);
    if (state.Document is not { } doc || ready is null) return Results.NotFound();

    string D(string s) => masker.Demask(id, s);
    var demaskedDoc = doc with { Sections = doc.Sections.Select(s => s with { Title = D(s.Title), Text = D(s.Text) }).ToList() };
    var rawReport = ready.Get<AuditReport>();
    var demaskedReport = rawReport with
    {
        Findings = rawReport.Findings.Select(f => f with
        {
            Quote = D(f.Quote), Title = D(f.Title), Description = D(f.Description), Recommendation = D(f.Recommendation), Rationale = D(f.Rationale),
        }).ToList(),
        Patches = rawReport.Patches.Select(p => p with { Before = D(p.Before), After = D(p.After), Gherkin = D(p.Gherkin) }).ToList(),
    };

    var model = config["ModelRouting:Default:Model"] ?? "gemini-flash-lite-latest";
    var md = ToolGateway.RenderReportMarkdown(demaskedDoc, demaskedReport, model);
    var baseName = Path.GetFileNameWithoutExtension(doc.SourceFileName);
    return Results.File(Encoding.UTF8.GetBytes(md), "text/markdown; charset=utf-8", $"{baseName}-report.md");
});

// 9. Export patched/fixed specification Markdown file (demasked).
api.MapGet("/{id}/export/fixed.md", async (string id, IEventStore events, PiiMasker masker, IOptions<PatchOptions> patchOpts, CancellationToken ct) =>
{
    var log = await events.ReadAsync(id, ct: ct);
    var state = AuditState.From(log);
    var ready = log.LastOrDefault(e => e.Type == EventTypes.ReportReady);
    if (state.Document is not { } doc || ready is null) return Results.NotFound();

    string D(string s) => masker.Demask(id, s);
    var demaskedDoc = doc with { Sections = doc.Sections.Select(s => s with { Title = D(s.Title), Text = D(s.Text) }).ToList() };
    var rawReport = ready.Get<AuditReport>();
    var demaskedReport = rawReport with
    {
        Patches = rawReport.Patches.Select(p => p with { Before = D(p.Before), After = D(p.After), Gherkin = D(p.Gherkin) }).ToList(),
    };

    var md = ToolGateway.RenderPatchedDocumentMarkdown(demaskedDoc, demaskedReport, patchOpts.Value.IncludeGherkinInFixed);
    var baseName = Path.GetFileNameWithoutExtension(doc.SourceFileName);
    return Results.File(Encoding.UTF8.GetBytes(md), "text/markdown; charset=utf-8", $"{baseName}-fixed.md");
});

// 9b. Export clean patched PDF (inline for iframe preview or ?download=1 / ?download=true for file download).
api.MapGet("/{id}/export/fixed.pdf", async (string id, string? download, IEventStore events, PiiMasker masker, IOptions<PatchOptions> patchOpts, HttpResponse resp, CancellationToken ct) =>
{
    var log = await events.ReadAsync(id, ct: ct);
    var state = AuditState.From(log);
    var ready = log.LastOrDefault(e => e.Type == EventTypes.ReportReady);
    if (state.Document is not { } doc || ready is null) return Results.NotFound();

    string D(string s) => masker.Demask(id, s);
    var demaskedDoc = doc with { Sections = doc.Sections.Select(s => s with { Title = D(s.Title), Text = D(s.Text) }).ToList() };
    var rawReport = ready.Get<AuditReport>();
    var demaskedReport = rawReport with
    {
        Patches = rawReport.Patches.Select(p => p with { Before = D(p.Before), After = D(p.After), Gherkin = D(p.Gherkin) }).ToList(),
    };

    var pdfBytes = DocumentExporter.RenderPdf(demaskedDoc, demaskedReport, patchOpts.Value.IncludeGherkinInFixed);
    var baseName = Path.GetFileNameWithoutExtension(doc.SourceFileName);
    var isDownload = string.Equals(download, "1", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(download, "true", StringComparison.OrdinalIgnoreCase);
    if (isDownload)
        return Results.File(pdfBytes, "application/pdf", $"{baseName}-fixed.pdf");
    resp.Headers.ContentDisposition = $"inline; filename=\"{baseName}-fixed.pdf\"";
    return Results.Bytes(pdfBytes, "application/pdf");
});

// 9c. Export clean patched DOCX (OpenXML).
api.MapGet("/{id}/export/fixed.docx", async (string id, IEventStore events, PiiMasker masker, IOptions<PatchOptions> patchOpts, CancellationToken ct) =>
{
    var log = await events.ReadAsync(id, ct: ct);
    var state = AuditState.From(log);
    var ready = log.LastOrDefault(e => e.Type == EventTypes.ReportReady);
    if (state.Document is not { } doc || ready is null) return Results.NotFound();

    string D(string s) => masker.Demask(id, s);
    var demaskedDoc = doc with { Sections = doc.Sections.Select(s => s with { Title = D(s.Title), Text = D(s.Text) }).ToList() };
    var rawReport = ready.Get<AuditReport>();
    var demaskedReport = rawReport with
    {
        Patches = rawReport.Patches.Select(p => p with { Before = D(p.Before), After = D(p.After), Gherkin = D(p.Gherkin) }).ToList(),
    };

    var docxBytes = DocumentExporter.RenderDocx(demaskedDoc, demaskedReport, patchOpts.Value.IncludeGherkinInFixed);
    var baseName = Path.GetFileNameWithoutExtension(doc.SourceFileName);
    return Results.File(docxBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{baseName}-fixed.docx");
});

// 9d. Original document as PDF (returns uploaded PDF verbatim if present, otherwise renders clean original PDF).
api.MapGet("/{id}/original.pdf", async (string id, IEventStore events, PiiMasker masker, HttpResponse resp, CancellationToken ct) =>
{
    var savedPdf = Path.Combine(uploadsDir, $"{id}.pdf");
    if (File.Exists(savedPdf))
    {
        resp.Headers.ContentDisposition = $"inline; filename=\"{id}-original.pdf\"";
        return Results.Bytes(await File.ReadAllBytesAsync(savedPdf, ct), "application/pdf");
    }
    var state = AuditState.From(await events.ReadAsync(id, ct: ct));
    if (state.Document is not { } doc) return Results.NotFound();
    string D(string s) => masker.Demask(id, s);
    var demaskedDoc = doc with { Sections = doc.Sections.Select(s => s with { Title = D(s.Title), Text = D(s.Text) }).ToList() };
    var pdfBytes = DocumentExporter.RenderPdf(demaskedDoc, null);
    var baseName = Path.GetFileNameWithoutExtension(doc.SourceFileName);
    resp.Headers.ContentDisposition = $"inline; filename=\"{baseName}.pdf\"";
    return Results.Bytes(pdfBytes, "application/pdf");
});

// 10. Runtime Settings (Patches, Model Routing, PII Masking) — RBAC enforced via ASP.NET Core policies.
static object SnapshotSettings(PatchOptions p, ModelRoutingOptions m, PiiMasker masker) => new
{
    patches = new
    {
        minSeverity = p.MinSeverity.ToString(),
        maxPatches = p.MaxPatches,
        maxRounds = p.MaxRounds,
        parallelism = p.Parallelism,
        includeGherkinInFixed = p.IncludeGherkinInFixed,
    },
    model = new
    {
        defaultModel = m.Default.Model,
        defaultEffort = m.Default.Effort,
    },
    pii = new
    {
        enabled = masker.Enabled,
        kinds = PiiMasker.AllKinds.ToDictionary(k => k, k => masker.EnabledKinds.Contains(k)),
    },
};

app.MapGet("/api/settings", (IOptions<PatchOptions> patchOpts, IOptions<ModelRoutingOptions> modelOpts, PiiMasker masker, IConfiguration config) =>
{
    var p = patchOpts.Value;
    var m = modelOpts.Value;
    return Results.Ok(new
    {
        patches = new
        {
            minSeverity = p.MinSeverity.ToString(),
            maxPatches = p.MaxPatches,
            maxRounds = p.MaxRounds,
            parallelism = p.Parallelism,
            includeGherkinInFixed = p.IncludeGherkinInFixed,
        },
        model = new
        {
            defaultModel = m.Default.Model,
            defaultEffort = m.Default.Effort,
            provider = config["Llm:Provider"] ?? "OpenAiCompatible",
            endpoint = config["Llm:Endpoint"] ?? "https://api.moonshot.ai/v1",
            availableModels = new[] { "kimi-k3", "kimi-k3.1-pro" },
            availableEfforts = new[] { "low", "high", "max" },
        },
        pii = new
        {
            enabled = masker.Enabled,
            kinds = PiiMasker.AllKinds.ToDictionary(k => k, k => masker.EnabledKinds.Contains(k)),
        },
    });
}).WithTags("Settings").WithSummary("Get runtime patch, model & PII settings").RequireAuthorization(AuthPolicies.AnalystOrAdmin);

static PersistedSystemSettings ToPersistedSettings(PatchOptions p, ModelRoutingOptions m, PiiMasker masker) => new(
    new PersistedPatchSettings(p.MinSeverity.ToString(), p.MaxPatches, p.MaxRounds, p.Parallelism, p.IncludeGherkinInFixed),
    new PersistedModelSettings(m.Default.Model, m.Default.Effort),
    new PersistedPiiSettings(masker.Enabled, PiiMasker.AllKinds.ToDictionary(k => k, k => masker.EnabledKinds.Contains(k))));

static void ApplyPersistedSettings(PersistedSystemSettings saved, PatchOptions p, ModelRoutingOptions m, PiiMasker masker)
{
    if (saved.Patches is { } sp)
    {
        if (Enum.TryParse<Severity>(sp.MinSeverity, true, out var sev)) p.MinSeverity = sev;
        p.MaxPatches = Math.Clamp(sp.MaxPatches, 1, 50);
        p.MaxRounds = Math.Clamp(sp.MaxRounds, 1, 3);
        p.Parallelism = Math.Clamp(sp.Parallelism, 1, 10);
        p.IncludeGherkinInFixed = sp.IncludeGherkinInFixed;
    }
    if (saved.Model is { } sm)
    {
        if (!string.IsNullOrWhiteSpace(sm.DefaultModel))
        {
            m.Default.Model = sm.DefaultModel.Trim();
            foreach (var ag in m.Agents.Values) ag.Model = sm.DefaultModel.Trim();
        }
        if (!string.IsNullOrWhiteSpace(sm.DefaultEffort))
        {
            m.Default.Effort = sm.DefaultEffort.Trim();
            foreach (var ag in m.Agents.Values) ag.Effort = sm.DefaultEffort.Trim();
        }
    }
    if (saved.Pii is { } spii)
    {
        masker.Enabled = spii.Enabled;
        if (spii.Kinds is not null)
        {
            masker.EnabledKinds = new HashSet<string>(
                spii.Kinds.Where(kv => kv.Value).Select(kv => kv.Key),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}

app.MapPut("/api/settings", async (ClaimsPrincipal user, AuthOptions authOpts, SettingsUpdateRequest body,
    IOptions<PatchOptions> patchOpts, IOptions<ModelRoutingOptions> modelOpts, PiiMasker masker,
    ISystemSettingsStore settingsStore, IAdminAuditLog auditLog, CancellationToken ct) =>
{
    var before = SnapshotSettings(patchOpts.Value, modelOpts.Value, masker);

    if (body.Patches is { } p)
    {
        if (Enum.TryParse<Severity>(p.MinSeverity, true, out var sev)) patchOpts.Value.MinSeverity = sev;
        patchOpts.Value.MaxPatches = Math.Clamp(p.MaxPatches, 1, 50);
        patchOpts.Value.MaxRounds = Math.Clamp(p.MaxRounds, 1, 3);
        patchOpts.Value.Parallelism = Math.Clamp(p.Parallelism, 1, 10);
        patchOpts.Value.IncludeGherkinInFixed = p.IncludeGherkinInFixed;
    }

    if (body.Model is { } m)
    {
        if (!string.IsNullOrWhiteSpace(m.DefaultModel))
        {
            modelOpts.Value.Default.Model = m.DefaultModel.Trim();
            foreach (var ag in modelOpts.Value.Agents.Values) ag.Model = m.DefaultModel.Trim();
        }
        if (!string.IsNullOrWhiteSpace(m.DefaultEffort))
        {
            modelOpts.Value.Default.Effort = m.DefaultEffort.Trim();
            foreach (var ag in modelOpts.Value.Agents.Values) ag.Effort = m.DefaultEffort.Trim();
        }
    }

    if (body.Pii is { } pii)
    {
        masker.Enabled = pii.Enabled;
        if (pii.Kinds is not null)
        {
            masker.EnabledKinds = new HashSet<string>(
                pii.Kinds.Where(kv => kv.Value).Select(kv => kv.Key),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    var after = SnapshotSettings(patchOpts.Value, modelOpts.Value, masker);
    var actor = CurrentUser.From(user, authOpts.EffectiveMode);
    await settingsStore.SaveAsync(ToPersistedSettings(patchOpts.Value, modelOpts.Value, masker), actor.Sub, ct);
    await auditLog.RecordAsync(actor.Sub, actor.Role, "settings.update", "runtime-settings",
        new { before, after }, ct);

    return Results.Ok(new { status = "saved" });
}).WithTags("Settings").WithSummary("Update & persist runtime settings (AdminOnly)").RequireAuthorization(AuthPolicies.AdminOnly);

// 11. RAG Knowledge Base reference documents (list / upload / delete) — Admin-only mutation.
var builtInKbFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "iso-29148-requirements-standard.md",
    "mandatory-checklist.md",
    "fintech-kz-standards.md",
    "banking-architecture-patterns.md",
};

static string ResolveKbDir(IKnowledgeBase kb, IConfiguration config, string contentRoot)
{
    if (kb is SemanticKnowledgeBase skb) return skb.DirectoryPath;
    var kbPath = config["Knowledge:Path"] ?? "knowledge";
    return Path.IsPathRooted(kbPath) ? kbPath : Path.GetFullPath(Path.Combine(contentRoot, kbPath));
}

app.MapGet("/api/knowledge", (IKnowledgeBase kb, IConfiguration config, IWebHostEnvironment env) =>
{
    var dir = ResolveKbDir(kb, config, env.ContentRootPath);
    if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());

    var files = Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
        .Select(path => new FileInfo(path))
        .OrderByDescending(f => builtInKbFiles.Contains(f.Name))
        .ThenBy(f => f.Name)
        .Select(f => new
        {
            name = f.Name,
            sizeBytes = f.Length,
            updatedAt = f.LastWriteTimeUtc,
            builtIn = builtInKbFiles.Contains(f.Name),
        })
        .ToList();
    return Results.Ok(files);
}).WithTags("Knowledge").WithSummary("List RAG knowledge base documents").RequireAuthorization(AuthPolicies.AnalystOrAdmin);

app.MapPost("/api/knowledge", async (ClaimsPrincipal user, AuthOptions authOpts, IFormFile file,
    SecurityOptions secOpts, DocumentParserRegistry parsers, IKnowledgeBase kb, IAdminAuditLog auditLog,
    IConfiguration config, IWebHostEnvironment env, CancellationToken ct) =>
{
    if (file.Length > secOpts.MaxUploadBytes)
        return Results.Json(new { error = $"Файл превышает максимальный размер ({secOpts.MaxUploadBytes / (1024 * 1024)} МБ)." }, statusCode: 413);

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var bytes = ms.ToArray();

    var validation = UploadValidator.Validate(file.FileName, bytes, secOpts.MaxUploadBytes);
    if (!validation.IsValid)
        return Results.Json(new { error = validation.Error }, statusCode: validation.StatusCode);

    var dir = ResolveKbDir(kb, config, env.ContentRootPath);
    Directory.CreateDirectory(dir);

    var rawName = Path.GetFileNameWithoutExtension(file.FileName);
    var safeBase = string.Concat(rawName.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')).Trim('-');
    if (string.IsNullOrWhiteSpace(safeBase)) safeBase = $"ref-{Guid.NewGuid():N}"[..12];
    var targetPath = Path.Combine(dir, $"{safeBase}.md");

    string mdContent;
    if (file.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || file.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
    {
        mdContent = Encoding.UTF8.GetString(bytes);
    }
    else
    {
        ms.Position = 0;
        var parsed = parsers.Parse(ms, file.FileName);
        var sb = new StringBuilder().AppendLine($"# {parsed.Title}").AppendLine();
        foreach (var s in parsed.Sections)
        {
            if (!string.IsNullOrWhiteSpace(s.Title)) sb.AppendLine($"## {s.Id} {s.Title}");
            sb.AppendLine(s.Text).AppendLine();
        }
        mdContent = sb.ToString();
    }

    await File.WriteAllTextAsync(targetPath, mdContent, Encoding.UTF8, ct);
    if (kb is SemanticKnowledgeBase skb) await skb.ReloadAsync();

    var savedName = Path.GetFileName(targetPath);
    var sizeBytes = Encoding.UTF8.GetByteCount(mdContent);
    var actor = CurrentUser.From(user, authOpts.EffectiveMode);
    await auditLog.RecordAsync(actor.Sub, actor.Role, "knowledge.upload", savedName,
        new { originalFileName = file.FileName, savedFileName = savedName, sizeBytes }, ct);

    return Results.Ok(new { name = savedName, sizeBytes });
}).DisableAntiforgery().WithTags("Knowledge").WithSummary("Upload document to RAG knowledge base (AdminOnly)").RequireAuthorization(AuthPolicies.AdminOnly);

app.MapDelete("/api/knowledge/{name}", async (ClaimsPrincipal user, AuthOptions authOpts, string name,
    IKnowledgeBase kb, IAdminAuditLog auditLog, IConfiguration config, IWebHostEnvironment env, CancellationToken ct) =>
{
    var decoded = Uri.UnescapeDataString(name);
    var safe = Path.GetFileName(decoded);
    if (string.IsNullOrWhiteSpace(safe) ||
        decoded != safe ||
        decoded.Contains("..", StringComparison.Ordinal) ||
        decoded.Contains('/') ||
        decoded.Contains('\\') ||
        !safe.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Некорректное имя файла." });

    var dir = ResolveKbDir(kb, config, env.ContentRootPath);
    var path = Path.Combine(dir, safe);
    if (!File.Exists(path)) return Results.NotFound();

    File.Delete(path);
    if (kb is SemanticKnowledgeBase skb) await skb.ReloadAsync();

    var actor = CurrentUser.From(user, authOpts.EffectiveMode);
    await auditLog.RecordAsync(actor.Sub, actor.Role, "knowledge.delete", safe,
        new { deleted = safe }, ct);

    return Results.Ok(new { deleted = safe });
}).WithTags("Knowledge").WithSummary("Delete document from RAG knowledge base (AdminOnly)").RequireAuthorization(AuthPolicies.AdminOnly);

// 12. Admin Audit Trail (read-only, AdminOnly).
app.MapGet("/api/admin/audit-log", async (int? limit, IAdminAuditLog auditLog, CancellationToken ct) =>
{
    var entries = await auditLog.ListRecentAsync(limit ?? 50, ct);
    return Results.Ok(entries);
}).WithTags("Admin").WithSummary("Inspect admin audit trail (AdminOnly)").RequireAuthorization(AuthPolicies.AdminOnly);

app.Run();
return 0;

static void LoadDotEnv()
{
    var dir = Directory.GetCurrentDirectory();
    while (dir is not null)
    {
        var envPath = Path.Combine(dir, ".env");
        if (File.Exists(envPath))
        {
            foreach (var line in File.ReadAllLines(envPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var idx = trimmed.IndexOf('=');
                if (idx <= 0) continue;
                var key = trimmed[..idx].Trim();
                var val = trimmed[(idx + 1)..].Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(val))
                    Environment.SetEnvironmentVariable(key, val);
            }
            break;
        }
        dir = Directory.GetParent(dir)?.FullName;
    }
}

public sealed record AskRequest(string Agent, string SectionId, string Question);
public sealed record PatchSettingsDto(string MinSeverity, int MaxPatches, int MaxRounds, int Parallelism, bool IncludeGherkinInFixed = false);
public sealed record ModelSettingsDto(string DefaultModel, string DefaultEffort);
public sealed record PiiSettingsDto(bool Enabled, Dictionary<string, bool>? Kinds);
public sealed record SettingsUpdateRequest(PatchSettingsDto? Patches, ModelSettingsDto? Model, PiiSettingsDto? Pii);

public partial class Program;
