# Backend Engineering Handoff & TODO (`BACKEND_TODO.md`)

This document summarizes recent AI/API/UI changes already merged into `main` and outlines the prioritized backend & security backlog for production readiness and hackathon delivery.

---

## 1. Summary of Recent Changes Already Implemented

The following endpoints and capabilities are live and verified (18/18 unit tests passing, Docker stack running on `localhost:5199`):

- **Runtime Settings API (`GET/PUT /api/settings`):**
  - Mutates `IOptions<PatchOptions>` (`MinSeverity`, `MaxPatches`, `MaxRounds`, `Parallelism`, `IncludeGherkinInFixed`), `IOptions<ModelRoutingOptions>` (`DefaultModel`, `DefaultEffort`), and `PiiMasker` (`Enabled`, `EnabledKinds`).
  - Currently guarded by a demo `X-SpecAgent-Role` header check (`403 Forbidden` when `analyst`).
- **Dynamic RAG Knowledge Base API (`GET/POST/DELETE /api/knowledge`):**
  - Lists `.md` reference files in `Knowledge:Path`, accepts `.md/.txt/.pdf/.docx` uploads (converting PDF/DOCX via `DocumentParserRegistry`), writes to `knowledge/`, and calls `SemanticKnowledgeBase.ReloadAsync()` to re-index Qdrant.
- **Native Document Exporters (`SpecAgent.Api/Export/DocumentExporter.cs`):**
  - `GET /api/audits/{id}/export/fixed.pdf` (QuestPDF + `fonts-dejavu-core` Cyrillic)
  - `GET /api/audits/{id}/export/fixed.docx` (OpenXML)
  - `GET /api/audits/{id}/original.pdf` (serves saved original PDF from `uploads/` or renders original)
- **International PII Masking (`PiiMasker.cs`):**
  - Expanded phone regex to mask international E.164/formatted numbers (10–15 digits) as well as local CIS `8-xxx` numbers, with per-kind toggles.
- **Recent Sessions Listing (`GET /api/audits/recent`):**
  - Queries `audit_sessions` + `audit_events` in PostgreSQL to return `sessionId`, `status`, `fileName`, `title`, `score`, and `updatedAt`.

---

## 2. Prioritized Backend TODOs

### P0 — Hackathon Submission Must-Haves

1. **Single-Command CLI Runner (BCC-HUB Requirement)**
   - Add CLI mode to `SpecAgent.Api` (or a dedicated `SpecAgent.Cli` project) supporting:
     ```bash
     dotnet run --project src/SpecAgent.Api -- --input <path/to/spec.pdf> --output <path/to/report.md>
     ```
   - Reuse `DocumentParserRegistry` → `PiiMasker` → `AuditOrchestrator.RunAsync` → `ToolGateway.RenderReportMarkdown`, returning exit code `0` on completion and writing the demasked Markdown report to `--output`.
   - Document the exact CLI command in root `README.md`.

2. **Persistent Volumes for `uploads/` and `knowledge/` in `docker-compose.yml`**
   - Currently uploaded original PDFs (`/app/uploads`) and custom bank RAG documents (`/app/knowledge`) live inside the container filesystem and reset on `docker compose up --build`.
   - Mount named Docker volumes (`specagent_uploads:/app/uploads`, `specagent_knowledge:/app/knowledge`) or store uploaded blobs in PostgreSQL / S3-compatible object storage (MinIO).

### P1 — Roles, Authentication & Access Control (RBAC)

3. **Real Authentication & Bank SSO (Replace Demo `X-SpecAgent-Role` Header)**
   - Right now `IsAnalystRole(HttpRequest)` trusts the client-supplied `X-SpecAgent-Role` header for UI demo purposes.
   - Replace with ASP.NET Core JWT Bearer / OIDC authentication (Keycloak / Azure AD / corporate IdP):
     - Define policies: `[Authorize(Policy = "AdminOnly")]` on `PUT /api/settings`, `POST /api/knowledge`, `DELETE /api/knowledge/{name}`.
     - Define `[Authorize(Policy = "AnalystOrAdmin")]` on `/api/audits/*`.
     - Return the authenticated user's role & name via `GET /api/me` so the frontend hides/disables admin controls automatically without a manual switcher.

4. **Admin Audit Trail in PostgreSQL**
   - Add an `admin_audit_log` table (`id`, `actor_sub`, `actor_role`, `action`, `target`, `diff_json`, `created_at`) recording every change to Settings, PII rules, LLM routing, and RAG knowledge base uploads/deletions.

### P2 — Security & Production Hardening

5. **Persistent, Encrypted PII Vault (`PostgresPiiVault`)**
   - Currently `IPiiVault` is `InMemoryPiiVault`, meaning demasking of old sessions does not survive API restarts (reopened sessions show `[PHONE_1]`/`[IBAN_1]` after restart).
   - Implement `PostgresPiiVault` storing `session_id`, `token`, and **AES-256-GCM encrypted** `ciphertext` (key loaded from KMS / Vault / `PII_VAULT_KEY` env var) with automatic TTL purge (e.g., 30 days) for GDPR / Kazakhstan Personal Data Law compliance.

6. **Upload Validation, Size Limits & Rate Limiting**
   - Enforce `RequestSizeLimit` (e.g., 25 MB max) and magic-byte validation (`%PDF-`, `PK\x03\x04` for `.docx`) on `POST /api/audits` and `POST /api/knowledge`.
   - Add `Microsoft.AspNetCore.RateLimiting` (fixed/sliding window per user/IP) on `POST /api/audits` and `POST /api/audits/{id}/ask` to prevent LLM token-exhaustion abuse.
   - Add Content-Security-Policy (CSP), `X-Content-Type-Options: nosniff`, and strict CORS policy.

7. **Persistent Settings in PostgreSQL**
   - Persist `PatchOptions`, `ModelRoutingOptions`, and `PiiMasker` settings in a `system_settings` table so Admin changes made in the UI survive container restarts and stay synchronized across horizontally scaled API replicas.

### P3 — Testing & Observability

8. **Integration Tests for New Endpoints**
   - Add `WebApplicationFactory` integration tests covering:
     - `GET/PUT /api/settings` (including `403 Forbidden` for non-admin roles).
     - `GET/POST/DELETE /api/knowledge` (including path-traversal rejection).
     - `GET /api/audits/{id}/export/fixed.pdf` and `fixed.docx` (verify non-empty valid PDF/DOCX bytes & `IncludeGherkinInFixed` toggle).
     - `PiiMaskerTests` for international phone numbers (`+1`, `+44`, `+998`, `+7`, `8`).
9. **Structured Telemetry & Cost Tracking**
   - Record per-session input/output token usage and estimated USD cost in `audit_sessions` (surfaced in `/api/audits/recent` and Prometheus/OpenTelemetry metrics).
