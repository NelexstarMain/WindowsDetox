using System.Text.RegularExpressions;
using System.Threading.Channels;
using Blocker.Core.Abstractions;
using Blocker.Core.Resilience;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Events;

/// <summary>
/// Host event bus: events are queued (drop-oldest when full) and dispatched by a background loop. Every subscriber
/// runs concurrently through its plugin's <see cref="PluginCircuitBreaker"/>, so a slow plugin never delays others
/// beyond its budget.
/// </summary>
public sealed partial class HostEventBus : IEventPublisher, IAsyncDisposable
{
    private readonly TimeProvider _time;
    private readonly ILogger<HostEventBus> _logger;
    private readonly Channel<IBlockerEvent> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Task _loop;
    private long _dropped;

    /// <summary>Creates the bus and starts its dispatch loop.</summary>
    /// <param name="time">Clock for custom event timestamps.</param>
    /// <param name="logger">Logger for subscriber failures.</param>
    /// <param name="capacity">Maximum queued events before the oldest is dropped.</param>
    public HostEventBus(TimeProvider time, ILogger<HostEventBus> logger, int capacity = 1024)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _time = time;
        _logger = logger;
        _channel = Channel.CreateBounded<IBlockerEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped));
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Gets the number of events dropped because the queue was full.</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);

    /// <inheritdoc/>
    public ValueTask PublishAsync(IBlockerEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        cancellationToken.ThrowIfCancellationRequested();
        _ = _channel.Writer.TryWrite(evt);
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates the <see cref="IEventBus"/> a plugin sees. Capability checks use <paramref name="plugin"/>.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="breaker">The plugin's circuit breaker; guards all of its handlers.</param>
    /// <returns>The plugin's bus.</returns>
    public IEventBus CreatePluginView(PluginInfo plugin, PluginCircuitBreaker breaker)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(breaker);
        return new PluginView(this, plugin, breaker);
    }

    /// <summary>Removes every subscription of <paramref name="pluginId"/>.</summary>
    /// <param name="pluginId">The plugin.</param>
    public void RemovePlugin(PluginId pluginId)
    {
        lock (_gate)
        {
            _subscriptions.RemoveAll(s => s.Plugin.Id == pluginId);
        }
    }

    /// <summary>Stops accepting events and waits until the queued ones are delivered.</summary>
    /// <param name="cancellationToken">Abandons the drain and cancels running handlers.</param>
    /// <returns>A task that completes when the dispatch loop has ended.</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryComplete();
        await using CancellationTokenRegistration _ = cancellationToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), _stop);
        await _loop.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (IBlockerEvent evt in _channel.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await DispatchAsync(evt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _channel.Writer.TryComplete();
        }
    }

    private async Task DispatchAsync(IBlockerEvent evt)
    {
        Subscription[] targets;
        lock (_gate)
        {
            targets = [.. _subscriptions.Where(s => s.Matches(evt))];
        }

        if (targets.Length > 0)
        {
            await Task.WhenAll(targets.Select(s => DeliverAsync(s, evt))).ConfigureAwait(false);
        }
    }

    private async Task DeliverAsync(Subscription s, IBlockerEvent evt)
    {
        try
        {
            BreakerResult<bool> result = await s.Breaker.ExecuteAsync(ct => s.Handler(s.Prepare(evt), ct), _stop.Token).ConfigureAwait(false);
            if (result.Outcome is BreakerOutcome.Timeout or BreakerOutcome.Exception)
            {
                LogHandlerFailed(_logger, result.Error, s.Plugin.Id.Value, evt.GetType().Name, result.Outcome);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Shutting down: remaining deliveries are abandoned.
        }
    }

    private Registration Add(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return new Registration(() =>
        {
            lock (_gate)
            {
                _subscriptions.Remove(subscription);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} handler for {EventType} failed: {Outcome}.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception? exception, string pluginId, string eventType, BreakerOutcome outcome);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventNamePattern();

    private sealed record Subscription(
        PluginInfo Plugin,
        PluginCircuitBreaker Breaker,
        Type EventType,
        string? Topic,
        Func<IBlockerEvent, CancellationToken, ValueTask> Handler)
    {
        public bool Matches(IBlockerEvent e) =>
            EventType.IsInstanceOfType(e)
            && (Topic is null || (e is CustomPluginEvent c && string.Equals(c.Topic, Topic, StringComparison.Ordinal)));

        // Plugins without the title capability never see window titles.
        public IBlockerEvent Prepare(IBlockerEvent e) =>
            e is ActiveWindowChanged { Window.Title: not null } w && !Plugin.Has(PluginCapability.WindowTitles)
                ? w with { Window = w.Window with { Title = null } }
                : e;
    }

    private sealed class Registration(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    private sealed class PluginView(HostEventBus bus, PluginInfo plugin, PluginCircuitBreaker breaker) : IEventBus
    {
        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
            where TEvent : class, IBlockerEvent
        {
            ArgumentNullException.ThrowIfNull(handler);
            Require(PluginCapabilities.RequiredForEvent(typeof(TEvent)));
            return bus.Add(new Subscription(plugin, breaker, typeof(TEvent), null, (e, ct) => handler((TEvent)e, ct)));
        }

        public IDisposable SubscribeCustom(string topic, Func<CustomPluginEvent, CancellationToken, ValueTask> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Require(PluginCapability.CustomEvents);
            ArgumentException.ThrowIfNullOrEmpty(topic);
            return bus.Add(new Subscription(plugin, breaker, typeof(CustomPluginEvent), topic, (e, ct) => handler((CustomPluginEvent)e, ct)));
        }

        public ValueTask PublishCustomAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Require(PluginCapability.CustomEvents);
            ArgumentNullException.ThrowIfNull(eventName);
            ArgumentNullException.ThrowIfNull(payloadJson);
            if (!EventNamePattern().IsMatch(eventName))
            {
                throw new ArgumentException($"'{eventName}' is not a valid event name (1-64 characters: letters, digits, '-', '_', '.').", nameof(eventName));
            }

            PayloadValidator.ThrowIfInvalid(payloadJson, nameof(payloadJson));
            string id = plugin.Id.Value;
            return bus.PublishAsync(new CustomPluginEvent($"{id}/{eventName}", id, payloadJson, bus._time.GetUtcNow()), cancellationToken);
        }

        private void Require(PluginCapability capability)
        {
            if (!plugin.Has(capability))
            {
                throw new PluginCapabilityException(capability);
            }
        }
    }
}
