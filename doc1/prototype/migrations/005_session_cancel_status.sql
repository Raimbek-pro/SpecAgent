-- Migration 005: Allow 'cancelled' status in audit_sessions for explicit user session cancellation.

ALTER TABLE audit_sessions
    DROP CONSTRAINT IF EXISTS audit_sessions_status_check;

ALTER TABLE audit_sessions
    ADD CONSTRAINT audit_sessions_status_check
    CHECK (status IN ('open', 'running', 'completed', 'failed', 'cancelled'));

INSERT INTO schema_migrations (version)
VALUES ('005_session_cancel_status')
ON CONFLICT (version) DO NOTHING;
