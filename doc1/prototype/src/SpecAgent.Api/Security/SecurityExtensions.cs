using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SpecAgent.Api.Security;

public static class RateLimitPolicies
{
    public const string AuditUpload = "AuditUpload";
    public const string AgentAsk = "AgentAsk";
}

public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Maximum uploaded file size in bytes (default: 25 MB).</summary>
    public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;

    public RateLimitOptions RateLimit { get; set; } = new();
    public CorsSecurityOptions Cors { get; set; } = new();
}

public sealed class RateLimitOptions
{
    public int AuditPerMinute { get; set; } = 10;
    public int AskPerMinute { get; set; } = 30;
}

public sealed class CorsSecurityOptions
{
    /// <summary>Explicitly allowed CORS origins. Empty = same-origin only (strict default).</summary>
    public string[] AllowedOrigins { get; set; } = [];
}

public static class UploadValidator
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".md", ".txt",
    };

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04]; // PK\x03\x04 (.docx OOXML container)

    public static (bool IsValid, int StatusCode, string? Error) Validate(string fileName, ReadOnlySpan<byte> bytes, long maxUploadBytes)
    {
        if (bytes.Length == 0)
            return (false, 400, "Загруженный файл пуст.");

        if (bytes.Length > maxUploadBytes)
            return (false, 413, $"Файл превышает максимальный размер ({maxUploadBytes / (1024 * 1024)} МБ).");

        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return (false, 400, $"Неподдерживаемый формат файла '{ext}'. Разрешены: .pdf, .docx, .md, .txt.");

        switch (ext)
        {
            case ".pdf":
                if (bytes.Length < PdfMagic.Length || !bytes[..PdfMagic.Length].SequenceEqual(PdfMagic))
                    return (false, 400, "Файл не является валидным PDF (отсутствует заголовок %PDF-).");
                break;

            case ".docx":
                if (bytes.Length < ZipMagic.Length || !bytes[..ZipMagic.Length].SequenceEqual(ZipMagic))
                    return (false, 400, "Файл не является валидным DOCX (отсутствует ZIP/OOXML-сигнатура PK).");
                break;

            case ".md":
            case ".txt":
                var sampleLen = Math.Min(bytes.Length, 8192);
                if (bytes[..sampleLen].IndexOf((byte)0) >= 0)
                    return (false, 400, "Текстовый файл содержит бинарные данные (NUL-байты).");
                break;
        }

        return (true, 200, null);
    }
}

public static class SecurityExtensions
{
    public const string StrictCorsPolicy = "StrictCors";

    public static IServiceCollection AddSpecAgentSecurity(this IServiceCollection services, IConfiguration config)
    {
        var opts = config.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
        services.AddSingleton(opts);

        services.AddCors(cors =>
        {
            cors.AddPolicy(StrictCorsPolicy, policy =>
            {
                var origins = opts.Cors.AllowedOrigins
                    .Where(o => !string.IsNullOrWhiteSpace(o) && o != "*")
                    .Select(o => o.Trim().TrimEnd('/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (origins.Length > 0)
                {
                    policy.WithOrigins(origins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                }
                // Empty AllowedOrigins = strict same-origin only (no Access-Control-Allow-Origin emitted).
            });
        });

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.ContentType = "application/json";
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

                await ctx.HttpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Превышен лимит запросов (rate limit). Повторите попытку позже.",
                }, ct);
            };

            limiter.AddPolicy(RateLimitPolicies.AuditUpload, http =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: PartitionKey(http),
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, opts.RateLimit.AuditPerMinute),
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    }));

            limiter.AddPolicy(RateLimitPolicies.AgentAsk, http =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: PartitionKey(http),
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, opts.RateLimit.AskPerMinute),
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    }));
        });

        return services;
    }

    public static IApplicationBuilder UseSpecAgentSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                h.TryAdd("X-Content-Type-Options", "nosniff");
                h.TryAdd("X-Frame-Options", "SAMEORIGIN");
                h.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
                h.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
                h.TryAdd("Content-Security-Policy",
                    "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
                    "img-src 'self' data: blob:; frame-src 'self' blob:; connect-src 'self'; " +
                    "object-src 'none'; base-uri 'self'; frame-ancestors 'self'");
                return Task.CompletedTask;
            });
            await next();
        });

    private static string PartitionKey(HttpContext http)
    {
        var sub = http.User?.FindFirstValue("sub")
                  ?? http.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(sub)) return $"u:{sub}";
        return $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
