<p align="center">
  <img src="assets/mascot.png" alt="SpecAgent Mascot" width="220" />
</p>

<h1 align="center">SpecAgent</h1>

<p align="center">
  <strong>Multi-Agent FinTech Requirements Auditor &amp; Auto-Fixer</strong><br/>
  ISO/IEC/IEEE 29148:2018 · IEEE 830 · PCI DSS · NBK / ARRFR Compliance · RAG (Qdrant) · Event Sourcing (PostgreSQL)
</p>

---

## Overview

**SpecAgent** is a multi-agent AI workbench built in **C# / .NET 10** for auditing, scoring, and automatically rewriting banking & FinTech technical specifications (`.pdf`, `.docx`, `.md`, `.txt`).

Unlike single-prompt LLM wrappers, SpecAgent runs a **4-level deterministic + multi-agent pipeline** backed by a **PostgreSQL Event Store**, **Qdrant semantic RAG**, and **pre-LLM PII masking/demasking**:

1. **Ingestion & PII Masking (`SpecAgent.Ingestion`)**
   - Layout-aware PDF parser (`PdfPig`) with ruled & borderless table extraction into Markdown tables.
   - Word `.docx` parser (`DocumentFormat.OpenXml`) preserving headings and tables.
   - Deterministic PII masker runs **before** any text leaves the perimeter: masks secrets/passwords, IBAN (`KZ...`), PAN (Luhn-validated), Kazakhstan IIN/BIN (mod-11 checksum), international & local phone numbers (`+1`, `+44`, `+7`, `+998`, `8...`), emails, and IPv4 addresses. Real PII is restored only at export/viewing time.
2. **Parallel Multi-Agent Audit (`SpecAgent.Agents`)**
   - **Supervisor Agent** classifies domain/complexity and routes sections to specialists.
   - **Deterministic Rule Engine (`RuleBasedCritic`)** catches high-precision defects at zero token cost (e.g., mutating `GET` endpoints, HTTP 200 with `"result": "ERROR"`, `TBD`/stub sections).
   - **Specialist Critics (parallel fan-out):** `logic`, `qa`, `compliance`, `distributed`, plus a single-call **Batch Checklist Agent** (`CHK-01`..`CHK-12`, ISO 29148).
   - **Semantic RAG (`Qdrant` + `gemini-embedding-001`)** grounds findings in bank standards, ISO/IEC/IEEE 29148, and custom uploaded bank reference documents.
3. **Grounding & Arbitration (`MetaCriticAgent`)**
   - Verifies every finding's verbatim quote against the source document (rejecting hallucinations).
   - Deduplicates overlapping findings, assigns final severity (`Blocker`, `Critical`, `Major`, `Minor`), and computes the **Quality Score (0–100)** + **Go / Conditional Go / No-Go** verdict.
4. **Full-Document Auto-Patching & Native Export (`PatchLoop` + `DocumentExporter`)**
   - Groups all findings per section and rewrites every flagged section into a production-ready version.
   - Runs a `PatchWriter → PatchVerifier` verification loop on high-risk (`Blocker` / `Critical`) sections to ensure zero contradictions with neighboring sections.
   - Exports the finished specification in its **original format**: clean **`.pdf`** (`QuestPDF` + Cyrillic `DejaVu Sans`), clean **`.docx`** (`OpenXML`), clean **`.md`**, plus the official audit **`report.md`**.

---

## What Was Built (AI Engineer Summary)

- **LLM Gateway (`OpenAiCompatibleLlmClient`):** Universal OpenAI-compatible client (Kimi K3 / Moonshot AI, Gemini, Qwen/vLLM) with native Strict JSON Schema validation (`StrictJsonSchema.cs`), configurable concurrency, pacing, retries, and fallback routing.
- **Knowledge Base & Semantic RAG (`SemanticKnowledgeBase` + `FreeEmbeddingClient`):** Qdrant vector search + local embedding cache + live bank reference document upload/deletion (`POST/DELETE /api/knowledge` supporting `.md`, `.txt`, `.pdf`, `.docx`).
- **ISO 29148 & FinTech Standards:** Added `iso-29148-requirements-standard.md`, `mandatory-checklist.md`, `ChecklistAgent`, and expanded `RuleBasedCritic`.
- **Token & Latency Optimization:** Smart Verifier fast-path (verifies `Blocker`/`Critical`, direct accept for `Major`/`Minor`), grouped per-section patching, compact `PatchWriter` prompts (`~2.5x–3x` faster and cheaper).
- **Native Document Exporters (`DocumentExporter.cs`):** Real `.pdf` (`QuestPDF`) and `.docx` (`OpenXML`) generation with headings, bullet lists, code blocks, and bordered tables, plus configurable Gherkin inclusion (`IncludeGherkinInFixed`).
- **Zed / Xcode-Inspired FinTech Workbench UI (`wwwroot/index.html`):**
  - Locked 2-pane split workbench with resizable divider and collapsible project/history sidebar (`PostgreSQL` cache — reopens past audits at **0 LLM tokens**).
  - Left pane: `Quality Score` executive summary + red-bordered flagged sections (click-to-jump to right pane) + **Original PDF / Document** viewer tab.
  - Right pane: **Live 5-stage progress pipeline** with real-time agent pills during runs; once complete, **Fixed Sections** view with expandable rationale/proof/`@agent` Q&A + **Clean PDF / DOCX / Document** preview tab + format-matching download button.
  - **Settings & RAG Modal (`⚙ Настройки и RAG`):** Live tuning for patch policies (`MinSeverity`, `MaxPatches`, `MaxRounds`, `Parallelism`, `IncludeGherkinInFixed`), RAG bank document management, PII toggles, LLM model/effort selection, and Admin vs. Analyst read-only RBAC mode.

---

## Quick Start

### Prerequisites

- **Docker** (Docker Desktop or OrbStack on macOS) — recommended for full stack (`PostgreSQL` + `Qdrant` + `API`)
- Or **.NET 10 SDK** for local development & unit tests
- API keys:
  - `MOONSHOT_API_KEY` (Kimi K3 / Moonshot AI — primary LLM)
  - `GEMINI_API_KEY` (optional, free `gemini-embedding-001` for Qdrant semantic embeddings; falls back to keyword search if omitted)

### 1. Configure Environment Variables

Copy the example environment file and add your keys (`.env` is gitignored — **never commit real keys**):

```bash
cp .env.example .env
```

Edit `.env`:

```env
MOONSHOT_API_KEY=your_moonshot_api_key_here
GEMINI_API_KEY=your_gemini_api_key_here
```

### 2. Run Full Stack with Docker Compose (Recommended)

```bash
docker compose up --build -d
```

Services started:
- **SpecAgent Web UI & API:** http://localhost:5199
- **Health check:** http://localhost:5199/health
- **Qdrant Dashboard:** http://localhost:6333/dashboard
- **PostgreSQL:** `localhost:5432` (`specagent` / `specagent`)
- **Persistent volumes:** `pgdata` (PostgreSQL), `qdrant_data` (Qdrant), `specagent_uploads` (`/app/uploads`), and `specagent_knowledge` (`/app/knowledge`) survive `docker compose up --build`.

To rebuild only the API container after code changes (preserving PostgreSQL, Qdrant, uploads & custom RAG files):

```bash
docker compose up --build -d api
```

### 3. Single-Command CLI Mode (BCC-HUB Submission)

Audit any `.pdf`, `.docx`, `.md`, or `.txt` specification with a single command and write the official demasked Markdown report (`report.md`):

```bash
cd doc1/prototype
dotnet run --project src/SpecAgent.Api -- --input dataset/specs/bcc-card-expenses.md --output report.md
```

Or directly from the repository root:

```bash
dotnet run --project doc1/prototype/src/SpecAgent.Api -- --input doc1/prototype/dataset/specs/p2p-transfers-tz.pdf --output report.md
```

Optional flags:
- `--fixed <path>` — also export the auto-patched specification (`.md`, `.pdf`, or `.docx` chosen by file extension)
- `--with-gherkin` — include Gherkin acceptance criteria in the `--fixed` output
- `--help` — show CLI usage

### 4. Run Web UI Locally (Without Docker)

```bash
cd doc1/prototype
dotnet run --project src/SpecAgent.Api
```

### 5. Run Unit Tests (0 LLM Tokens)

All unit tests use deterministic fakes and require no API keys:

```bash
cd doc1/prototype
dotnet test --nologo
```

---

## Key API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/health` | Service health check (anonymous) |
| `GET` | `/api/me` | Authenticated user identity, role (`admin`/`analyst`), and auth mode (`jwt`/`demo`) |
| `GET` | `/api/audits/recent` | List recent PostgreSQL audit sessions (0 tokens) |
| `POST` | `/api/audits` | Upload `.pdf`, `.docx`, `.md`, or `.txt` to start an audit |
| `GET` | `/api/audits/{id}/events` | Live Server-Sent Events (SSE) execution trace |
| `GET` | `/api/audits/{id}/document` | Demasked parsed document sections |
| `GET` | `/api/audits/{id}` | Final audit report JSON (`findings`, `patches`, `qualityScore`) |
| `POST` | `/api/audits/{id}/ask` | Human-in-the-loop `@agent` Q&A on a specific section |
| `GET` | `/api/audits/{id}/export/report.md` | Download official Markdown audit report |
| `GET` | `/api/audits/{id}/export/fixed.md` | Download clean patched Markdown specification |
| `GET` | `/api/audits/{id}/export/fixed.pdf` | View/download (`?download=1`) clean patched PDF |
| `GET` | `/api/audits/{id}/export/fixed.docx` | Download clean patched Microsoft Word `.docx` |
| `GET` | `/api/audits/{id}/original.pdf` | View original uploaded PDF (or rendered original) |
| `GET` / `PUT` | `/api/settings` | Read (`AnalystOrAdmin`) / update (`AdminOnly`) runtime audit, PII, and model settings |
| `GET` / `POST` / `DELETE` | `/api/knowledge` | List (`AnalystOrAdmin`) / upload & delete (`AdminOnly`) RAG reference documents |
| `GET` | `/api/admin/audit-log` | Inspect PostgreSQL admin audit trail (`AdminOnly`) |

### Authentication & RBAC

SpecAgent uses ASP.NET Core Authentication & Authorization policies (`AdminOnly`, `AnalystOrAdmin`):
- **Production / Bank SSO (`Auth:Mode = Jwt` or `Auto` with `AUTH_AUTHORITY` / `JWT_SIGNING_KEY`):** validates JWT Bearer tokens (OIDC discovery for Keycloak / Entra ID, including `realm_access.roles`, or HS256 `JWT_SIGNING_KEY`). Header spoofing is rejected with `401 Unauthorized`; analysts receive `403 Forbidden` on admin endpoints.
- **Local Demo (`Auth:Mode = Auto` with no IdP configured):** maps `X-SpecAgent-Role` through the same ASP.NET Core policy pipeline so the local workbench works out of the box.
- Every change to Settings, PII rules, LLM routing, and RAG Knowledge Base is recorded in PostgreSQL table `admin_audit_log` (`migrations/002_admin_audit_log.sql`) and persisted across restarts in `system_settings` (`migrations/003_pii_vault_and_settings.sql`).
- **Encrypted PII Vault (`PostgresPiiVault`):** when running with `EventStore=Postgres`, real PII values are encrypted at rest with **AES-256-GCM** (keyed via `PII_VAULT_KEY`) with automatic TTL retention purge (`PII_VAULT_TTL_DAYS=30`) so reopened sessions demask cleanly even after container restarts.
- **Upload & API Hardening:** 25 MB upload size limit, magic-byte file verification (`%PDF-`, `PK\x03\x04`, non-binary text), per-user/IP sliding-window rate limiting (`429`), strict same-origin CORS, CSP, and `X-Content-Type-Options: nosniff`.

---

## Roadmap & Backend Handoff

See [BACKEND_TODO.md](BACKEND_TODO.md) for the prioritized backend engineering backlog (enterprise RBAC/OIDC, encrypted PII Vault, persistent settings/storage, CLI single-command runner, and security hardening).
