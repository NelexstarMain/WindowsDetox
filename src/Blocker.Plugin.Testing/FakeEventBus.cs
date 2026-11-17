using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blocker.Sdk;
using Blocker.Sdk.Events;

namespace Blocker.Plugin.Testing;

/// <summary>Event bus with the capability and payload rules of the production host. Delivery is inline and sequential.</summary>
internal sealed partial class FakeEventBus(PluginInfo plugin, TimeProvider time) : IEventBus
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : class, IBlockerEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        Require(PluginCapabilities.RequiredForEvent(typeof(TEvent)));
        return Add(new Subscription(typeof(TEvent), null, (e, ct) => handler((TEvent)e, ct)));
    }

    public IDisposable SubscribeCustom(string topic, Func<CustomPluginEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Require(PluginCapability.CustomEvents);
        ArgumentException.ThrowIfNullOrEmpty(topic);
        return Add(new Subscription(typeof(CustomPluginEvent), topic, (e, ct) => handler((CustomPluginEvent)e, ct)));
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
        return DispatchAsync(new CustomPluginEvent($"{id}/{eventName}", id, payloadJson, time.GetUtcNow()), cancellationToken);
    }

    public async ValueTask DispatchAsync(IBlockerEvent e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);
        Subscription[] targets;
        lock (_gate)
        {
            targets = [.. _subscriptions.Where(s => s.EventType.IsInstanceOfType(e)
                && (s.Topic is null || (e is CustomPluginEvent c && string.Equals(c.Topic, s.Topic, StringComparison.Ordinal))))];
        }

        foreach (Subscription s in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await s.Handler(e, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Require(PluginCapability capability)
    {
        if (!plugin.Has(capability))
        {
            throw new PluginCapabilityException(capability);
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

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventNamePattern();

    private sealed record Subscription(Type EventType, string? Topic, Func<IBlockerEvent, CancellationToken, ValueTask> Handler);
}

/// <summary>Size and syntax checks shared by custom events and IPC payloads.</summary>
internal static class PayloadValidator
{
    public static void ThrowIfInvalid(string json, string paramName)
    {
        if (Encoding.UTF8.GetByteCount(json) > BlockerSdk.MaxPayloadBytes)
        {
            throw new ArgumentException($"Payload exceeds {BlockerSdk.MaxPayloadBytes} bytes.", paramName);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Payload is not valid JSON: {ex.Message}", paramName, ex);
        }
    }
}
