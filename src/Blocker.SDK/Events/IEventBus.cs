namespace Blocker.Sdk.Events;

/// <summary>
/// Publish/subscribe access to Blocker events. Handlers run on the host's dispatcher with the
/// <see cref="BlockerSdk.CallbackBudgetMilliseconds"/> budget; a slow or throwing handler counts against the
/// plugin's circuit breaker but never delays other plugins or the core.
/// </summary>
public interface IEventBus
{
    /// <summary>Subscribes to a core event type.</summary>
    /// <typeparam name="TEvent">Core event type, for example <see cref="ProcessBlocked"/>.</typeparam>
    /// <param name="handler">Callback invoked for every event.</param>
    /// <returns>A registration; dispose it to unsubscribe.</returns>
    /// <exception cref="PluginCapabilityException">The capability required by <typeparamref name="TEvent"/> is missing.</exception>
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : class, IBlockerEvent;

    /// <summary>Subscribes to custom events on a topic (<c>{pluginId}/{eventName}</c>). Requires <see cref="PluginCapability.CustomEvents"/>.</summary>
    /// <param name="topic">Exact topic to observe.</param>
    /// <param name="handler">Callback invoked for every event.</param>
    /// <returns>A registration; dispose it to unsubscribe.</returns>
    IDisposable SubscribeCustom(string topic, Func<CustomPluginEvent, CancellationToken, ValueTask> handler);

    /// <summary>
    /// Publishes a custom event under the topic <c>{thisPluginId}/{eventName}</c>. Requires <see cref="PluginCapability.CustomEvents"/>.
    /// </summary>
    /// <param name="eventName">Event name: 1-64 characters, letters, digits, '-', '_' or '.'.</param>
    /// <param name="payloadJson">Valid JSON, at most <see cref="BlockerSdk.MaxPayloadBytes"/> bytes.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes once the event is queued.</returns>
    ValueTask PublishCustomAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default);
}
