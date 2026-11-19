# ⚠️ PROTOTYPE — SpecAgent C# reference implementation

> **This is a prototype, not production code.** It exists to prove the design in [`doc1/`](../README.md) works end to end in C#. It is the starting point for the hackathon codebase: expect it to be restructured, and do not deploy it as is.

| | |
|---|---|
| **Model provider** | The design now targets **Qwen** ([models-and-cost.md](../models-and-cost.md)). This prototype still calls **Claude** through `ClaudeLlmClient`; switching means replacing that one class with a Qwen client. Per-section Qdrant matching and the checklist pass are also not built yet. |
| Status | Builds; 11/11 tests pass; smoke-tested locally without an API key (deterministic path only) |
| **Not yet tested** | Real Claude calls (no API key used), PostgreSQL event store, `.docx` / `.pdf` parsing on real bank documents |
| Not included | Dockerfile / Docker Compose, Telegram bot, Qdrant, `.docx` export of patches |
| Language | C# / .NET 10 |

## What it does today

- Upload a ТЗ (`.docx`, `.pdf`, `.md`, `.txt`) → parsed into numbered sections (`4.3` is its own section).
- PII masked before anything is logged or sent to a model: ИИН (mod-11 checksum), card PAN (Luhn), KZ IBAN, phone, e-mail, IPv4, secrets.
- Full agent pipeline from the docs: Supervisor → 4 Claude critics in parallel + 1 deterministic rules critic → quote grounding → Meta-Critic → Patch Writer ⇄ Verifier → report.
- Every step appended to an event log; a crashed run is resumed from the log without re-running finished critics.
- Split-screen web page: document with highlights on the left, live agent trace (SSE) and finding cards on the right; `@agent` questions on a card.
- **Without an API key** the run still finishes: AI critics are logged as failed, and the rules critic plus deterministic merge produce the report. On the demo spec this finds the CVV-in-logs and double-debit Blockers, `float` money, missing timeouts and vague wording.

## Run it

Requires the .NET 10 SDK.

```bash
cd doc1/prototype
```

```bash
dotnet test
```

```bash
export ANTHROPIC_API_KEY=...   # optional; without it only the deterministic path runs
```

```bash
dotnet run --project src/SpecAgent.Api --urls http://localhost:5199
```

Open http://localhost:5199 and upload `dataset/specs/p2p-transfer-bad.md`.

To use PostgreSQL instead of the in-memory log, set `EventStore=Postgres` and `ConnectionStrings__Postgres=...`. The table is created on startup. This path has not been run yet.

## Layout

```text
prototype/
├─ SpecAgent.slnx
├─ src/
│  ├─ SpecAgent.Core/        TzDocument, findings, AuditEvent, IEventStore (+ in-memory)
│  ├─ SpecAgent.Ingestion/   docx / pdf / md parsers, SectionBuilder, PiiMasker + vault
│  ├─ SpecAgent.Agents/      ClaudeLlmClient, prompts (*.md), agents, orchestrator, worker, tool gateway, KB, Postgres store
│  └─ SpecAgent.Api/         REST + SSE endpoints, appsettings.json (model routing), wwwroot/index.html
├─ tests/SpecAgent.Tests/    xUnit with a scripted FakeLlmClient (no network)
├─ knowledge/                knowledge base: FinTech security checklist, НБРК notes, ТЗ writing standard
└─ dataset/                  demo spec with 10 injected defects + labels
```

Where each piece is explained:

| Code | Doc |
|---|---|
| `Agents/SupervisorAgent.cs`, `CriticAgent.cs`, `RuleBasedCritic.cs`, `MetaCriticAgent.cs`, `PatchLoop.cs` | [agents.md](../agents.md) |
| `Orchestration/AuditOrchestrator.cs`, `AuditState.cs`, `AuditWorker.cs` | [multi-agent-orchestration.md](../multi-agent-orchestration.md) |
| `Core/Events/*`, `Agents/Contracts.cs`, `Tools/ToolGateway.cs` | [cross-agent-protocol.md](../cross-agent-protocol.md) |
| `Llm/ClaudeLlmClient.cs`, `Llm/StrictJsonSchema.cs`, `Api/Program.cs` | [backend-csharp.md](../backend-csharp.md) |
| `Ingestion/Pii/PiiMasker.cs` | [pii-and-security.md](../pii-and-security.md) |
| `dataset/`, `knowledge/` | [evaluation-and-demo.md](../evaluation-and-demo.md) |

Prompts are plain Markdown in `src/SpecAgent.Agents/Prompts/`; tune them there, no C# change needed. Models and effort per agent are in `src/SpecAgent.Api/appsettings.json` under `ModelRouting`.

## Tests

| Test | Checks |
|---|---|
| `PiiMaskerTests` (6) | ИИН checksum, masking + exact round trip, same value → same token, session isolation, no false positives on plain numbers |
| `ParserTests` | Markdown spec split into numbered sections, tables kept |
| `Full_pipeline_…` | Routing floor adds money critics, 5 `FINDINGS` events, hallucinated quote rejected, headline defect patched and verified |
| `Crashed_worker_is_resumed_…` | Crash during Meta-Critic, fresh orchestrator resumes, critics not called again |
| `Rule_based_critic_…` | Offline detection of idempotency/timeouts (4.3), PCI DSS (5.1), float money (4.1), vague words (6.1); offline merge keeps different defects apart |
| `Structured_output_schema_is_strict` | Schema sent to Claude is closed (`additionalProperties: false`) and carries the enum values |

## Known limitations

- **PII vault is in memory.** After a restart, finished reports can't be demasked.
- **Critic failure is final for that run.** A critic that failed writes an empty `FINDINGS` event, so a resume does not retry it.
- **Rules critic is narrow.** It reports one vague word per line and pins "missing" defects to a single anchor line.
- **Knowledge base search is keyword-based** (crude stemming), not embeddings.
- **SSE polls the store every 300 ms** instead of using push notifications.
- **UI is a single static page** with no build step and no authentication.
- **НБРК/АРРФР notes in `knowledge/` are a working summary.** Check them against the current regulations before the pitch.
