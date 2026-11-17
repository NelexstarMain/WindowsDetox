using System.Text.RegularExpressions;
using Blocker.Sdk;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Ipc;
using Blocker.UI.Sdk;

namespace Blocker.Plugin.Testing;

/// <summary>Extension registry enforcing <see cref="ExtensionCapabilities"/>; also schedules tick listeners.</summary>
internal sealed class FakeExtensionRegistry(PluginInfo plugin, TimeProvider time) : IExtensionRegistry
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];

    public IReadOnlyList<IPluginExtension> Extensions
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(e => e.Extension)];
            }
        }
    }

    public IDisposable Add(IPluginExtension extension)
    {
        IReadOnlyList<PluginCapability> required = ExtensionCapabilities.GetRequired(extension);
        if (required.Count == 0)
        {
            throw new ArgumentException($"'{extension.GetType().Name}' implements no known extension interface.", nameof(extension));
        }

        foreach (PluginCapability capability in required.Where(c => !plugin.Has(c)))
        {
            throw new PluginCapabilityException(capability);
        }

        var entry = new Entry(extension, extension is ITickListener t ? time.GetUtcNow() + Interval(t) : null);
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return new Registration(() =>
        {
            lock (_gate)
            {
                _entries.Remove(entry);
            }
        });
    }

    /// <summary>Returns the registered tick listener due first at or before <paramref name="limit"/> and schedules its next tick.</summary>
    public (ITickListener Listener, DateTimeOffset Due)? TakeNextDue(DateTimeOffset limit)
    {
        lock (_gate)
        {
            Entry? next = null;
            foreach (Entry e in _entries)
            {
                if (e.NextDue is { } due && due <= limit && (next is null || due < next.NextDue))
                {
                    next = e;
                }
            }

            if (next is null)
            {
                return null;
            }

            var listener = (ITickListener)next.Extension;
            DateTimeOffset taken = next.NextDue!.Value;
            next.NextDue = taken + Interval(listener);
            return (listener, taken);
        }
    }

    private static TimeSpan Interval(ITickListener listener) =>
        listener.Interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : listener.Interval;

    private sealed class Entry(IPluginExtension extension, DateTimeOffset? nextDue)
    {
        public IPluginExtension Extension { get; } = extension;

        public DateTimeOffset? NextDue { get; set; } = nextDue;
    }
}

/// <summary>IPC registry enforcing the <see cref="PluginCapability.Ipc"/> capability, method syntax and uniqueness.</summary>
internal sealed partial class FakeIpcRegistry(PluginInfo plugin) : IIpcRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Func<string, CancellationToken, ValueTask<string>>> _handlers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Methods
    {
        get
        {
            lock (_gate)
            {
                return [.. _handlers.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    public IDisposable Register(string method, Func<string, CancellationToken, ValueTask<string>> handler)
    {
        if (!plugin.Has(PluginCapability.Ipc))
        {
            throw new PluginCapabilityException(PluginCapability.Ipc);
        }

        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        if (!MethodPattern().IsMatch(method))
        {
            throw new ArgumentException($"'{method}' is not a valid IPC method name (lowercase first letter, then letters or digits, max 64).", nameof(method));
        }

        lock (_gate)
        {
            if (!_handlers.TryAdd(method, handler))
            {
                throw new ArgumentException($"IPC method '{method}' is already registered.", nameof(method));
            }
        }

        return new Registration(() =>
        {
            lock (_gate)
            {
                _handlers.Remove(method);
            }
        });
    }

    public async ValueTask<string> InvokeAsync(string method, string payloadJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(payloadJson);
        Func<string, CancellationToken, ValueTask<string>>? handler;
        lock (_gate)
        {
            _handlers.TryGetValue(method, out handler);
        }

        if (handler is null)
        {
            throw new InvalidOperationException($"IPC method '{method}' is not registered.");
        }

        PayloadValidator.ThrowIfInvalid(payloadJson, nameof(payloadJson));
        string response = await handler(payloadJson, cancellationToken).ConfigureAwait(false);
        try
        {
            PayloadValidator.ThrowIfInvalid(response, nameof(response));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"IPC method '{method}' returned an invalid response. {ex.Message}", ex);
        }

        return response;
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex MethodPattern();
}

/// <summary>Counts re-render requests.</summary>
internal sealed class CountingSurface : IUiSurface
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    public void RequestRender() => Interlocked.Increment(ref _requests);
}
