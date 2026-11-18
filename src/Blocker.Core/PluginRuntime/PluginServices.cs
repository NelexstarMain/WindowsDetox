using System.Text.RegularExpressions;
using Blocker.Contracts;
using Blocker.Core.Events;
using Blocker.Core.Ipc;
using Blocker.Sdk;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Ipc;
using Blocker.Sdk.Session;
using Blocker.Sdk.Storage;
using Blocker.UI.Sdk;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.PluginRuntime;

/// <summary>The services of one plugin, as handed to <see cref="IBlockerPlugin.InitializeAsync"/>.</summary>
internal sealed record PluginHostContext(
    PluginInfo Plugin,
    IEventBus Events,
    IExtensionRegistry Extensions,
    IIpcRegistry Ipc,
    IPluginStorage Storage,
    ISessionInfo Session,
    TimeProvider Time,
    IPluginLogger Logger) : IPluginContext;

/// <summary>Session snapshot shared by all plugins; replaced as a whole whenever the service reports a change.</summary>
internal sealed class HostSessionInfo : ISessionInfo
{
    private SessionSnapshot _current = SessionSnapshot.Idle;

    public SessionSnapshot Current => Volatile.Read(ref _current);

    public void Set(SessionSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}

/// <summary>Idempotent <see cref="IDisposable"/> that runs a callback once.</summary>
internal sealed class DisposeAction(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
}

/// <summary>One registered extension. Its <see cref="Lifetime"/> ends when it is unregistered or the plugin is unloaded.</summary>
internal sealed class ExtensionEntry(int index, IPluginExtension extension) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Gets the registration number, unique within the plugin; part of contribution ids.</summary>
    public int Index { get; } = index;

    public IPluginExtension Extension { get; } = extension;

    public CancellationToken Lifetime => _lifetime.Token;

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>
/// Production <see cref="IExtensionRegistry"/>: enforces <see cref="ExtensionCapabilities"/> (the same table the testing kit uses),
/// tracks every registration, and refuses registrations once the plugin was unloaded or faulted.
/// </summary>
internal sealed class PluginExtensionRegistry(PluginInfo plugin) : IExtensionRegistry
{
    private readonly Lock _gate = new();
    private readonly List<ExtensionEntry> _entries = [];
    private int _nextIndex;
    private bool _closed;

    /// <summary>Raised (outside any lock) after an extension was registered.</summary>
    public event Action<ExtensionEntry>? Added;

    public IDisposable Add(IPluginExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        IReadOnlyList<PluginCapability> required = ExtensionCapabilities.GetRequired(extension);
        if (required.Count == 0)
        {
            throw new ArgumentException($"'{extension.GetType().Name}' implements no known extension interface.", nameof(extension));
        }

        foreach (PluginCapability capability in required.Where(c => !plugin.Has(c)))
        {
            throw new PluginCapabilityException(capability);
        }

        ExtensionEntry entry;
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("The plugin is no longer active; extensions cannot be registered.");
            }

            entry = new ExtensionEntry(_nextIndex++, extension);
            _entries.Add(entry);
        }

        Added?.Invoke(entry);
        return new DisposeAction(() => Remove(entry));
    }

    public IReadOnlyList<ExtensionEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <summary>Removes every extension and refuses further registrations.</summary>
    public void Close()
    {
        ExtensionEntry[] removed;
        lock (_gate)
        {
            _closed = true;
            removed = [.. _entries];
            _entries.Clear();
        }

        foreach (ExtensionEntry entry in removed)
        {
            entry.Dispose();
        }
    }

    private void Remove(ExtensionEntry entry)
    {
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(entry);
        }

        if (removed)
        {
            entry.Dispose();
        }
    }
}

/// <summary>
/// Production <see cref="IIpcRegistry"/>: requires <see cref="PluginCapability.Ipc"/>, validates method names, and exposes the
/// methods under their wire names <c>plugin.{pluginId}.{method}</c>. Request and response payloads are limited to
/// <see cref="BlockerSdk.MaxPayloadBytes"/>.
/// </summary>
internal sealed partial class PluginIpcRegistry(PluginInfo plugin) : IIpcRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Func<string, CancellationToken, ValueTask<string>>> _handlers = new(StringComparer.Ordinal);
    private bool _closed;

    /// <summary>Gets the wire names of all registered methods, in ordinal order.</summary>
    public IReadOnlyList<string> WireMethods
    {
        get
        {
            lock (_gate)
            {
                return [.. _handlers.Keys.Select(m => IpcMethods.ForPlugin(plugin.Id.Value, m)).Order(StringComparer.Ordinal)];
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
            if (_closed)
            {
                throw new InvalidOperationException("The plugin is no longer active; IPC methods cannot be registered.");
            }

            if (!_handlers.TryAdd(method, handler))
            {
                throw new ArgumentException($"IPC method '{method}' is already registered.", nameof(method));
            }
        }

        return new DisposeAction(() =>
        {
            lock (_gate)
            {
                _handlers.Remove(method);
            }
        });
    }

    /// <summary>Returns whether a method with this plugin-local name is registered.</summary>
    public bool Contains(string method)
    {
        lock (_gate)
        {
            return _handlers.ContainsKey(method);
        }
    }

    /// <summary>Calls a registered handler. The payload was already size-checked by the caller; the response is checked here.</summary>
    /// <exception cref="InvalidOperationException">The method is not registered or the plugin returned an invalid response.</exception>
    public async ValueTask<string> InvokeAsync(string method, string payloadJson, CancellationToken cancellationToken)
    {
        Func<string, CancellationToken, ValueTask<string>>? handler;
        lock (_gate)
        {
            _handlers.TryGetValue(method, out handler);
        }

        if (handler is null)
        {
            throw new InvalidOperationException($"IPC method '{method}' is not registered.");
        }

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

    /// <summary>Removes every method and refuses further registrations.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _handlers.Clear();
        }
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex MethodPattern();
}

/// <summary>
/// Production <see cref="IPluginLogger"/>: forwards to the host logger with the plugin id, truncates and sanitizes the text,
/// and drops entries beyond a token-bucket rate (burst 20, 5 per second), counting what was suppressed.
/// </summary>
internal sealed partial class RateLimitedPluginLogger(PluginId pluginId, ILogger logger, TimeProvider time) : IPluginLogger
{
    private const int MaxMessageLength = 2000;
    private readonly TokenBucket _bucket = new(5, 20, time);
    private long _suppressed;

    public void Log(PluginLogLevel level, string message, Exception? exception = null)
    {
        if (!_bucket.TryTake())
        {
            Interlocked.Increment(ref _suppressed);
            return;
        }

        long dropped = Interlocked.Exchange(ref _suppressed, 0);
        if (dropped > 0)
        {
            LogEntry(logger, LogLevel.Warning, null, pluginId.Value, $"{dropped} log entries were suppressed by the rate limit.");
        }

        LogLevel mapped = level switch
        {
            PluginLogLevel.Debug => LogLevel.Debug,
            PluginLogLevel.Information => LogLevel.Information,
            PluginLogLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Error,
        };
        LogEntry(logger, mapped, exception, pluginId.Value, Sanitize(message));
    }

    private static string Sanitize(string? message)
    {
        string text = string.Concat((message ?? string.Empty).Select(c => char.IsControl(c) ? '?' : c));
        return text.Length <= MaxMessageLength ? text : string.Concat(text.AsSpan(0, MaxMessageLength - 3), "...");
    }

    [LoggerMessage(Message = "[plugin {PluginId}] {Message}")]
    private static partial void LogEntry(ILogger logger, LogLevel level, Exception? exception, string pluginId, string message);
}
