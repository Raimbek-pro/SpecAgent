using System.Collections.Concurrent;

namespace SpecAgent.Core.Events;

/// <summary>Dev/test store. Use PostgresEventStore when the process must survive restarts.</summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, List<AuditEvent>> _sessions = new();

    public Task<AuditEvent> AppendAsync(string sessionId, string type, string agent, string message,
        object? payload = null, CancellationToken ct = default)
    {
        var list = _sessions.GetOrAdd(sessionId, _ => []);
        lock (list)
        {
            var e = new AuditEvent(list.Count + 1, sessionId, type, agent, DateTimeOffset.UtcNow, message,
                EventJson.ToElement(payload));
            list.Add(e);
            return Task.FromResult(e);
        }
    }

    public Task<IReadOnlyList<AuditEvent>> ReadAsync(string sessionId, long afterSeq = 0, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var list)) return Task.FromResult<IReadOnlyList<AuditEvent>>([]);
        lock (list) return Task.FromResult<IReadOnlyList<AuditEvent>>(list.Where(e => e.Seq > afterSeq).ToList());
    }

    public Task<IReadOnlyList<string>> ListOpenSessionsAsync(CancellationToken ct = default)
    {
        var open = _sessions
            .Where(kv => { lock (kv.Value) return !kv.Value.Any(e => EventTypes.Terminal.Contains(e.Type)); })
            .Select(kv => kv.Key).ToList();
        return Task.FromResult<IReadOnlyList<string>>(open);
    }
}
