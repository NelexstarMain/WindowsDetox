namespace Blocker.Sdk;

/// <summary>
/// Thrown when a plugin calls an API that requires a capability it did not declare in <c>plugin.json</c>.
/// </summary>
public sealed class PluginCapabilityException : InvalidOperationException
{
    /// <summary>Creates the exception for a missing capability.</summary>
    /// <param name="capability">The capability that was required.</param>
    public PluginCapabilityException(PluginCapability capability)
        : base($"This operation requires the '{PluginCapabilities.ToManifestName(capability)}' capability. Declare it in plugin.json.")
    {
        Capability = capability;
    }

    /// <summary>Gets the capability that was required.</summary>
    public PluginCapability Capability { get; }
}
