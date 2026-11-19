using System.Text.Json;

namespace SpecAgent.Core.Events;

public interface IEventStore
{
    Task<AuditEvent> AppendAsync(string sessionId, string type, string agent, string message,
        object? payload = null, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEvent>> ReadAsync(string sessionId, long afterSeq = 0, CancellationToken ct = default);

    /// <summary>Sessions that have started but have no terminal event — candidates for wake().</summary>
    Task<IReadOnlyList<string>> ListOpenSessionsAsync(CancellationToken ct = default);
}

public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement? ToElement(object? payload) =>
        payload is null ? null : JsonSerializer.SerializeToElement(payload, Options);

    public static T Get<T>(this AuditEvent e) =>
        e.Payload is { } p
            ? p.Deserialize<T>(Options) ?? throw new InvalidOperationException($"Empty payload in {e.Type}#{e.Seq}")
            : throw new InvalidOperationException($"No payload in {e.Type}#{e.Seq}");
}
