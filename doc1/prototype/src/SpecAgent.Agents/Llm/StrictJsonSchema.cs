using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using SpecAgent.Core.Events;

namespace SpecAgent.Agents.Llm;

/// <summary>
/// Generates a structured-outputs-compatible JSON Schema from a C# type:
/// every object closed (additionalProperties:false) and every property required.
/// The C# record is the single source of truth for each agent's output contract.
/// </summary>
public static class StrictJsonSchema
{
    private static readonly JsonSerializerOptions Options = new(EventJson.Options)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static Dictionary<string, JsonElement> For<T>()
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (_, node) =>
            {
                if (node is JsonObject obj)
                {
                    if (obj.ContainsKey("enum") && !obj.ContainsKey("type"))
                        obj["type"] = "string";
                    if (obj["properties"] is JsonObject props)
                    {
                        obj["additionalProperties"] = false;
                        obj["required"] = new JsonArray(props.Select(p => (JsonNode)p.Key).ToArray());
                    }
                }
                return node;
            },
        };
        var schema = Options.GetJsonSchemaAsNode(typeof(T), exporter);
        return schema.AsObject().ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new LlmException($"Model returned empty JSON for {typeof(T).Name}");
}
