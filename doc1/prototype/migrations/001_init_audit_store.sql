-- Migration 001: Initial durable audit session and append-only event log schema.
-- Executed automatically by Postgres on fresh container init (/docker-entrypoint-initdb.d)
-- and idempotently by PostgresEventStore.EnsureSchemaAsync() on API startup.

CREATE TABLE IF NOT EXISTS schema_migrations (
    version    text        PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS audit_sessions (
    session_id text        PRIMARY KEY,
    status     text        NOT NULL DEFAULT 'open'
                           CHECK (status IN ('open', 'running', 'completed', 'failed')),
    last_seq   bigint      NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS audit_events (
    session_id text        NOT NULL REFERENCES audit_sessions(session_id) ON DELETE CASCADE,
    seq        bigint      NOT NULL,
    type       text        NOT NULL,
    agent      text        NOT NULL,
    at         timestamptz NOT NULL,
    message    text        NOT NULL,
    payload    jsonb,
    PRIMARY KEY (session_id, seq)
);

-- Fast wake() query on worker startup: only scans unfinished sessions.
CREATE INDEX IF NOT EXISTS idx_audit_sessions_open
    ON audit_sessions (created_at)
    WHERE status IN ('open', 'running');

-- Fast filtering by event type within a session and global chronological audit queries.
CREATE INDEX IF NOT EXISTS idx_audit_events_session_type
    ON audit_events (session_id, type);

CREATE INDEX IF NOT EXISTS idx_audit_events_at
    ON audit_events (at DESC);

INSERT INTO schema_migrations (version)
VALUES ('001_init_audit_store')
ON CONFLICT (version) DO NOTHING;
