-- Migration 003: Encrypted PostgreSQL PII Vault (AES-256-GCM + TTL) and persistent system_settings.

CREATE TABLE IF NOT EXISTS pii_vault_entries (
    session_id text        NOT NULL,
    token      text        NOT NULL,
    kind       text        NOT NULL,
    value_hmac bytea       NOT NULL,
    nonce      bytea       NOT NULL,
    ciphertext bytea       NOT NULL,
    tag        bytea       NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (session_id, token),
    UNIQUE (session_id, kind, value_hmac)
);

CREATE INDEX IF NOT EXISTS idx_pii_vault_expires_at
    ON pii_vault_entries (expires_at);

CREATE TABLE IF NOT EXISTS system_settings (
    key        text        PRIMARY KEY,
    value_json jsonb       NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by text        NOT NULL DEFAULT 'system'
);

INSERT INTO schema_migrations (version)
VALUES ('003_pii_vault_and_settings')
ON CONFLICT (version) DO NOTHING;
