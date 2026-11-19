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
/// Runs audits in the background. On startup it wakes every session that has no terminal event —
/// so killing the container mid-audit loses nothing (with a durable event store).
/// </summary>
public sealed class AuditWorker(AuditQueue queue, IEventStore events, IServiceScopeFactory scopes, ILogger<AuditWorker> logger)
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
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<AuditOrchestrator>().RunAsync(sessionId, stoppingToken);
                }
                catch (Exception ex) { logger.LogError(ex, "Worker crashed on {Session}", sessionId); }
                finally { _gate.Release(); }
            }, stoppingToken);
        }
    }
}
