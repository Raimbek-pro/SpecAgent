using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SpecAgent.Core.Events;

namespace SpecAgent.Agents.Storage;

/// <summary>
/// Durable append-only log. Parallel critics append to the same session concurrently, so seq is
/// assigned under a per-session advisory lock.
/// </summary>
public sealed class PostgresEventStore(NpgsqlDataSource db) : IEventStore
{
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS audit_events (
                session_id text        NOT NULL,
                seq        bigint      NOT NULL,
                type       text        NOT NULL,
                agent      text        NOT NULL,
                at         timestamptz NOT NULL,
                message    text        NOT NULL,
                payload    jsonb,
                PRIMARY KEY (session_id, seq)
            );
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AuditEvent> AppendAsync(string sessionId, string type, string agent, string message,
        object? payload = null, CancellationToken ct = default)
    {
        var element = EventJson.ToElement(payload);
        var at = DateTimeOffset.UtcNow;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@s))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("s", sessionId);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO audit_events (session_id, seq, type, agent, at, message, payload)
            SELECT @s, COALESCE(MAX(seq), 0) + 1, @t, @a, @at, @m, @p FROM audit_events WHERE session_id = @s
            RETURNING seq
            """, conn, tx);
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("t", type);
        cmd.Parameters.AddWithValue("a", agent);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("m", message);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb)
            { Value = element is { } el ? el.GetRawText() : DBNull.Value });
        var seq = (long)(await cmd.ExecuteScalarAsync(ct))!;
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
            SELECT session_id FROM audit_events GROUP BY session_id
            HAVING bool_or(type = 'RUN_STARTED' OR type = 'TZ_INGESTED')
               AND NOT bool_or(type IN ('REPORT_READY', 'RUN_FAILED'))
            """);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }
}
