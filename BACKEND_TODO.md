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

### P0 — Hackathon Submission Must-Haves (Done ✅)

1. **✅ Single-Command CLI Runner (BCC-HUB Requirement)**
   - Implemented in `SpecAgent.Api/Cli/AuditCliRunner.cs` and wired in `Program.cs`:
     ```bash
     dotnet run --project src/SpecAgent.Api -- --input <path/to/spec.pdf> --output <path/to/report.md>
     ```
   - Reuses `DocumentParserRegistry` → `PiiMasker` → `AuditOrchestrator.RunAsync` → `ToolGateway.RenderReportMarkdown`, returns exit code `0` on completion, writes the demasked Markdown report to `--output`, and optionally exports `--fixed` (`.md`/`.pdf`/`.docx`).
   - Documented in root `README.md` and covered by `CliRunnerTests`.

2. **✅ Persistent Volumes for `uploads/` and `knowledge/` in `docker-compose.yml`**
   - Mounted named Docker volumes `specagent_uploads:/app/uploads` and `specagent_knowledge:/app/knowledge` on the `api` service so uploaded originals and custom bank RAG documents survive `docker compose up --build`.

### P1 — Roles, Authentication & Access Control (RBAC) (Done ✅)

3. **✅ Real Authentication & Bank SSO (Replaced Manual `X-SpecAgent-Role` Check)**
   - Implemented in `SpecAgent.Api/Auth/AuthExtensions.cs` using ASP.NET Core `JwtBearer` + Authorization Policies (`AdminOnly`, `AnalystOrAdmin`):
     - Supports OIDC IdPs (`Auth:Authority` / `Auth:Audience` for Keycloak / Azure AD, including `realm_access.roles` normalization) and symmetric HS256 (`JWT_SIGNING_KEY`).
     - Enforces `RequireAuthorization("AdminOnly")` on `PUT /api/settings`, `POST /api/knowledge`, `DELETE /api/knowledge/{name}`, and `GET /api/admin/audit-log`; `RequireAuthorization("AnalystOrAdmin")` on `/api/audits/*`, `GET /api/settings`, `GET /api/knowledge`, and `GET /api/me`.
     - `GET /api/me` returns `{ authenticated, authMode, sub, name, role, isAdmin }`.
     - Falls back cleanly to `DemoHeaderAuthHandler` only when `Auth:Mode=Auto` and no IdP/signing key is configured, while still enforcing the exact same policies.

4. **✅ Admin Audit Trail in PostgreSQL (`admin_audit_log`)**
   - Added SQL migration `doc1/prototype/migrations/002_admin_audit_log.sql` and `IAdminAuditLog` (`PostgresAdminAuditLog` + `InMemoryAdminAuditLog`).
   - Automatically records `actor_sub`, `actor_role`, `action`, `target`, and JSONB `diff_json` (`before`/`after`) for `settings.update`, `knowledge.upload`, and `knowledge.delete`, queryable via `GET /api/admin/audit-log`.
   - Covered by `AuthAndAdminAuditTests` (`WebApplicationFactory` JWT/RBAC + live PostgreSQL tests).

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
