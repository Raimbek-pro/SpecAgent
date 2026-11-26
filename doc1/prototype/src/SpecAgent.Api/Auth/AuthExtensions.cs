using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SpecAgent.Api.Auth;

public static class AuthPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string AnalystOrAdmin = "AnalystOrAdmin";
}

public static class AuthRoles
{
    public const string Admin = "admin";
    public const string Analyst = "analyst";
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Auto (default): enables strict JWT Bearer whenever Authority or SigningKey is set; otherwise DemoHeader for local UI.
    /// Jwt: forces strict JWT Bearer (no header fallback).
    /// Demo: enables DemoHeader scheme for local hackathon workbench without an IdP.
    /// </summary>
    public string Mode { get; set; } = "Auto";

    /// <summary>OIDC Authority URL (e.g. Keycloak realm URL or Entra ID / Azure AD tenant URL).</summary>
    public string? Authority { get; set; }

    /// <summary>Expected JWT audience (aud claim).</summary>
    public string Audience { get; set; } = "specagent-api";

    /// <summary>Expected JWT issuer (iss claim) when using a symmetric SigningKey.</summary>
    public string Issuer { get; set; } = "specagent-auth";

    /// <summary>Optional HS256 symmetric signing key (min 32 bytes). Also read from JWT_SIGNING_KEY env var.</summary>
    public string? SigningKey { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;
    public string RoleClaimType { get; set; } = "role";

    /// <summary>Resolved effective mode: "jwt" or "demo".</summary>
    public string EffectiveMode { get; set; } = "demo";
}

public sealed record CurrentUser(string Sub, string Name, string Role, bool IsAdmin, string AuthMode)
{
    public static CurrentUser From(ClaimsPrincipal user, string authMode)
    {
        var sub = user.FindFirstValue("sub")
                  ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? "anonymous";
        var name = user.FindFirstValue("name")
                   ?? user.FindFirstValue("preferred_username")
                   ?? user.Identity?.Name
                   ?? sub;
        var isAdmin = user.IsInRole(AuthRoles.Admin);
        var role = isAdmin ? AuthRoles.Admin
            : user.IsInRole(AuthRoles.Analyst) ? AuthRoles.Analyst
            : user.FindFirstValue(ClaimTypes.Role) ?? "unknown";
        return new CurrentUser(sub, name, role, isAdmin, authMode);
    }
}

public static class AuthExtensions
{
    public const string DemoScheme = "DemoHeader";

    public static IServiceCollection AddSpecAgentAuth(this IServiceCollection services, IConfiguration config)
    {
        var opts = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        if (string.IsNullOrWhiteSpace(opts.SigningKey))
            opts.SigningKey = config["JWT_SIGNING_KEY"] ?? Environment.GetEnvironmentVariable("JWT_SIGNING_KEY");

        var hasJwtConfig = !string.IsNullOrWhiteSpace(opts.Authority) || !string.IsNullOrWhiteSpace(opts.SigningKey);
        var useJwt = opts.Mode.Equals("Jwt", StringComparison.OrdinalIgnoreCase) ||
                     (opts.Mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && hasJwtConfig);

        opts.EffectiveMode = useJwt ? "jwt" : "demo";
        services.AddSingleton(opts);

        if (useJwt)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(jwt =>
                {
                    jwt.MapInboundClaims = false;
                    jwt.RequireHttpsMetadata = opts.RequireHttpsMetadata;

                    if (!string.IsNullOrWhiteSpace(opts.Authority))
                    {
                        jwt.Authority = opts.Authority;
                        jwt.Audience = opts.Audience;
                        jwt.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidateAudience = !string.IsNullOrWhiteSpace(opts.Audience),
                            ValidAudience = opts.Audience,
                            ValidateLifetime = true,
                            NameClaimType = "name",
                            RoleClaimType = ClaimTypes.Role,
                        };
                    }
                    else if (!string.IsNullOrWhiteSpace(opts.SigningKey))
                    {
                        var keyBytes = Encoding.UTF8.GetBytes(opts.SigningKey);
                        jwt.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = !string.IsNullOrWhiteSpace(opts.Issuer),
                            ValidIssuer = opts.Issuer,
                            ValidateAudience = !string.IsNullOrWhiteSpace(opts.Audience),
                            ValidAudience = opts.Audience,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                            ValidateLifetime = true,
                            ClockSkew = TimeSpan.FromSeconds(30),
                            NameClaimType = "name",
                            RoleClaimType = ClaimTypes.Role,
                        };
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Auth:Mode is 'Jwt', but neither Auth:Authority nor Auth:SigningKey (or JWT_SIGNING_KEY) is configured.");
                    }

                    jwt.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = ctx =>
                        {
                            NormalizeRoleClaims(ctx.Principal, opts.RoleClaimType);
                            return Task.CompletedTask;
                        },
                    };
                });
        }
        else
        {
            services.AddAuthentication(DemoScheme)
                .AddScheme<AuthenticationSchemeOptions, DemoHeaderAuthHandler>(DemoScheme, _ => { });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.AdminOnly, p => p
                .RequireAuthenticatedUser()
                .RequireRole(AuthRoles.Admin))
            .AddPolicy(AuthPolicies.AnalystOrAdmin, p => p
                .RequireAuthenticatedUser()
                .RequireRole(AuthRoles.Admin, AuthRoles.Analyst));

        return services;
    }

    /// <summary>
    /// Normalizes roles from standard JWT claims (`role`, `roles`, ClaimTypes.Role) and Keycloak's
    /// `realm_access: { "roles": [...] }` JSON object into lowercase ClaimTypes.Role claims.
    /// </summary>
    private static void NormalizeRoleClaims(ClaimsPrincipal? principal, string configuredRoleClaim)
    {
        if (principal?.Identity is not ClaimsIdentity identity) return;

        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in identity.Claims)
        {
            if (claim.Type == ClaimTypes.Role ||
                claim.Type.Equals(configuredRoleClaim, StringComparison.OrdinalIgnoreCase) ||
                claim.Type.Equals("role", StringComparison.OrdinalIgnoreCase) ||
                claim.Type.Equals("roles", StringComparison.OrdinalIgnoreCase))
            {
                AddRolesFromValue(claim.Value, discovered);
            }
            else if (claim.Type.Equals("realm_access", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(claim.Value);
                    if (doc.RootElement.TryGetProperty("roles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rolesEl.EnumerateArray())
                            if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } s)
                                discovered.Add(s.Trim().ToLowerInvariant());
                    }
                }
                catch (JsonException) { /* ignore malformed realm_access */ }
            }
        }

        foreach (var role in discovered)
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
    }

    private static void AddRolesFromValue(string value, HashSet<string> target)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                            target.Add(s.Trim().ToLowerInvariant());
                    return;
                }
            }
            catch (JsonException) { /* fall through */ }
        }

        foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            target.Add(part.ToLowerInvariant());
    }
}

/// <summary>
/// Local demo authentication handler (active ONLY when Auth:Mode=Demo or Auto with no IdP/SigningKey).
/// Maps X-SpecAgent-Role into a real ClaimsPrincipal so the exact same ASP.NET Core RBAC policies
/// (AdminOnly / AnalystOrAdmin) enforce access control everywhere.
/// </summary>
public sealed class DemoHeaderAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headerRole = Request.Headers["X-SpecAgent-Role"].ToString().Trim();
        var role = headerRole.Equals(AuthRoles.Analyst, StringComparison.OrdinalIgnoreCase)
            ? AuthRoles.Analyst
            : AuthRoles.Admin;

        var sub = role == AuthRoles.Analyst ? "demo-analyst" : "demo-admin";
        var name = role == AuthRoles.Analyst ? "Demo Analyst" : "Demo Admin";

        var claims = new[]
        {
            new Claim("sub", sub),
            new Claim("name", name),
            new Claim(ClaimTypes.NameIdentifier, sub),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
