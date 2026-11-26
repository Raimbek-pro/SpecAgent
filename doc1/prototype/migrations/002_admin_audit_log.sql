-- Migration 002: Admin audit trail for settings, PII rules, LLM routing, and RAG KB mutations.

CREATE TABLE IF NOT EXISTS admin_audit_log (
    id          bigserial   PRIMARY KEY,
    actor_sub   text        NOT NULL,
    actor_role  text        NOT NULL,
    action      text        NOT NULL,
    target      text        NOT NULL,
    diff_json   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_admin_audit_log_created_at
    ON admin_audit_log (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_admin_audit_log_action
    ON admin_audit_log (action, created_at DESC);

INSERT INTO schema_migrations (version)
VALUES ('002_admin_audit_log')
ON CONFLICT (version) DO NOTHING;
