using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SpecAgent.Agents.Storage;

namespace SpecAgent.Tests;

public class AuthAndAdminAuditTests
{
    private const string TestSigningKey = "specagent-super-secret-signing-key-2026-min-32-bytes!";
    private const string TestIssuer = "specagent-test-idp";
    private const string TestAudience = "specagent-api";

    [Fact]
    public void Migration_002_admin_audit_log_exists_and_matches_schema()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "migrations", "002_admin_audit_log.sql");
        Assert.True(File.Exists(path), $"Expected migration file at {path}");
        var sql = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE IF NOT EXISTS admin_audit_log", sql);
        Assert.Contains("actor_sub", sql);
        Assert.Contains("actor_role", sql);
        Assert.Contains("diff_json", sql);
        Assert.Contains(PostgresAdminAuditLog.MigrationVersion, sql);
    }

    [Fact]
    public async Task Jwt_mode_blocks_unauthenticated_and_spoofed_headers_and_enforces_rbac()
    {
        var tempKb = CreateTempKbDir();
        try
        {
            await using var factory = CreateFactory("Jwt", tempKb);
            using var client = factory.CreateClient();

            // 1. Health endpoint remains anonymous.
            var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            // 2. Protected endpoints reject missing JWT (401), even with forged X-SpecAgent-Role header.
            using var spoofedReq = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            spoofedReq.Headers.Add("X-SpecAgent-Role", "admin");
            var spoofedRes = await client.SendAsync(spoofedReq);
            Assert.Equal(HttpStatusCode.Unauthorized, spoofedRes.StatusCode);

            var unauthAudits = await client.GetAsync("/api/audits/recent");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthAudits.StatusCode);

            // 3. Analyst JWT: can access /api/me and /api/audits/*, forbidden (403) on admin endpoints.
            var analystJwt = MintJwt("usr-analyst-1", "Aigerim Analyst", role: "analyst");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", analystJwt);

            var meAnalyst = await client.GetFromJsonAsync<JsonElement>("/api/me");
            Assert.Equal("jwt", meAnalyst.GetProperty("authMode").GetString());
            Assert.Equal("usr-analyst-1", meAnalyst.GetProperty("sub").GetString());
            Assert.Equal("analyst", meAnalyst.GetProperty("role").GetString());
            Assert.False(meAnalyst.GetProperty("isAdmin").GetBoolean());

            var auditsOk = await client.GetAsync("/api/audits/recent");
            Assert.Equal(HttpStatusCode.OK, auditsOk.StatusCode);

            var putSettingsForbidden = await client.PutAsJsonAsync("/api/settings", new
            {
                patches = new { minSeverity = "Major", maxPatches = 5, maxRounds = 2, parallelism = 2, includeGherkinInFixed = true },
            });
            Assert.Equal(HttpStatusCode.Forbidden, putSettingsForbidden.StatusCode);

            var delKbForbidden = await client.DeleteAsync("/api/knowledge/custom.md");
            Assert.Equal(HttpStatusCode.Forbidden, delKbForbidden.StatusCode);

            var auditLogForbidden = await client.GetAsync("/api/admin/audit-log");
            Assert.Equal(HttpStatusCode.Forbidden, auditLogForbidden.StatusCode);

            // 4. Admin JWT: allowed on settings, KB upload/delete, and all actions recorded in admin_audit_log.
            var adminJwt = MintJwt("usr-admin-42", "Sanzhar Admin", role: "admin");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

            var meAdmin = await client.GetFromJsonAsync<JsonElement>("/api/me");
            Assert.Equal("admin", meAdmin.GetProperty("role").GetString());
            Assert.True(meAdmin.GetProperty("isAdmin").GetBoolean());

            var putOk = await client.PutAsJsonAsync("/api/settings", new
            {
                patches = new { minSeverity = "Critical", maxPatches = 6, maxRounds = 2, parallelism = 3, includeGherkinInFixed = true },
                pii = new { enabled = true, kinds = new Dictionary<string, bool> { ["IIN"] = true, ["PAN"] = true } },
            });
            Assert.Equal(HttpStatusCode.OK, putOk.StatusCode);

            using var uploadContent = new MultipartFormDataContent();
            uploadContent.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("# Bank Rule\nMust enforce 3DS2.")), "file", "bank-3ds-rule.md");
            var uploadRes = await client.PostAsync("/api/knowledge", uploadContent);
            Assert.Equal(HttpStatusCode.OK, uploadRes.StatusCode);

            var deleteRes = await client.DeleteAsync("/api/knowledge/bank-3ds-rule.md");
            Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);

            var auditEntries = await client.GetFromJsonAsync<List<JsonElement>>("/api/admin/audit-log");
            Assert.NotNull(auditEntries);
            Assert.True(auditEntries.Count >= 3);
            Assert.Equal("knowledge.delete", auditEntries[0].GetProperty("action").GetString());
            Assert.Equal("bank-3ds-rule.md", auditEntries[0].GetProperty("target").GetString());
            Assert.Equal("usr-admin-42", auditEntries[0].GetProperty("actorSub").GetString());
            Assert.Equal("knowledge.upload", auditEntries[1].GetProperty("action").GetString());
            Assert.Equal("settings.update", auditEntries[2].GetProperty("action").GetString());
            Assert.True(auditEntries[2].GetProperty("diffJson").TryGetProperty("before", out _));
            Assert.True(auditEntries[2].GetProperty("diffJson").TryGetProperty("after", out _));

            // 5. Keycloak-style realm_access.roles JWT is also normalized cleanly to AdminOnly.
            var keycloakJwt = MintKeycloakJwt("kc-admin-7", "Keycloak Admin", "admin");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keycloakJwt);
            var kcMe = await client.GetFromJsonAsync<JsonElement>("/api/me");
            Assert.True(kcMe.GetProperty("isAdmin").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/audit-log")).StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempKb)) Directory.Delete(tempKb, recursive: true);
        }
    }

    [Fact]
    public async Task Demo_mode_preserves_workbench_compatibility_via_same_policies()
    {
        var tempKb = CreateTempKbDir();
        try
        {
            await using var factory = CreateFactory("Demo", tempKb);
            using var client = factory.CreateClient();

            var meDefault = await client.GetFromJsonAsync<JsonElement>("/api/me");
            Assert.Equal("demo", meDefault.GetProperty("authMode").GetString());
            Assert.True(meDefault.GetProperty("isAdmin").GetBoolean());

            using var analystReq = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
            {
                Content = JsonContent.Create(new { patches = new { minSeverity = "Critical", maxPatches = 4, maxRounds = 1, parallelism = 2 } }),
            };
            analystReq.Headers.Add("X-SpecAgent-Role", "analyst");
            var res = await client.SendAsync(analystReq);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempKb)) Directory.Delete(tempKb, recursive: true);
        }
    }

    [Fact]
    public async Task Postgres_admin_audit_log_persists_and_reads_entries_when_db_available()
    {
        var cs = Environment.GetEnvironmentVariable("SPECAGENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;

        await using var dataSource = NpgsqlDataSource.Create(cs);
        var log = new PostgresAdminAuditLog(dataSource);
        await log.EnsureSchemaAsync();
        await log.EnsureSchemaAsync(); // idempotent

        var target = $"test-rule-{Guid.NewGuid():N}"[..20];
        var created = await log.RecordAsync("sub-pg-admin", "admin", "knowledge.upload", target,
            new { sizeBytes = 42, source = "integration-test" });

        Assert.True(created.Id > 0);
        var recent = await log.ListRecentAsync(10);
        Assert.Contains(recent, e => e.Id == created.Id && e.ActorSub == "sub-pg-admin" && e.Target == target);
    }

    private static WebApplicationFactory<Program> CreateFactory(string authMode, string kbDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("EventStore", "InMemory");
            builder.UseSetting("Knowledge:Path", kbDir);
            builder.UseSetting("Auth:Mode", authMode);
            builder.UseSetting("Auth:Issuer", TestIssuer);
            builder.UseSetting("Auth:Audience", TestAudience);
            builder.UseSetting("Auth:SigningKey", authMode == "Jwt" ? TestSigningKey : "");
        });

    private static string MintJwt(string sub, string name, string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims:
            [
                new Claim("sub", sub),
                new Claim("name", name),
                new Claim("role", role),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string MintKeycloakJwt(string sub, string name, string realmRole)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims:
            [
                new Claim("sub", sub),
                new Claim("preferred_username", name),
                new Claim("realm_access", JsonSerializer.Serialize(new { roles = new[] { realmRole } })),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateTempKbDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"specagent-kb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
