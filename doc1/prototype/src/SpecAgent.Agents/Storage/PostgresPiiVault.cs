using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SpecAgent.Ingestion.Pii;

namespace SpecAgent.Agents.Storage;

/// <summary>
/// Persistent, AES-256-GCM encrypted PII Vault backed by PostgreSQL (migration 003_pii_vault_and_settings).
/// - Plaintext PII is never stored in the database.
/// - Per-session/kind deduplication uses keyed HMAC-SHA256 so equal values map to the same token
///   without decrypting existing rows or leaking cross-session equality.
/// - Ciphertext is bound to (session_id | token) via AES-GCM Associated Authenticated Data (AAD).
/// - Entries expire automatically after TtlDays (default 30 days) for Personal Data Law compliance.
/// </summary>
public sealed partial class PostgresPiiVault : IPiiVault
{
    public const string MigrationVersion = "003_pii_vault_and_settings";

    private readonly NpgsqlDataSource _db;
    private readonly byte[] _encKey;
    private readonly byte[] _hmacKey;
    private readonly TimeSpan _ttl;

    [GeneratedRegex(@"\[(?:SECRET|IBAN|PAN|IIN|PHONE|EMAIL|IP)_\d+\]")]
    private static partial Regex TokenRegex();

    public PostgresPiiVault(NpgsqlDataSource db, string? rawKey, int ttlDays = 30, ILogger<PostgresPiiVault>? logger = null)
    {
        _db = db;
        _ttl = TimeSpan.FromDays(Math.Clamp(ttlDays, 1, 3650));

        byte[] master;
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            logger?.LogWarning("PII_VAULT_KEY is not set; deriving a local development encryption key. Set PII_VAULT_KEY in production!");
            master = SHA256.HashData(Encoding.UTF8.GetBytes("specagent-local-dev-pii-vault-key-change-in-production"));
        }
        else
        {
            master = ParseMasterKey(rawKey.Trim());
        }

        // Domain-separated 256-bit keys for AES-256-GCM encryption and HMAC-SHA256 indexing.
        _encKey = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("specagent:pii:aes-256-gcm:v1"));
        _hmacKey = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("specagent:pii:hmac-index:v1"));
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('specagent:schema_migrations'))", conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version    text        PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now()
            );

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

            INSERT INTO schema_migrations (version)
            VALUES ('003_pii_vault_and_settings')
            ON CONFLICT (version) DO NOTHING;
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public string Tokenize(string sessionId, string kind, string value)
    {
        var hmac = ComputeHmac(sessionId, kind, value);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(_ttl);

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@lockKey))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("lockKey", $"pii:{sessionId}");
            lockCmd.ExecuteNonQuery();
        }

        // 1. Check if this exact value was already tokenized for this session & kind.
        using (var findCmd = new NpgsqlCommand("""
            SELECT token FROM pii_vault_entries
            WHERE session_id = @s AND kind = @k AND value_hmac = @h AND expires_at > @now
            LIMIT 1
            """, conn, tx))
        {
            findCmd.Parameters.AddWithValue("s", sessionId);
            findCmd.Parameters.AddWithValue("k", kind);
            findCmd.Parameters.AddWithValue("h", hmac);
            findCmd.Parameters.AddWithValue("now", now);
            if (findCmd.ExecuteScalar() is string existing)
            {
                tx.Commit();
                return existing;
            }
        }

        // 2. Allocate next monotonic token number for (session_id, kind).
        long count;
        using (var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pii_vault_entries WHERE session_id = @s AND kind = @k", conn, tx))
        {
            countCmd.Parameters.AddWithValue("s", sessionId);
            countCmd.Parameters.AddWithValue("k", kind);
            count = (long)countCmd.ExecuteScalar()!;
        }

        var token = $"[{kind}_{count + 1}]";
        var (nonce, ciphertext, tag) = Encrypt(sessionId, token, value);

        using (var insertCmd = new NpgsqlCommand("""
            INSERT INTO pii_vault_entries (session_id, token, kind, value_hmac, nonce, ciphertext, tag, expires_at, created_at)
            VALUES (@s, @tok, @k, @h, @nonce, @ct, @tag, @exp, @now)
            """, conn, tx))
        {
            insertCmd.Parameters.AddWithValue("s", sessionId);
            insertCmd.Parameters.AddWithValue("tok", token);
            insertCmd.Parameters.AddWithValue("k", kind);
            insertCmd.Parameters.AddWithValue("h", hmac);
            insertCmd.Parameters.AddWithValue("nonce", nonce);
            insertCmd.Parameters.AddWithValue("ct", ciphertext);
            insertCmd.Parameters.AddWithValue("tag", tag);
            insertCmd.Parameters.AddWithValue("exp", expiresAt);
            insertCmd.Parameters.AddWithValue("now", now);
            insertCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return token;
    }

    public string Detokenize(string sessionId, string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var matches = TokenRegex().Matches(text);
        if (matches.Count == 0) return text;

        var neededTokens = matches.Select(m => m.Value).Distinct(StringComparer.Ordinal).ToArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        using var conn = _db.OpenConnection();
        using var cmd = new NpgsqlCommand("""
            SELECT token, nonce, ciphertext, tag
            FROM pii_vault_entries
            WHERE session_id = @s AND token = ANY(@tokens) AND expires_at > @now
            """, conn);
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("tokens", neededTokens);
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var token = reader.GetString(0);
            var nonce = (byte[])reader[1];
            var ciphertext = (byte[])reader[2];
            var tag = (byte[])reader[3];
            map[token] = Decrypt(sessionId, token, nonce, ciphertext, tag);
        }

        if (map.Count == 0) return text;
        return TokenRegex().Replace(text, m => map.GetValueOrDefault(m.Value, m.Value));
    }

    public int Count(string sessionId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pii_vault_entries WHERE session_id = @s AND expires_at > @now", conn);
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        return Convert.ToInt32((long)cmd.ExecuteScalar()!);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM pii_vault_entries WHERE expires_at <= @now");
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private byte[] ComputeHmac(string sessionId, string kind, string value) =>
        HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes($"{sessionId}\0{kind}\0{value}"));

    private (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(string sessionId, string token, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];
        var aad = Encoding.UTF8.GetBytes($"{sessionId}|{token}");

        using var aes = new AesGcm(_encKey, 16);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag, aad);
        return (nonce, ciphertext, tag);
    }

    private string Decrypt(string sessionId, string token, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        var plainBytes = new byte[ciphertext.Length];
        var aad = Encoding.UTF8.GetBytes($"{sessionId}|{token}");

        using var aes = new AesGcm(_encKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plainBytes, aad);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] ParseMasterKey(string rawKey)
    {
        // Support 32-byte Base64, 64-char hex, or arbitrary passphrase (hashed to 32 bytes).
        if (rawKey.Length == 64 && rawKey.All(Uri.IsHexDigit))
            return Convert.FromHexString(rawKey);
        try
        {
            var decoded = Convert.FromBase64String(rawKey);
            if (decoded.Length == 32) return decoded;
        }
        catch (FormatException) { /* fall back to SHA-256 passphrase derivation */ }

        return SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
    }
}

/// <summary>Periodically purges expired PII vault entries according to configured retention TTL.</summary>
public sealed class PiiVaultCleanupWorker(PostgresPiiVault vault, ILogger<PiiVaultCleanupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var purged = await vault.PurgeExpiredAsync(stoppingToken);
                if (purged > 0)
                    logger.LogInformation("Purged {Count} expired PII vault entries", purged);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PII vault TTL purge failed");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
