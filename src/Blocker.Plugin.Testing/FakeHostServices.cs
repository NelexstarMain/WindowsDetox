using Blocker.Sdk;
using Blocker.Sdk.Notifications;
using Blocker.Sdk.Sessions;

namespace Blocker.Plugin.Testing;

/// <summary>Recording <see cref="ISessionLauncher"/>: enforces the capability and the duration limits, then returns <see cref="NextResult"/>.</summary>
public sealed class FakeSessionLauncher : ISessionLauncher
{
    private readonly PluginInfo _plugin;
    private readonly Lock _gate = new();
    private readonly List<(TimeSpan Duration, string Reason)> _calls = [];

    internal FakeSessionLauncher(PluginInfo plugin) => _plugin = plugin;

    /// <summary>Gets the accepted calls in order (refused ones are not recorded).</summary>
    public IReadOnlyList<(TimeSpan Duration, string Reason)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Gets or sets the result of the next accepted call; starts as <c>Started</c>.</summary>
    public SessionStartResult NextResult { get; set; } = new(true, null);

    /// <inheritdoc/>
    public ValueTask<SessionStartResult> StartAsync(TimeSpan duration, string reason, CancellationToken cancellationToken = default)
    {
        if (!_plugin.Has(PluginCapability.SessionStart))
        {
            throw new PluginCapabilityException(PluginCapability.SessionStart);
        }

        ArgumentNullException.ThrowIfNull(reason);
        if (duration < SessionLaunchLimits.MinDuration || duration > SessionLaunchLimits.MaxDuration)
        {
            return ValueTask.FromResult(new SessionStartResult(false, "The session length is out of range."));
        }

        lock (_gate)
        {
            _calls.Add((duration, reason));
        }

        return ValueTask.FromResult(NextResult);
    }
}

/// <summary>Recording <see cref="IPluginNotifier"/>.</summary>
public sealed class FakePluginNotifier : IPluginNotifier
{
    private readonly PluginInfo _plugin;
    private readonly Lock _gate = new();
    private readonly List<string> _messages = [];

    internal FakePluginNotifier(PluginInfo plugin) => _plugin = plugin;

    /// <summary>Gets the toasts shown, in order.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> ShowAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!_plugin.Has(PluginCapability.Notify))
        {
            throw new PluginCapabilityException(PluginCapability.Notify);
        }

        ArgumentNullException.ThrowIfNull(message);
        string text = message.Length <= PluginNotifierLimits.MaxMessageLength ? message : message[..PluginNotifierLimits.MaxMessageLength];
        lock (_gate)
        {
            _messages.Add(text);
        }

        return ValueTask.FromResult(true);
    }
}
