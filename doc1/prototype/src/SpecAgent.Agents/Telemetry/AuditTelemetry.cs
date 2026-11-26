using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Npgsql;

namespace SpecAgent.Agents.Telemetry;

public sealed record SessionUsage(long InputTokens, long OutputTokens, decimal EstimatedCostUsd, int LlmCalls);

/// <summary>
/// Ambient AsyncLocal carrier for the active audit session id so LLM clients can attribute token
/// usage and USD cost to the current session across parallel Task.WhenAll critic branches.
/// </summary>
public static class AuditTelemetryContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CurrentSessionId => Current.Value;

    public static IDisposable BeginSession(string sessionId)
    {
        var previous = Current.Value;
        Current.Value = sessionId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>USD per 1M input / output tokens for cost estimation across supported providers.</summary>
public static class ModelPricing
{
    public static decimal EstimateCostUsd(string model, long inputTokens, long outputTokens)
    {
        var (inPerMillion, outPerMillion) = RatesPerMillion(model);
        var cost = (inputTokens * inPerMillion + outputTokens * outPerMillion) / 1_000_000m;
        return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
    }

    private static (decimal Input, decimal Output) RatesPerMillion(string model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("kimi-k3.1-pro")) return (1.20m, 4.00m);
        if (m.Contains("kimi")) return (0.60m, 2.50m);
        if (m.Contains("qwen4-max")) return (1.50m, 4.50m);
        if (m.Contains("qwen3.7-plus")) return (0.40m, 1.60m);
        if (m.Contains("qwen3.7-flash")) return (0.03m, 0.13m);
        if (m.Contains("gemini-4-argon")) return (2.00m, 10.00m);
        if (m.Contains("gemini-4-hydrogen")) return (1.50m, 7.50m);
        if (m.Contains("gemini") && m.Contains("flash-lite")) return (0.10m, 0.40m);
        if (m.Contains("gemini") && m.Contains("flash")) return (0.75m, 3.75m);
        if (m.Contains("claude-opus")) return (5.00m, 25.00m);
        if (m.Contains("claude-sonnet")) return (3.00m, 15.00m);
        if (m.Contains("claude-haiku")) return (0.80m, 4.00m);
        return (0.50m, 2.00m);
    }
}

public interface ISessionUsageStore
{
    Task RecordUsageAsync(
        string sessionId,
        string agent,
        string model,
        long inputTokens,
        long outputTokens,
        decimal costUsd,
        int durationMs,
        CancellationToken ct = default);

    Task<SessionUsage> GetUsageAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>Persists per-session token usage and USD cost onto audit_sessions in PostgreSQL.</summary>
public sealed class PostgresSessionUsageStore(NpgsqlDataSource db) : ISessionUsageStore
{
    public const string MigrationVersion = "004_session_telemetry";

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

            ALTER TABLE audit_sessions
                ADD COLUMN IF NOT EXISTS input_tokens       bigint         NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS output_tokens      bigint         NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS estimated_cost_usd numeric(12, 6) NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS llm_calls          int            NOT NULL DEFAULT 0;

            INSERT INTO schema_migrations (version)
            VALUES ('004_session_telemetry')
            ON CONFLICT (version) DO NOTHING;
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task RecordUsageAsync(
        string sessionId,
        string agent,
        string model,
        long inputTokens,
        long outputTokens,
        decimal costUsd,
        int durationMs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        await using var cmd = db.CreateCommand("""
            UPDATE audit_sessions
            SET input_tokens       = input_tokens + @in,
                output_tokens      = output_tokens + @out,
                estimated_cost_usd = estimated_cost_usd + @cost,
                llm_calls          = llm_calls + 1,
                updated_at         = now()
            WHERE session_id = @s
            """);
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("in", inputTokens);
        cmd.Parameters.AddWithValue("out", outputTokens);
        cmd.Parameters.AddWithValue("cost", costUsd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionUsage> GetUsageAsync(string sessionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT input_tokens, output_tokens, estimated_cost_usd, llm_calls
            FROM audit_sessions
            WHERE session_id = @s
            """);
        cmd.Parameters.AddWithValue("s", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new SessionUsage(0, 0, 0m, 0);
        return new SessionUsage(r.GetInt64(0), r.GetInt64(1), r.GetDecimal(2), r.GetInt32(3));
    }
}

public sealed class InMemorySessionUsageStore : ISessionUsageStore
{
    private readonly ConcurrentDictionary<string, SessionUsage> _usage = new(StringComparer.Ordinal);

    public Task RecordUsageAsync(
        string sessionId,
        string agent,
        string model,
        long inputTokens,
        long outputTokens,
        decimal costUsd,
        int durationMs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Task.CompletedTask;
        _usage.AddOrUpdate(
            sessionId,
            _ => new SessionUsage(inputTokens, outputTokens, costUsd, 1),
            (_, cur) => new SessionUsage(
                cur.InputTokens + inputTokens,
                cur.OutputTokens + outputTokens,
                cur.EstimatedCostUsd + costUsd,
                cur.LlmCalls + 1));
        return Task.CompletedTask;
    }

    public Task<SessionUsage> GetUsageAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(_usage.TryGetValue(sessionId, out var u) ? u : new SessionUsage(0, 0, 0m, 0));
}

/// <summary>
/// OpenTelemetry Meter ("SpecAgent") + lightweight Prometheus exposition renderer for GET /metrics.
/// </summary>
public sealed class SpecAgentMetrics : IDisposable
{
    private readonly Meter _meter = new("SpecAgent", "1.0.0");
    private readonly Counter<long> _auditsStartedCounter;
    private readonly Counter<long> _auditsCompletedCounter;
    private readonly Counter<long> _auditsFailedCounter;
    private readonly Counter<long> _auditsCancelledCounter;
    private readonly Counter<long> _llmCallsCounter;
    private readonly Counter<long> _llmInputTokensCounter;
    private readonly Counter<long> _llmOutputTokensCounter;
    private readonly Histogram<double> _llmDurationHistogram;

    private long _auditsStarted;
    private long _auditsCompleted;
    private long _auditsFailed;
    private long _auditsCancelled;
    private readonly ConcurrentDictionary<(string Agent, string Model, string Status), long> _llmCalls = new();
    private readonly ConcurrentDictionary<(string Agent, string Model), long> _inputTokens = new();
    private readonly ConcurrentDictionary<(string Agent, string Model), long> _outputTokens = new();
    private readonly ConcurrentDictionary<string, double> _costUsd = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Agent, string Model), (long Count, long TotalMs)> _latency = new();

    public SpecAgentMetrics()
    {
        _auditsStartedCounter = _meter.CreateCounter<long>("specagent.audits.started");
        _auditsCompletedCounter = _meter.CreateCounter<long>("specagent.audits.completed");
        _auditsFailedCounter = _meter.CreateCounter<long>("specagent.audits.failed");
        _auditsCancelledCounter = _meter.CreateCounter<long>("specagent.audits.cancelled");
        _llmCallsCounter = _meter.CreateCounter<long>("specagent.llm.calls");
        _llmInputTokensCounter = _meter.CreateCounter<long>("specagent.llm.tokens.input");
        _llmOutputTokensCounter = _meter.CreateCounter<long>("specagent.llm.tokens.output");
        _llmDurationHistogram = _meter.CreateHistogram<double>("specagent.llm.duration.ms", "ms");
    }

    public void RecordAuditStarted()
    {
        Interlocked.Increment(ref _auditsStarted);
        _auditsStartedCounter.Add(1);
    }

    public void RecordAuditCompleted()
    {
        Interlocked.Increment(ref _auditsCompleted);
        _auditsCompletedCounter.Add(1);
    }

    public void RecordAuditFailed()
    {
        Interlocked.Increment(ref _auditsFailed);
        _auditsFailedCounter.Add(1);
    }

    public void RecordAuditCancelled()
    {
        Interlocked.Increment(ref _auditsCancelled);
        _auditsCancelledCounter.Add(1);
    }

    public void RecordLlmCall(string agent, string model, bool success, long inputTokens, long outputTokens, decimal costUsd, int durationMs)
    {
        var status = success ? "ok" : "error";
        _llmCalls.AddOrUpdate((agent, model, status), 1, (_, n) => n + 1);
        _llmCallsCounter.Add(1,
            new KeyValuePair<string, object?>("agent", agent),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("status", status));

        if (inputTokens > 0)
        {
            _inputTokens.AddOrUpdate((agent, model), inputTokens, (_, n) => n + inputTokens);
            _llmInputTokensCounter.Add(inputTokens,
                new KeyValuePair<string, object?>("agent", agent),
                new KeyValuePair<string, object?>("model", model));
        }

        if (outputTokens > 0)
        {
            _outputTokens.AddOrUpdate((agent, model), outputTokens, (_, n) => n + outputTokens);
            _llmOutputTokensCounter.Add(outputTokens,
                new KeyValuePair<string, object?>("agent", agent),
                new KeyValuePair<string, object?>("model", model));
        }

        if (costUsd > 0)
        {
            _costUsd.AddOrUpdate(model, (double)costUsd, (_, cur) => cur + (double)costUsd);
        }

        _latency.AddOrUpdate((agent, model), (1, durationMs), (_, cur) => (cur.Count + 1, cur.TotalMs + durationMs));
        _llmDurationHistogram.Record(durationMs,
            new KeyValuePair<string, object?>("agent", agent),
            new KeyValuePair<string, object?>("model", model));
    }

    public string RenderPrometheus()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("# HELP specagent_audits_started_total Total number of audit runs started.");
        sb.AppendLine("# TYPE specagent_audits_started_total counter");
        sb.AppendLine($"specagent_audits_started_total {Interlocked.Read(ref _auditsStarted)}");

        sb.AppendLine("# HELP specagent_audits_completed_total Total number of audit runs completed.");
        sb.AppendLine("# TYPE specagent_audits_completed_total counter");
        sb.AppendLine($"specagent_audits_completed_total {Interlocked.Read(ref _auditsCompleted)}");

        sb.AppendLine("# HELP specagent_audits_failed_total Total number of audit runs failed.");
        sb.AppendLine("# TYPE specagent_audits_failed_total counter");
        sb.AppendLine($"specagent_audits_failed_total {Interlocked.Read(ref _auditsFailed)}");

        sb.AppendLine("# HELP specagent_audits_cancelled_total Total number of audit runs cancelled by users.");
        sb.AppendLine("# TYPE specagent_audits_cancelled_total counter");
        sb.AppendLine($"specagent_audits_cancelled_total {Interlocked.Read(ref _auditsCancelled)}");

        sb.AppendLine("# HELP specagent_llm_calls_total Total number of LLM API calls.");
        sb.AppendLine("# TYPE specagent_llm_calls_total counter");
        foreach (var kv in _llmCalls.OrderBy(k => k.Key.Agent).ThenBy(k => k.Key.Model))
            sb.AppendLine($"specagent_llm_calls_total{{agent=\"{Esc(kv.Key.Agent)}\",model=\"{Esc(kv.Key.Model)}\",status=\"{Esc(kv.Key.Status)}\"}} {kv.Value}");

        sb.AppendLine("# HELP specagent_llm_tokens_input_total Total LLM input tokens consumed.");
        sb.AppendLine("# TYPE specagent_llm_tokens_input_total counter");
        foreach (var kv in _inputTokens.OrderBy(k => k.Key.Agent).ThenBy(k => k.Key.Model))
            sb.AppendLine($"specagent_llm_tokens_input_total{{agent=\"{Esc(kv.Key.Agent)}\",model=\"{Esc(kv.Key.Model)}\"}} {kv.Value}");

        sb.AppendLine("# HELP specagent_llm_tokens_output_total Total LLM output tokens generated.");
        sb.AppendLine("# TYPE specagent_llm_tokens_output_total counter");
        foreach (var kv in _outputTokens.OrderBy(k => k.Key.Agent).ThenBy(k => k.Key.Model))
            sb.AppendLine($"specagent_llm_tokens_output_total{{agent=\"{Esc(kv.Key.Agent)}\",model=\"{Esc(kv.Key.Model)}\"}} {kv.Value}");

        sb.AppendLine("# HELP specagent_llm_cost_usd_total Estimated cumulative LLM API cost in USD.");
        sb.AppendLine("# TYPE specagent_llm_cost_usd_total counter");
        foreach (var kv in _costUsd.OrderBy(k => k.Key))
            sb.AppendLine($"specagent_llm_cost_usd_total{{model=\"{Esc(kv.Key)}\"}} {kv.Value.ToString("F6", inv)}");

        sb.AppendLine("# HELP specagent_llm_duration_ms_sum Cumulative LLM latency in milliseconds.");
        sb.AppendLine("# TYPE specagent_llm_duration_ms_sum counter");
        foreach (var kv in _latency.OrderBy(k => k.Key.Agent).ThenBy(k => k.Key.Model))
            sb.AppendLine($"specagent_llm_duration_ms_sum{{agent=\"{Esc(kv.Key.Agent)}\",model=\"{Esc(kv.Key.Model)}\"}} {kv.Value.TotalMs}");

        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose() => _meter.Dispose();
}
