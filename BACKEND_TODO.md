# Backend Engineering Handoff & TODO (`BACKEND_TODO.md`)

This document summarizes recent AI/API/UI changes already merged into `main` and outlines the prioritized backend & security backlog for production readiness and hackathon delivery.

---

## 1. Summary of Recent Changes Already Implemented

The following endpoints and capabilities are live and verified (50/50 unit & PostgreSQL integration tests passing, 0 build warnings, Docker stack running on `localhost:5199`):

- **Runtime Settings API (`GET/PUT /api/settings`):**
  - Mutates `IOptions<PatchOptions>` (`MinSeverity`, `MaxPatches`, `MaxRounds`, `Parallelism`, `IncludeGherkinInFixed`), `IOptions<ModelRoutingOptions>` (`DefaultModel`, `DefaultEffort`, preserving per-agent hybrid overrides), and `PiiMasker` (`Enabled`, `EnabledKinds`), persisted in PostgreSQL `system_settings`.
  - Guarded by ASP.NET Core Authorization policies (`AdminOnly` for writes, `AnalystOrAdmin` for reads).
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

### P2 — Security & Production Hardening (Done ✅)

5. **✅ Persistent, Encrypted PII Vault (`PostgresPiiVault`)**
   - Implemented in `SpecAgent.Agents/Storage/PostgresPiiVault.cs` + migration `003_pii_vault_and_settings.sql`.
   - Encrypts PII values with **AES-256-GCM** (random 12-byte nonce, 16-byte tag, AAD bound to `session_id|token`, keyed HMAC-SHA256 deduplication index, master key loaded from `PII_VAULT_KEY`) with automatic TTL retention purge (`PiiVaultCleanupWorker`, default 30 days).

6. **✅ Upload Validation, Size Limits, Rate Limiting & Security Headers**
   - Implemented in `SpecAgent.Api/Security/SecurityExtensions.cs` (`UploadValidator`, `AddSpecAgentSecurity`, `UseSpecAgentSecurityHeaders`):
     - 25 MB upload limit (`413 Payload Too Large`) + magic-byte validation (`%PDF-`, `PK\x03\x04`, binary NUL check) on `POST /api/audits` and `POST /api/knowledge`.
     - Per-user/IP sliding-window rate limiting (`Microsoft.AspNetCore.RateLimiting`, `429 Too Many Requests`) on `POST /api/audits` and `POST /api/audits/{id}/ask`.
     - Added `Content-Security-Policy`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`, `Referrer-Policy`, and strict CORS policy (`StrictCors`).

7. **✅ Persistent Settings in PostgreSQL (`system_settings`)**
   - Implemented `ISystemSettingsStore` (`PostgresSystemSettingsStore` + `InMemorySystemSettingsStore`) persisting `PatchOptions`, `ModelRoutingOptions`, and `PiiMasker` settings in `system_settings` and hydrating them automatically on API startup.
   - Covered by `SecurityAndPersistenceTests` (unit, `WebApplicationFactory`, and live PostgreSQL tests).

### P3 — Testing & Observability (Done ✅)

8. **✅ Integration Tests for New Endpoints**
   - Covered across `AuthAndAdminAuditTests`, `SecurityAndPersistenceTests`, `EndpointExportAndTelemetryTests`, and `PiiMaskerTests`:
     - `GET/PUT /api/settings` (including `403 Forbidden` for non-admin roles and JWT/Keycloak RBAC).
     - `GET/POST/DELETE /api/knowledge` (including path-traversal rejection `..%2F`, magic-byte and size validation).
     - `GET /api/audits/{id}/export/fixed.pdf` and `fixed.docx` (verified valid `%PDF-` / OpenXML bytes and `IncludeGherkinInFixed` toggle).
     - `PiiMaskerTests` for international & CIS phone numbers (`+1`, `+44`, `+998`, `+7`, `8`), labeled synthetic IIN, and `EnabledKinds` toggling.

9. **✅ Structured Telemetry & Cost Tracking**
   - Added migration `004_session_telemetry.sql`, `AuditTelemetryContext`, `ModelPricing`, `ISessionUsageStore` (`PostgresSessionUsageStore` + `InMemorySessionUsageStore`), and OpenTelemetry `Meter("SpecAgent")` + Prometheus `GET /metrics` endpoint (`SpecAgentMetrics`).
   - Per-session `inputTokens`, `outputTokens`, `estimatedCostUsd`, and `llmCalls` are recorded automatically by `OpenAiCompatibleLlmClient` / `ClaudeLlmClient` and surfaced in `GET /api/audits/recent`.
