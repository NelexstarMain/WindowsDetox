namespace Blocker.Sdk;

/// <summary>
/// Identity and granted capabilities of a loaded plugin.
/// </summary>
/// <param name="Id">Unique plugin id from the manifest.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Version">Plugin version.</param>
/// <param name="Author">Plugin author.</param>
/// <param name="Capabilities">Capabilities granted to the plugin by its manifest.</param>
public sealed record PluginInfo(
    PluginId Id,
    string Name,
    Version Version,
    string Author,
    IReadOnlySet<PluginCapability> Capabilities)
{
    /// <summary>Returns whether the plugin was granted <paramref name="capability"/>.</summary>
    /// <param name="capability">Capability to check.</param>
    /// <returns><see langword="true"/> when the capability is granted.</returns>
    public bool Has(PluginCapability capability) => Capabilities.Contains(capability);
}
