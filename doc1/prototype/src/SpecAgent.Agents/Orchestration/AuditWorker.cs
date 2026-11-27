using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpecAgent.Core.Events;

namespace SpecAgent.Agents.Orchestration;

public sealed class AuditQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    public ValueTask EnqueueAsync(string sessionId) => _channel.Writer.WriteAsync(sessionId);
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Tracks per-session CancellationTokenSources so a user can cancel a single running audit
/// without affecting other concurrent sessions or host graceful shutdown (Crash &amp; Resume).
/// </summary>
public sealed class AuditSessionRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.OrdinalIgnoreCase);

    public sealed class SessionLease(
        string sessionId,
        CancellationToken token,
        CancellationTokenSource userCts,
        CancellationTokenSource linkedCts,
        AuditSessionRegistry owner) : IDisposable
    {
        public CancellationToken Token { get; } = token;

        public void Dispose()
        {
            owner.Release(sessionId, userCts);
            linkedCts.Dispose();
            userCts.Dispose();
        }
    }

    public bool TryBegin(string sessionId, CancellationToken hostStoppingToken, out SessionLease? lease)
    {
        var userCts = new CancellationTokenSource();
        if (!_active.TryAdd(sessionId, userCts))
        {
            userCts.Dispose();
            lease = null;
            return false;
        }
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken, userCts.Token);
        lease = new SessionLease(sessionId, linkedCts.Token, userCts, linkedCts, this);
        return true;
    }

    public SessionLease Begin(string sessionId, CancellationToken hostStoppingToken)
    {
        var userCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken, userCts.Token);
        _active[sessionId] = userCts;
        return new SessionLease(sessionId, linkedCts.Token, userCts, linkedCts, this);
    }

    /// <summary>Requests cancellation for an active session. Returns true if the session was actively executing.</summary>
    public bool TryCancel(string sessionId)
    {
        if (!_active.TryGetValue(sessionId, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    public bool IsUserCancelled(string sessionId) =>
        _active.TryGetValue(sessionId, out var cts) && cts.IsCancellationRequested;

    public bool IsActive(string sessionId) => _active.ContainsKey(sessionId);

    private void Release(string sessionId, CancellationTokenSource expected) =>
        _active.TryRemove(new KeyValuePair<string, CancellationTokenSource>(sessionId, expected));
}

/// <summary>
/// Runs audits in the background. On startup it wakes every session that has no terminal event —
/// so killing the container mid-audit loses nothing (with a durable event store).
/// </summary>
public sealed class AuditWorker(
    AuditQueue queue,
    AuditSessionRegistry registry,
    IEventStore events,
    IServiceScopeFactory scopes,
    ILogger<AuditWorker> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var open in await events.ListOpenSessionsAsync(stoppingToken))
        {
            logger.LogInformation("Waking unfinished session {Session}", open);
            await queue.EnqueueAsync(open);
        }

        await foreach (var sessionId in queue.ReadAllAsync(stoppingToken))
        {
            await _gate.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!registry.TryBegin(sessionId, stoppingToken, out var lease) || lease is null)
                    {
                        logger.LogInformation("Skipping duplicate queued execution for active session {Session}", sessionId);
                        return;
                    }
                    using (lease)
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<AuditOrchestrator>().RunAsync(sessionId, lease.Token);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Host stopping — session {Session} left open for resume", sessionId);
                }
                catch (Exception ex) { logger.LogError(ex, "Worker crashed on {Session}", sessionId); }
                finally { _gate.Release(); }
            }, CancellationToken.None);
        }
    }
}
