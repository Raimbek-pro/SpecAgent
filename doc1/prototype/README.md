# SpecAgent Prototype (.NET 10)

This directory contains the C# / .NET 10 solution for **SpecAgent**. For full project documentation, Docker setup, API reference, and quick start instructions, see the root [README.md](../../README.md).

## Solution Structure

- `src/SpecAgent.Core` — Domain models (`TzDocument`, `Finding`, `Patch`, `AuditReport`), event types, and state projection (`AuditState`).
- `src/SpecAgent.Ingestion` — Layout-aware PDF parser (`PdfPig`), DOCX parser (`OpenXML`), Markdown/text parser, `SectionBuilder`, and deterministic `PiiMasker`.
- `src/SpecAgent.Agents` — Multi-agent orchestrator, `SupervisorAgent`, `CriticAgent`, `ChecklistAgent`, `RuleBasedCritic`, `MetaCriticAgent`, `PatchLoop`, `SemanticKnowledgeBase` (Qdrant + embeddings), `PostgresEventStore`, and `OpenAiCompatibleLlmClient`.
- `src/SpecAgent.Api` — Minimal API, SSE live trace, Settings & RAG management API, native PDF/DOCX/MD exporters (`DocumentExporter`), and the FinTech Workbench UI (`wwwroot/index.html`).
- `tests/SpecAgent.Tests` — Unit & parser tests (18 tests, runs offline with 0 LLM tokens).

## Commands

```bash
# Run all unit tests (0 tokens, no API keys required)
dotnet test --nologo

# Run API locally
dotnet run --project src/SpecAgent.Api
```
