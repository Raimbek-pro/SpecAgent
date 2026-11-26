using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SpecAgent.Core.Events;

namespace SpecAgent.Agents.Storage;

public sealed record AdminAuditEntry(
    long Id,
    string ActorSub,
    string ActorRole,
    string Action,
    string Target,
    JsonElement DiffJson,
    DateTimeOffset CreatedAt);

public interface IAdminAuditLog
{
    Task<AdminAuditEntry> RecordAsync(
        string actorSub,
        string actorRole,
        string action,
        string target,
        object diff,
        CancellationToken ct = default);

    Task<IReadOnlyList<AdminAuditEntry>> ListRecentAsync(int limit = 50, CancellationToken ct = default);
}

/// <summary>Durable admin audit trail backed by PostgreSQL (migration 002_admin_audit_log).</summary>
public sealed class PostgresAdminAuditLog(NpgsqlDataSource db) : IAdminAuditLog
{
    public const string MigrationVersion = "002_admin_audit_log";

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('specagent:schema_migrations'))", conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version    text        PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now()
            );

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
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<AdminAuditEntry> RecordAsync(
        string actorSub,
        string actorRole,
        string action,
        string target,
        object diff,
        CancellationToken ct = default)
    {
        var diffElement = EventJson.ToElement(diff) ?? JsonDocument.Parse("{}").RootElement.Clone();
        var createdAt = DateTimeOffset.UtcNow;

        await using var cmd = db.CreateCommand("""
            INSERT INTO admin_audit_log (actor_sub, actor_role, action, target, diff_json, created_at)
            VALUES (@sub, @role, @action, @target, @diff, @at)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue("sub", actorSub);
        cmd.Parameters.AddWithValue("role", actorRole);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("target", target);
        cmd.Parameters.Add(new NpgsqlParameter("diff", NpgsqlDbType.Jsonb) { Value = diffElement.GetRawText() });
        cmd.Parameters.AddWithValue("at", createdAt);

        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return new AdminAuditEntry(id, actorSub, actorRole, action, target, diffElement, createdAt);
    }

    public async Task<IReadOnlyList<AdminAuditEntry>> ListRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var capped = Math.Clamp(limit, 1, 200);
        await using var cmd = db.CreateCommand("""
            SELECT id, actor_sub, actor_role, action, target, diff_json::text, created_at
            FROM admin_audit_log
            ORDER BY id DESC
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("limit", capped);

        var list = new List<AdminAuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var diff = JsonDocument.Parse(r.GetString(5)).RootElement.Clone();
            list.Add(new AdminAuditEntry(
                Id: r.GetInt64(0),
                ActorSub: r.GetString(1),
                ActorRole: r.GetString(2),
                Action: r.GetString(3),
                Target: r.GetString(4),
                DiffJson: diff,
                CreatedAt: r.GetFieldValue<DateTimeOffset>(6)));
        }
        return list;
    }
}

/// <summary>In-memory admin audit trail used when EventStore=InMemory (dev/tests).</summary>
public sealed class InMemoryAdminAuditLog : IAdminAuditLog
{
    private readonly List<AdminAuditEntry> _entries = [];
    private long _nextId;

    public Task<AdminAuditEntry> RecordAsync(
        string actorSub,
        string actorRole,
        string action,
        string target,
        object diff,
        CancellationToken ct = default)
    {
        var diffElement = EventJson.ToElement(diff) ?? JsonDocument.Parse("{}").RootElement.Clone();
        lock (_entries)
        {
            var entry = new AdminAuditEntry(++_nextId, actorSub, actorRole, action, target, diffElement, DateTimeOffset.UtcNow);
            _entries.Add(entry);
            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<AdminAuditEntry>> ListRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var capped = Math.Clamp(limit, 1, 200);
        lock (_entries)
        {
            IReadOnlyList<AdminAuditEntry> result = _entries.OrderByDescending(e => e.Id).Take(capped).ToList();
            return Task.FromResult(result);
        }
    }
}
