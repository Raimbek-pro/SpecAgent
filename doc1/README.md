# SpecAgent — documentation (doc1)

SpecAgent audits a software specification (ТЗ) for a bank: it finds contradictions, security and compliance gaps, missing reliability guarantees and vague wording, then writes a verified fix for each serious defect.

It is built as a **hierarchical multi-agent system** with a **C# / .NET 10 backend**, Claude models through the official Anthropic C# SDK, a deterministic PII-masking perimeter, and an append-only event log that lets any agent worker crash and resume without losing work.

> Status: design documentation plus a **prototype** C# implementation in [`prototype/`](prototype/README.md). The prototype proves the design end to end; it is not production code.

## Reading order

| # | Document | What it answers |
|---|---|---|
| 1 | [architecture.md](architecture.md) | The whole system on one page: security perimeter, brain / session / hands, data flow |
| 2 | [agents.md](agents.md) | Every agent: role, input, output contract, model, what happens when it fails |
| 3 | [multi-agent-orchestration.md](multi-agent-orchestration.md) | How the agents are arranged: routing, parallel fan-out, judge, patch loop, resume after crash |
| 4 | [cross-agent-protocol.md](cross-agent-protocol.md) | How agents exchange work: the event log, event types, contracts, conflict resolution, human @mentions |
| 5 | [backend-csharp.md](backend-csharp.md) | .NET solution layout, key classes, Anthropic SDK usage, REST + SSE API, configuration, Docker |
| 6 | [pii-and-security.md](pii-and-security.md) | PII masking and the vault, prompt-injection defence, why the bank can trust it |
| 7 | [evaluation-and-demo.md](evaluation-and-demo.md) | Benchmark dataset, Precision/Recall, demo script, timeline to the pitch |
| 8 | [prototype/README.md](prototype/README.md) | ⚠️ **Prototype** C# code: what works, how to run, what is not tested yet |

## The system in five sentences

1. The ТЗ (.docx / .pdf / .md) is parsed into numbered sections and **PII is masked** before any model sees it.
2. A **Supervisor** classifies the document and routes sections to the specialist critics it needs.
3. Four **domain critics** (logic, QA, compliance, distributed systems) and one **deterministic rules critic** review in parallel, each in an isolated context.
4. A **Meta-Critic** checks every quote against the document (deterministic), merges duplicates, rejects false positives and assigns severity; the score is computed by formula.
5. A **Patch Writer ⇄ Patch Verifier** loop writes "before → after" fixes with Gherkin acceptance criteria, then the report is **demasked** and shown in a split-screen UI.

## Tech stack

| Layer | Choice |
|---|---|
| Backend | C# / .NET 10, ASP.NET Core minimal API, `BackgroundService` worker |
| LLM | Claude via the `Anthropic` NuGet package (structured outputs, effort, server-side refusal fallbacks) |
| Models | Claude Opus 5.5 (supervisor, judge, verifier), Claude Sonnet 5.5 (critics, writer), Claude Haiku 5.5 (QA checker) — configurable |
| Parsing | DocumentFormat.OpenXml (.docx), PdfPig (.pdf), plain Markdown/text |
| Session log | PostgreSQL (append-only `audit_events` table); in-memory store for dev/tests |
| Knowledge base | Markdown files, keyword search; Qdrant as a drop-in upgrade |
| UI | Split screen: ТЗ text with highlights on the left, live agent trace and finding cards on the right (SSE) |
| Second entry point | Telegram bot calling the same REST API |
| Infra | Docker Compose: `api` + `postgres` (+ `qdrant` profile) — `docker compose up` |
