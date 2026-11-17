using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Ipc;
using Blocker.Sdk.Session;
using Blocker.Sdk.Storage;

namespace Blocker.Sdk;

/// <summary>
/// Services the host provides to a plugin. Every member is scoped to the calling plugin: storage is isolated,
/// IPC methods are namespaced with the plugin id and capability checks use the plugin's manifest.
/// </summary>
public interface IPluginContext
{
    /// <summary>Gets identity and granted capabilities of the plugin, as read from its manifest.</summary>
    PluginInfo Plugin { get; }

    /// <summary>Gets the event bus used to observe core events and exchange custom plugin events.</summary>
    IEventBus Events { get; }

    /// <summary>Gets the registry used to add backend and UI extensions.</summary>
    IExtensionRegistry Extensions { get; }

    /// <summary>Gets the registry used to expose plugin-specific IPC methods.</summary>
    IIpcRegistry Ipc { get; }

    /// <summary>Gets the plugin's private, quota-limited key/value storage.</summary>
    IPluginStorage Storage { get; }

    /// <summary>Gets read-only information about the current detox session.</summary>
    ISessionInfo Session { get; }

    /// <summary>
    /// Gets the host clock. <see cref="TimeProvider.GetUtcNow"/> returns network-verified time and
    /// <see cref="TimeProvider.GetTimestamp"/> is monotonic; never use <see cref="DateTime.Now"/> in plugins.
    /// </summary>
    TimeProvider Time { get; }

    /// <summary>Gets the rate-limited logger for this plugin.</summary>
    IPluginLogger Logger { get; }
}
