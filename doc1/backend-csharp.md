# C# backend

> **In one minute**
> - .NET 10 solution: `Core` (domain) ← `Ingestion` (parsing, PII) ← `Agents` (brain) ← `Api` (HTTP).
> - Models are **Qwen**, called through the OpenAI-compatible API with the official `OpenAI` .NET library and **strict JSON schema output**, so each agent's answer must match its C# record. The same client works for Alibaba Cloud or a self-hosted Qwen.
> - Every external dependency sits behind an interface with an in-memory version for tests.

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
│  │  ├─ Llm/                           ILlmClient, QwenLlmClient (prototype: ClaudeLlmClient), StrictJsonSchema, ModelRoutingOptions, PromptLibrary
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
| `OpenAI` (official .NET library) | Qwen via Model Studio's OpenAI-compatible API, or self-hosted vLLM |
| `Qdrant.Client` | Qdrant vector search for the knowledge base |
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

## Calling Qwen (`QwenLlmClient`)

> The prototype still contains `ClaudeLlmClient`. Switching to Qwen means replacing that **one class**; the agents only see `ILlmClient`. The snippet below is a design sketch to be compile-checked when we implement it.

Alibaba Model Studio exposes an **OpenAI-compatible** endpoint, so we use the official **OpenAI .NET library** (`OpenAI` NuGet) pointed at it. The same code works against a **self-hosted** open-weight Qwen served by vLLM: only the endpoint and model name change.

```csharp
using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

var chat = new ChatClient(
    model: route.Model,                                         // e.g. "qwen3.7-plus", from ModelRouting
    credential: new ApiKeyCredential(Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY")!),
    options: new OpenAIClientOptions
    {
        Endpoint = new Uri(config["Llm:Endpoint"]!),             // https://dashscope-intl.aliyuncs.com/compatible-mode/v1
    });                                                         // or http://vllm:8000/v1 on-premise

var options = new ChatCompletionOptions
{
    MaxOutputTokenCount = call.MaxTokens,
    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
        jsonSchemaFormatName: typeof(T).Name,
        jsonSchema: BinaryData.FromObjectAsJson(StrictJsonSchema.For<T>()),
        jsonSchemaIsStrict: true),                              // json_schema + strict, as Model Studio documents
};

ChatCompletion result = await chat.CompleteChatAsync(
    [new SystemChatMessage(call.System), new UserChatMessage(call.User)], options, ct);
```

Then:
- `FinishReason == Length` → `LlmException` (truncated JSON is never parsed).
- `FinishReason == ContentFilter`, or an API error → retry once, then the **fallback model** for that tier from config (`Fallbacks` in [models-and-cost.md](models-and-cost.md)).
- The text is deserialised into `T`.
- Usage (input/output tokens), model and latency are logged per agent; this feeds the cost-per-document table.

Notes for Qwen:
- **Pick models that support `json_schema`:** `qwen4-max`, Qwen3.7-Plus, Qwen3.7/3.8-Flash, Qwen3.7/3.8-Max. **Not** Qwen3.6-Plus, Qwen3.5, or open-weight models via the API.
- **Thinking mode + `json_schema`:** not clearly documented; test it on day one. If it fails, run agents in non-thinking mode.
- **`tool_choice: "required"` is not supported** for Qwen. We use `auto` (default), so this doesn't affect us.
- Schemas must avoid `multipleOf`, `uniqueItems`, `patternProperties` and similar keywords. `StrictJsonSchema` doesn't emit them.
- `DASHSCOPE_API_KEY` is read from the environment; never put it in config files.

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
  "Llm": { "Endpoint": "https://dashscope-intl.aliyuncs.com/compatible-mode/v1" },
  "ModelRouting": { "Default": { "Model": "qwen3.7-plus" }, "Agents": { … }, "Fallbacks": { … } },
  "Qdrant": { "Url": "http://qdrant:6334", "Collection": "rules", "EmbeddingModel": "Qwen3-Embedding-0.6B" },
  "Patches": { "MinSeverity": "Critical", "MaxPatches": 8, "MaxRounds": 2, "Parallelism": 3 }
}
```

Every key can be overridden by environment variables (`ModelRouting__Agents__qa__Model=qwen3.8-flash`).

## Docker Compose (planned)

```yaml
services:
  api:
    build: { context: ., dockerfile: src/SpecAgent.Api/Dockerfile }
    ports: ["8080:8080"]
    environment:
      DASHSCOPE_API_KEY: ${DASHSCOPE_API_KEY}
      EventStore: Postgres
      ConnectionStrings__Postgres: Host=postgres;Database=specagent;Username=specagent;Password=specagent
      Knowledge__Path: /app/knowledge
    depends_on: [postgres]
  postgres:
    image: postgres:17
    environment: { POSTGRES_DB: specagent, POSTGRES_USER: specagent, POSTGRES_PASSWORD: specagent }
    volumes: [pgdata:/var/lib/postgresql/data]
  qdrant:
    image: qdrant/qdrant                 # always on: every audit consults it
  embeddings:
    # local embedding model (Qwen3-Embedding) so ТЗ text never leaves for embedding;
    # served e.g. by vLLM or Text Embeddings Inference
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
| Strict schema for `CriticOutput` | `additionalProperties: false`, enum values present: what the model actually receives |
