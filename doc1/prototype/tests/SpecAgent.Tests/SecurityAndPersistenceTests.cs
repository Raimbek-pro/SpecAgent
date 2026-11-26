using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using SpecAgent.Agents.Storage;
using SpecAgent.Api.Security;
using SpecAgent.Ingestion.Pii;

namespace SpecAgent.Tests;

public class SecurityAndPersistenceTests
{
    [Fact]
    public void Migration_003_pii_vault_and_settings_exists_and_matches_schema()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "migrations", "003_pii_vault_and_settings.sql");
        Assert.True(File.Exists(path), $"Expected migration file at {path}");
        var sql = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE IF NOT EXISTS pii_vault_entries", sql);
        Assert.Contains("ciphertext", sql);
        Assert.Contains("expires_at", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS system_settings", sql);
        Assert.Contains(PostgresPiiVault.MigrationVersion, sql);
    }

    [Fact]
    public void UploadValidator_enforces_size_extension_and_magic_bytes()
    {
        const long maxBytes = 1024;

        // Valid PDF magic header
        var okPdf = UploadValidator.Validate("spec.pdf", "%PDF-1.7\nbody"u8.ToArray(), maxBytes);
        Assert.True(okPdf.IsValid);

        // Spoofed PDF (plain text renamed to .pdf)
        var fakePdf = UploadValidator.Validate("evil.pdf", "<html><script>alert(1)</script></html>"u8.ToArray(), maxBytes);
        Assert.False(fakePdf.IsValid);
        Assert.Equal(400, fakePdf.StatusCode);

        // Spoofed DOCX (missing PK\x03\x04)
        var fakeDocx = UploadValidator.Validate("fake.docx", "not a zip file"u8.ToArray(), maxBytes);
        Assert.False(fakeDocx.IsValid);
        Assert.Equal(400, fakeDocx.StatusCode);

        // Valid DOCX magic header
        var okDocx = UploadValidator.Validate("real.docx", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, maxBytes);
        Assert.True(okDocx.IsValid);

        // Binary data disguised as .md
        var binMd = UploadValidator.Validate("bad.md", new byte[] { 0x4D, 0x5A, 0x00, 0x01 }, maxBytes);
        Assert.False(binMd.IsValid);
        Assert.Equal(400, binMd.StatusCode);

        // Disallowed extension
        var exe = UploadValidator.Validate("payload.exe", "MZ"u8.ToArray(), maxBytes);
        Assert.False(exe.IsValid);
        Assert.Equal(400, exe.StatusCode);

        // Exceeds size limit → 413
        var big = UploadValidator.Validate("big.md", new byte[maxBytes + 1], maxBytes);
        Assert.False(big.IsValid);
        Assert.Equal(413, big.StatusCode);
    }

    [Fact]
    public async Task Api_enforces_security_headers_cors_magic_bytes_size_and_rate_limit()
    {
        var tempKb = Path.Combine(Path.GetTempPath(), $"specagent-sec-kb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempKb);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("EventStore", "InMemory");
                builder.UseSetting("Knowledge:Path", tempKb);
                builder.UseSetting("Auth:Mode", "Demo");
                builder.UseSetting("Security:MaxUploadBytes", "2048");
                builder.UseSetting("Security:RateLimit:AuditPerMinute", "2");
            });

            using var client = factory.CreateClient();

            // 1. Security headers + strict CORS (evil origin gets no ACAO header).
            using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
            req.Headers.Add("Origin", "https://evil.example");
            var health = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("SAMEORIGIN", health.Headers.GetValues("X-Frame-Options").Single());
            Assert.True(health.Headers.Contains("Content-Security-Policy"));
            Assert.False(health.Headers.Contains("Access-Control-Allow-Origin"));

            // 2. Spoofed PDF rejected on POST /api/knowledge (400).
            using (var badKb = new MultipartFormDataContent())
            {
                badKb.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("not a pdf")), "file", "spoofed.pdf");
                var kbRes = await client.PostAsync("/api/knowledge", badKb);
                Assert.Equal(HttpStatusCode.BadRequest, kbRes.StatusCode);
            }

            // 3. Oversized file rejected with 413 on POST /api/knowledge.
            using (var bigKb = new MultipartFormDataContent())
            {
                bigKb.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(new string('A', 4096))), "file", "huge.md");
                var bigRes = await client.PostAsync("/api/knowledge", bigKb);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bigRes.StatusCode);
            }

            // 4. Rate limit on POST /api/audits (AuditPerMinute=2 → 3rd call gets 429).
            for (int i = 0; i < 2; i++)
            {
                using var form = new MultipartFormDataContent();
                form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("# Spec\n1.1 Simple rule.")), "file", $"ok-{i}.md");
                var okRes = await client.PostAsync("/api/audits", form);
                Assert.Equal(HttpStatusCode.Accepted, okRes.StatusCode);
            }
            using (var thirdForm = new MultipartFormDataContent())
            {
                thirdForm.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("# Spec\n1.1 Third call.")), "file", "ok-3.md");
                var limited = await client.PostAsync("/api/audits", thirdForm);
                Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            }
        }
        finally
        {
            if (Directory.Exists(tempKb)) Directory.Delete(tempKb, recursive: true);
        }
    }

    [Fact]
    public async Task Postgres_pii_vault_encrypts_survives_restart_and_purges_expired_when_db_available()
    {
        var cs = Environment.GetEnvironmentVariable("SPECAGENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;

        const string key = "test-aes256-gcm-master-key-for-specagent-vault-2026!";
        await using var dataSource = NpgsqlDataSource.Create(cs);

        var vault1 = new PostgresPiiVault(dataSource, key, ttlDays: 30);
        await vault1.EnsureSchemaAsync();

        var sessionId = $"s-vault-{Guid.NewGuid():N}"[..18];
        const string original = "ИИН 900101300037, карта 4111 1111 1111 1111, счёт KZ86125KZT5004100100, тел. +7 701 123 45 67";

        var masked = new PiiMasker(vault1).Mask(sessionId, original);
        Assert.DoesNotContain("900101300037", masked);
        Assert.DoesNotContain("4111 1111", masked);
        Assert.Equal(4, vault1.Count(sessionId));

        // Verify plaintext is NOT stored anywhere in ciphertext column.
        await using (var cmd = dataSource.CreateCommand("SELECT ciphertext FROM pii_vault_entries WHERE session_id = @s"))
        {
            cmd.Parameters.AddWithValue("s", sessionId);
            await using var r = await cmd.ExecuteReaderAsync();
            int rows = 0;
            while (await r.ReadAsync())
            {
                rows++;
                var raw = Encoding.UTF8.GetString((byte[])r[0]);
                Assert.DoesNotContain("900101300037", raw);
                Assert.DoesNotContain("4111", raw);
            }
            Assert.Equal(4, rows);
        }

        // Simulate API container restart: brand-new PostgresPiiVault instance with the same key.
        var vault2 = new PostgresPiiVault(dataSource, key, ttlDays: 30);
        Assert.Equal(original, new PiiMasker(vault2).Demask(sessionId, masked));

        // Session isolation: another session must NOT demask this session's tokens.
        Assert.Equal(masked, new PiiMasker(vault2).Demask("other-session-id", masked));

        // TTL expiry + purge test.
        await using (var expireCmd = dataSource.CreateCommand(
            "UPDATE pii_vault_entries SET expires_at = now() - interval '1 minute' WHERE session_id = @s"))
        {
            expireCmd.Parameters.AddWithValue("s", sessionId);
            await expireCmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, vault2.Count(sessionId));
        var purged = await vault2.PurgeExpiredAsync();
        Assert.True(purged >= 4);
    }

    [Fact]
    public async Task Postgres_system_settings_store_persists_across_instances_when_db_available()
    {
        var cs = Environment.GetEnvironmentVariable("SPECAGENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;

        await using var dataSource = NpgsqlDataSource.Create(cs);
        var store1 = new PostgresSystemSettingsStore(dataSource);
        await store1.EnsureSchemaAsync();

        var previous = await store1.LoadAsync();
        try
        {
            var saved = new PersistedSystemSettings(
                new PersistedPatchSettings("Critical", 7, 2, 4, true),
                new PersistedModelSettings("kimi-k3.1-pro", "high"),
                new PersistedPiiSettings(true, new Dictionary<string, bool> { ["IIN"] = true, ["PAN"] = true }));

            await store1.SaveAsync(saved, "test-admin");

            // Fresh instance simulates container restart.
            var store2 = new PostgresSystemSettingsStore(dataSource);
            var loaded = await store2.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal("Critical", loaded.Patches!.MinSeverity);
            Assert.Equal(7, loaded.Patches.MaxPatches);
            Assert.True(loaded.Patches.IncludeGherkinInFixed);
            Assert.Equal("kimi-k3.1-pro", loaded.Model!.DefaultModel);
            Assert.True(loaded.Pii!.Enabled);
        }
        finally
        {
            if (previous is not null)
                await store1.SaveAsync(previous, "test-restore");
            else
            {
                await using var cleanup = dataSource.CreateCommand("DELETE FROM system_settings WHERE key = 'runtime'");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
