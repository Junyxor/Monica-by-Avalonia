using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Monica.Data.Mdbx;

public sealed partial class MdbxVaultStore
{
    private static readonly JsonSerializerOptions LegacyPayloadWriteOptions = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static JsonObject? LegacyPayloadOwner(JsonObject root, string member, long localId)
    {
        var data = root["data"] as JsonObject;
        var owner = data?[member] as JsonObject ?? data;
        return owner?["id"] is JsonValue id && id.TryGetValue<long>(out var storedId) && storedId == localId
            ? owner : null;
    }

    private static string? RewriteLegacyPayload<T>(
        MdbxNativeEntryRecord? existing,
        string member,
        long localId,
        T model,
        IReadOnlyList<(string Name, object Value)> children)
    {
        if (existing is null) return null;
        var root = JsonNode.Parse(existing.PayloadJson) as JsonObject;
        if (root is null || LegacyPayloadOwner(root, member, localId) is not { } owner) return null;

        // A flat rewrite alongside the old data envelope leaves the reader seeing the old
        // model forever. Update the established envelope instead, preserving unknown fields.
        var data = (JsonObject)root["data"]!;
        if (ReferenceEquals(owner, data))
        {
            data = new JsonObject { [member] = owner.DeepClone() };
            root["data"] = data;
            owner = (JsonObject)data[member]!;
        }

        foreach (var property in JsonSerializer.SerializeToNode(model, LegacyPayloadWriteOptions)!.AsObject())
            owner[property.Key] = property.Value?.DeepClone();
        foreach (var (name, value) in children)
            data[name] = JsonSerializer.SerializeToNode(value, LegacyPayloadWriteOptions);
        return root.ToJsonString();
    }
}
