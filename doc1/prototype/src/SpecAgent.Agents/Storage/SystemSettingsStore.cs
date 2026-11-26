using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SpecAgent.Core.Events;

namespace SpecAgent.Agents.Storage;

public sealed record PersistedPatchSettings(
    string MinSeverity,
    int MaxPatches,
    int MaxRounds,
    int Parallelism,
    bool IncludeGherkinInFixed);

public sealed record PersistedModelSettings(
    string DefaultModel,
    string DefaultEffort);

public sealed record PersistedPiiSettings(
    bool Enabled,
    Dictionary<string, bool> Kinds);

public sealed record PersistedSystemSettings(
    PersistedPatchSettings? Patches,
    PersistedModelSettings? Model,
    PersistedPiiSettings? Pii);

public interface ISystemSettingsStore
{
    Task<PersistedSystemSettings?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(PersistedSystemSettings settings, string updatedBy, CancellationToken ct = default);
}

/// <summary>Durable runtime settings store backed by PostgreSQL (system_settings table).</summary>
public sealed class PostgresSystemSettingsStore(NpgsqlDataSource db) : ISystemSettingsStore
{
    private const string SettingsKey = "runtime";

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

            CREATE TABLE IF NOT EXISTS system_settings (
                key        text        PRIMARY KEY,
                value_json jsonb       NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by text        NOT NULL DEFAULT 'system'
            );

            INSERT INTO schema_migrations (version)
            VALUES ('003_pii_vault_and_settings')
            ON CONFLICT (version) DO NOTHING;
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<PersistedSystemSettings?> LoadAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT value_json::text FROM system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", SettingsKey);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<PersistedSystemSettings>(json, EventJson.Options);
    }

    public async Task SaveAsync(PersistedSystemSettings settings, string updatedBy, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings, EventJson.Options);
        await using var cmd = db.CreateCommand("""
            INSERT INTO system_settings (key, value_json, updated_at, updated_by)
            VALUES (@k, @v, @at, @by)
            ON CONFLICT (key) DO UPDATE
                SET value_json = EXCLUDED.value_json,
                    updated_at = EXCLUDED.updated_at,
                    updated_by = EXCLUDED.updated_by
            """);
        cmd.Parameters.AddWithValue("k", SettingsKey);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("at", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("by", updatedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class InMemorySystemSettingsStore : ISystemSettingsStore
{
    private PersistedSystemSettings? _current;

    public Task<PersistedSystemSettings?> LoadAsync(CancellationToken ct = default) =>
        Task.FromResult(Volatile.Read(ref _current));

    public Task SaveAsync(PersistedSystemSettings settings, string updatedBy, CancellationToken ct = default)
    {
        Volatile.Write(ref _current, settings);
        return Task.CompletedTask;
    }
}
