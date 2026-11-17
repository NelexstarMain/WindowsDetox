namespace Blocker.Sdk;

/// <summary>
/// Version information and global limits of the Blocker plugin SDK.
/// </summary>
public static class BlockerSdk
{
    /// <summary>
    /// Gets the version of the SDK contracts. A plugin manifest declares the SDK version it was written
    /// against; the host refuses plugins that target a newer major version than <see cref="Version"/>.
    /// </summary>
    public static Version Version { get; } = new(1, 0, 0);

    /// <summary>
    /// Time budget, in milliseconds, for a single synchronous plugin callback (interceptors, lock votes,
    /// event handlers, UI rendering). Callbacks that repeatedly exceed it trip the plugin's circuit breaker
    /// and move the plugin to the <c>Faulted</c> state.
    /// </summary>
    public const int CallbackBudgetMilliseconds = 50;

    /// <summary>
    /// Maximum size, in bytes, of a JSON payload exchanged through <see cref="Ipc.IIpcRegistry"/> or a
    /// custom event published on <see cref="Events.IEventBus"/>.
    /// </summary>
    public const int MaxPayloadBytes = 64 * 1024;
}
