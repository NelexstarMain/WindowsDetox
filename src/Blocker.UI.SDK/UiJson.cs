using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocker.UI.Sdk;

/// <summary>
/// Serializes UI trees for transport between the plugin host and the UI process. Deserialization is
/// strict (unknown properties and types are rejected); always run <see cref="UiTreeValidator"/> afterwards.
/// </summary>
public static class UiJson
{
    /// <summary>Maximum accepted size of a serialized tree, in UTF-8 bytes.</summary>
    public const int MaxDocumentBytes = 512 * 1024;

    /// <summary>Serializes a tree.</summary>
    /// <param name="node">Root node.</param>
    /// <returns>JSON text.</returns>
    public static string Serialize(UiNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return JsonSerializer.Serialize(node, UiJsonContext.Default.UiNode);
    }

    /// <summary>Deserializes a tree produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">JSON text.</param>
    /// <returns>The root node.</returns>
    /// <exception cref="JsonException">The JSON is malformed, too large, or contains unknown members.</exception>
    public static UiNode Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
        {
            throw new JsonException($"UI document exceeds {MaxDocumentBytes} bytes.");
        }

        return JsonSerializer.Deserialize(json, UiJsonContext.Default.UiNode)
            ?? throw new JsonException("UI document is empty.");
    }
}

/// <summary>Source-generated serializer metadata for UI trees.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    MaxDepth = 64,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UiNode))]
[JsonSerializable(typeof(UiAction))]
internal sealed partial class UiJsonContext : JsonSerializerContext
{
}
