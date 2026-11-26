-- Migration 004: Per-session token usage, LLM call count, and estimated USD cost telemetry.

ALTER TABLE audit_sessions
    ADD COLUMN IF NOT EXISTS input_tokens       bigint         NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS output_tokens      bigint         NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS estimated_cost_usd numeric(12, 6) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS llm_calls          int            NOT NULL DEFAULT 0;

INSERT INTO schema_migrations (version)
VALUES ('004_session_telemetry')
ON CONFLICT (version) DO NOTHING;
