# C# backend

> The layout below exists as a **prototype** in [`prototype/`](prototype/README.md) (paths there are relative to `doc1/prototype/`). Docker Compose and the Telegram bot are not built yet.

.NET 10, ASP.NET Core minimal API, one solution (`SpecAgent.slnx`) with four projects and a test project. Dependencies point one way: `Api → Agents → Ingestion → Core`.

```text
SpecAgent/
├─ SpecAgent.slnx
├─ src/
│  ├─ SpecAgent.Core/          domain only, no dependencies
│  │  ├─ Documents/TzDocument.cs        TzDocument, TzSection
│  │  ├─ Findings/Models.cs             Severity, RawFinding, Finding, Patch, AuditReport, CriticNames
│  │  └─ Events/                        AuditEvent, EventTypes, IEventStore, InMemoryEventStore
│  ├─ SpecAgent.Ingestion/     parsing + PII
│  │  ├─ Parsing/                       IDocumentParser, Docx/Pdf/Text parsers, SectionBuilder
│  │  └─ Pii/PiiMasker.cs               PiiMasker, IPiiVault, InMemoryPiiVault
│  ├─ SpecAgent.Agents/        the brain
│  │  ├─ Llm/                           ILlmClient, ClaudeLlmClient, StrictJsonSchema, ModelRoutingOptions, PromptLibrary
│  │  ├─ Prompts/*.md                   one Markdown prompt per agent + _shared.md
│  │  ├─ Agents/                        Contracts, SupervisorAgent, CriticAgent, RuleBasedCritic, MetaCriticAgent, PatchLoop
│  │  ├─ Orchestration/                 AuditOrchestrator, AuditState, AuditQueue, AuditWorker
│  │  ├─ Tools/ToolGateway.cs           execute() door
│  │  ├─ Knowledge/KnowledgeBase.cs     IKnowledgeBase, MarkdownKnowledgeBase
│  │  └─ Storage/PostgresEventStore.cs
│  └─ SpecAgent.Api/           HTTP surface + static UI (wwwroot/index.html)
├─ tests/SpecAgent.Tests/      xUnit; FakeLlmClient for offline, deterministic runs
├─ knowledge/                  *.md knowledge base (fintech checklist, НБРК notes, ТЗ standard)
├─ dataset/                    benchmark specs + labels
└─ docker-compose.yml
```

## NuGet packages

| Package | Used for |
|---|---|
| `Anthropic` (official C# SDK) | Claude Messages API |
| `DocumentFormat.OpenXml` | .docx parsing (heading styles → sections, tables → Markdown) |
| `PdfPig` | .pdf text extraction |
| `Npgsql` | PostgreSQL event store |
| `xunit` | tests |

## Key abstractions

```csharp
public interface ILlmClient
{
    // One isolated call for one agent; returns JSON validated against T's schema.
    Task<T> CompleteJsonAsync<T>(AgentCall call, CancellationToken ct = default) where T : class;
}

public sealed record AgentCall { string Agent; string System; string User; int MaxTokens = 16000; }

public interface IEventStore
{
    Task<AuditEvent> AppendAsync(string sessionId, string type, string agent, string message, object? payload = null, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> ReadAsync(string sessionId, long afterSeq = 0, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListOpenSessionsAsync(CancellationToken ct = default);
}

public interface IPiiVault   { string Tokenize(...); string Detokenize(...); }
public interface IKnowledgeBase { IReadOnlyList<KbSnippet> Search(string query, int top = 6); }
public interface IDocumentParser { bool CanParse(string fileName); TzDocument Parse(Stream s, string fileName); }
```

Each has a dev implementation (in-memory, keyword search) and a production one (Postgres, Qdrant, separate vault service) behind the same interface. Tests swap `ILlmClient` for a scripted `FakeLlmClient`, so the whole pipeline — including crash/resume — runs without network or API key.

## Calling Claude (`ClaudeLlmClient`)

One code path for every agent, on the beta Messages endpoint of the official SDK:

```csharp
using Anthropic;
using Anthropic.Models.Beta.Messages;

var parameters = new MessageCreateParams
{
    Model     = route.Model,                       // from ModelRouting for this agent
    MaxTokens = call.MaxTokens,
    System    = call.System,                       // role prompt + _shared.md
    Messages  = [new BetaUserMessageParam { Content = call.User }],
    OutputConfig = new BetaOutputConfig
    {
        Effort = Effort.Medium,                    // from ModelRouting
        Format = new BetaJsonOutputFormat { Schema = StrictJsonSchema.For<T>() },
    },
    // Server-side refusal fallback for Opus 5.5 / Sonnet 5.5; omitted for Haiku.
    Betas     = useFallback ? ["server-side-fallback-2026-07-01"] : null,
    Fallbacks = useFallback ? (BetaFallbacksParam)new Default() : null,
};
BetaMessage response = await client.Beta.Messages.Create(parameters, ct);
```

Then:
- `StopReason == "refusal"` → `LlmException` (only reached when no fallback applied).
- `StopReason == "max_tokens"` → `LlmException` (truncated JSON is never parsed).
- Text blocks are concatenated and deserialised into `T`.
- Usage (input/output tokens), model and latency are logged per agent — this feeds the cost-per-document table.

Notes for this model generation:
- No `temperature` / `top_p` / `budget_tokens`; thinking depth is controlled only by `Effort`. Claude Opus 5.5 defaults to `medium`, so it is set explicitly.
- No assistant prefill; JSON shape comes from structured outputs.
- `ANTHROPIC_API_KEY` is read by the SDK; never put it in config files.

**Strict schemas from C# records.** `StrictJsonSchema.For<T>()` uses `System.Text.Json.Schema.JsonSchemaExporter` and a transform that sets `additionalProperties: false` and marks every property required on every object. The C# record is the single source of truth for each agent's output.

## REST + SSE API

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/audits` (multipart `file`) | Parse → mask → `TZ_INGESTED` → enqueue. Returns `202 { sessionId }` |
| `GET` | `/api/audits/{id}/events` | **SSE** live trace from the log (`id:` = seq, supports `Last-Event-ID`) |
| `GET` | `/api/audits/{id}/document` | Sections for the left pane (demasked) |
| `GET` | `/api/audits/{id}` | `running` / `failed` / `done` + demasked `AuditReport` |
| `GET` | `/api/audits/{id}/findings/{fid}/trace` | Raw findings + judge rationale behind one finding |
| `POST` | `/api/audits/{id}/ask` | `@agent` question on a section |
| `POST` | `/api/audits/{id}/resume` | Manual `wake()` |
| `GET` | `/health` | Liveness |

The SSE endpoint polls the store every 300 ms and sends only `{seq, type, agent, at, message}` — payloads (which may hold the whole document) stay server-side.

## Background processing

- `AuditQueue` — unbounded `Channel<string>` of session ids.
- `AuditWorker : BackgroundService` — on startup re-enqueues `ListOpenSessionsAsync()`; then runs up to 4 audits concurrently, each in its own DI scope with a fresh `AuditOrchestrator`.

## Configuration (`appsettings.json`)

```json
{
  "EventStore": "Postgres",                 // or "InMemory"
  "ConnectionStrings": { "Postgres": "Host=postgres;Database=specagent;Username=specagent;Password=…" },
  "Knowledge": { "Path": "knowledge" },
  "ModelRouting": { "Default": { "Model": "claude-opus-5-5", "Effort": "medium" }, "Agents": { … } },
  "Patches": { "MinSeverity": "Critical", "MaxPatches": 8, "MaxRounds": 2, "Parallelism": 3 }
}
```

Every key can be overridden by environment variables (`ModelRouting__Agents__qa__Model=claude-sonnet-5-5`).

## Docker Compose (planned)

```yaml
services:
  api:
    build: { context: ., dockerfile: src/SpecAgent.Api/Dockerfile }
    ports: ["8080:8080"]
    environment:
      ANTHROPIC_API_KEY: ${ANTHROPIC_API_KEY}
      EventStore: Postgres
      ConnectionStrings__Postgres: Host=postgres;Database=specagent;Username=specagent;Password=specagent
      Knowledge__Path: /app/knowledge
    depends_on: [postgres]
  postgres:
    image: postgres:17
    environment: { POSTGRES_DB: specagent, POSTGRES_USER: specagent, POSTGRES_PASSWORD: specagent }
    volumes: [pgdata:/var/lib/postgresql/data]
  qdrant:
    image: qdrant/qdrant
    profiles: [rag]
volumes: { pgdata: {} }
```

Image: multi-stage — `mcr.microsoft.com/dotnet/sdk:10.0` to build, `mcr.microsoft.com/dotnet/aspnet:10.0` to run; `knowledge/` copied into `/app/knowledge`.

## Telegram bot (second entry point)

A thin client over the same REST API: user sends a .docx/.pdf → bot `POST /api/audits` → follows the SSE stream and edits one status message → sends the score, top Blocker/Critical findings and a link to the web UI. In C# with `Telegram.Bot` as a separate small project, or aiogram if someone on the team prefers Python — it only talks HTTP, so the backend doesn't care.

## Tests to keep green

| Test | Proves |
|---|---|
| IIN checksum, PAN Luhn, mask → demask round trip, session isolation | PII perimeter is correct and reversible |
| Markdown spec splits into numbered sections (`4.3` is its own section) | Findings can point at exact requirements |
| Full pipeline with `FakeLlmClient` | Routing floor, 5 `FINDINGS` events, hallucinated quote rejected, headline defect patched |
| Crash in Meta-Critic, then a fresh orchestrator | `RESUMED` written, critics not re-run, report produced |
| Rules critic on the demo spec | Idempotency, PCI DSS, float money, vague words found offline |
| Strict schema for `CriticOutput` | `additionalProperties: false`, enum values present — what Claude actually receives |
