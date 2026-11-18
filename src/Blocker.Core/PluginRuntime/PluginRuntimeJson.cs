using System.Text.Json.Serialization;

namespace Blocker.Core.PluginRuntime;

/// <summary>Content of <c>state.json</c> next to an installed plugin package.</summary>
/// <param name="Enabled">Whether the plugin is loaded by the host.</param>
internal sealed record PluginState(bool Enabled);

/// <summary>Source-generated, strict serializer metadata for the plugin store files.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    MaxDepth = 4)]
[JsonSerializable(typeof(PluginState))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class PluginRuntimeJsonContext : JsonSerializerContext
{
}
