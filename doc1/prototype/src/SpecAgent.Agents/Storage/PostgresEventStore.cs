using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SpecAgent.Core.Events;

namespace SpecAgent.Agents.Storage;

/// <summary>
/// Durable append-only session & event log backed by PostgreSQL.
/// Parallel critics append to the same session concurrently, so sequence numbers and session
/// status transitions are serialized per session using a transactional advisory lock.
/// </summary>
public sealed class PostgresEventStore(NpgsqlDataSource db) : IEventStore
{
    public const string MigrationVersion = "001_init_audit_store";

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
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

            CREATE INDEX IF NOT EXISTS idx_audit_sessions_open
                ON audit_sessions (created_at)
                WHERE status IN ('open', 'running');

            CREATE INDEX IF NOT EXISTS idx_audit_events_session_type
                ON audit_events (session_id, type);

            CREATE INDEX IF NOT EXISTS idx_audit_events_at
                ON audit_events (at DESC);

            INSERT INTO schema_migrations (version)
            VALUES ('001_init_audit_store')
            ON CONFLICT (version) DO NOTHING;
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AuditEvent> AppendAsync(string sessionId, string type, string agent, string message,
        object? payload = null, CancellationToken ct = default)
    {
        var element = EventJson.ToElement(payload);
        var at = DateTimeOffset.UtcNow;
        var nextStatus = StatusForEvent(type);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@s))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("s", sessionId);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        // Upsert session row and atomically allocate the next monotonic seq for this session.
        long seq;
        await using (var sessionCmd = new NpgsqlCommand("""
            INSERT INTO audit_sessions (session_id, status, last_seq, created_at, updated_at)
            VALUES (@s, COALESCE(@status, 'open'), 1, @at, @at)
            ON CONFLICT (session_id) DO UPDATE
                SET last_seq   = audit_sessions.last_seq + 1,
                    status     = COALESCE(@status, audit_sessions.status),
                    updated_at = @at
            RETURNING last_seq
            """, conn, tx))
        {
            sessionCmd.Parameters.AddWithValue("s", sessionId);
            sessionCmd.Parameters.AddWithValue("status", (object?)nextStatus ?? DBNull.Value);
            sessionCmd.Parameters.AddWithValue("at", at);
            seq = (long)(await sessionCmd.ExecuteScalarAsync(ct))!;
        }

        await using (var eventCmd = new NpgsqlCommand("""
            INSERT INTO audit_events (session_id, seq, type, agent, at, message, payload)
            VALUES (@s, @seq, @t, @a, @at, @m, @p)
            """, conn, tx))
        {
            eventCmd.Parameters.AddWithValue("s", sessionId);
            eventCmd.Parameters.AddWithValue("seq", seq);
            eventCmd.Parameters.AddWithValue("t", type);
            eventCmd.Parameters.AddWithValue("a", agent);
            eventCmd.Parameters.AddWithValue("at", at);
            eventCmd.Parameters.AddWithValue("m", message);
            eventCmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb)
                { Value = element is { } el ? el.GetRawText() : DBNull.Value });
            await eventCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new AuditEvent(seq, sessionId, type, agent, at, message, element);
    }

    public async Task<IReadOnlyList<AuditEvent>> ReadAsync(string sessionId, long afterSeq = 0, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT seq, type, agent, at, message, payload::text FROM audit_events WHERE session_id = @s AND seq > @after ORDER BY seq");
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("after", afterSeq);
        var list = new List<AuditEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            JsonElement? payload = r.IsDBNull(5) ? null : JsonDocument.Parse(r.GetString(5)).RootElement.Clone();
            list.Add(new AuditEvent(r.GetInt64(0), sessionId, r.GetString(1), r.GetString(2),
                r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), payload));
        }
        return list;
    }

    public async Task<IReadOnlyList<string>> ListOpenSessionsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT session_id
            FROM audit_sessions
            WHERE status IN ('open', 'running')
            ORDER BY created_at
            """);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    private static string? StatusForEvent(string type) => type switch
    {
        EventTypes.TzIngested => "open",
        EventTypes.RunStarted or EventTypes.Resumed => "running",
        EventTypes.ReportReady => "completed",
        EventTypes.RunFailed => "failed",
        _ => null,
    };
}
