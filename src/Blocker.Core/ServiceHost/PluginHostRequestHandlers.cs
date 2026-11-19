using System.Globalization;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Sdk;
using Blocker.Sdk.Notifications;
using Blocker.Sdk.Sessions;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.ServiceHost;

/// <summary>An installed, enabled plugin and what its manifest grants.</summary>
/// <param name="Name">Display name from the manifest.</param>
/// <param name="Capabilities">Manifest capability names.</param>
internal sealed record PluginGrant(string Name, IReadOnlySet<string> Capabilities);

/// <summary>Everything <see cref="PluginHostRequestHandlers"/> needs from the service.</summary>
/// <param name="ResolvePluginAsync">Finds an installed and enabled plugin by id; <see langword="null"/> otherwise.</param>
/// <param name="Agent">The user-session agent (toasts).</param>
/// <param name="StartSessionAsync">Starts a session with the saved allowlist and the default pass settings, for a length in minutes.</param>
/// <param name="Time">Clock for the rate limits.</param>
internal sealed record PluginHostRequestDependencies(
    Func<string, CancellationToken, ValueTask<PluginGrant?>> ResolvePluginAsync,
    IAgentToasts Agent,
    Func<int, CancellationToken, ValueTask<(bool Started, string? Error)>> StartSessionAsync,
    TimeProvider Time);

/// <summary>
/// The service's side of the capabilities that reach beyond the plugin host's sandbox: <c>session.start</c> and <c>notify</c>. Only the live plugin host may call (checked per request), the named plugin must be installed, enabled and declare the
/// capability in its manifest, and every effect is bounded: session starts by length, spacing and a daily total,
/// toasts by an hourly count. Sessions started here never reveal a break-glass code to the plugin.
/// </summary>
internal sealed partial class PluginHostRequestHandlers(PluginHostRequestDependencies deps, ILogger<PluginHostRequestHandlers> logger)
{
    /// <summary>Smallest gap between two plugin-started sessions of one plugin.</summary>
    public static readonly TimeSpan MinSessionGap = TimeSpan.FromMinutes(2);

    /// <summary>Most minutes of sessions one plugin may start within 24 hours.</summary>
    public const int MaxSessionMinutesPerDay = 16 * 60;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<(DateTimeOffset At, int Minutes)>> _sessionStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _toasts = new(StringComparer.Ordinal);

    /// <summary>Registers the handlers on the plugin-host pipe.</summary>
    /// <param name="server">The plugin-host pipe server.</param>
    /// <param name="isCurrentHost">Whether a peer is the plugin host the supervisor launched and registered (not a throw-away evaluation host).</param>
    /// <returns>The registrations.</returns>
    public IReadOnlyList<IDisposable> RegisterAll(IpcServer server, Func<IpcPeer, bool> isCurrentHost)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(isCurrentHost);
        return
        [
            server.Register<PluginStartSessionRequest, PluginStartSessionResponse>(IpcMethods.PluginHostStartSession, (c, r, ct) => Guard(c, isCurrentHost, () => StartSessionAsync(r, ct))),
            server.Register<PluginNotifyRequest, PluginNotifyResponse>(IpcMethods.PluginHostNotify, (c, r, ct) => Guard(c, isCurrentHost, () => NotifyAsync(r, ct))),
        ];
    }

    /// <summary>Handles <see cref="IpcMethods.PluginHostStartSession"/>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async ValueTask<PluginStartSessionResponse> StartSessionAsync(PluginStartSessionRequest request, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(request.PluginId, "session.start", cancellationToken).ConfigureAwait(false) is not { } id
            || await deps.ResolvePluginAsync(request.PluginId, cancellationToken).ConfigureAwait(false) is not { } grant)
        {
            return new PluginStartSessionResponse(false, "The plugin may not start sessions.");
        }

        TimeSpan duration = TimeSpan.FromMinutes(request.DurationMinutes);
        if (duration < SessionLaunchLimits.MinDuration || duration > SessionLaunchLimits.MaxDuration)
        {
            return new PluginStartSessionResponse(false, "The session length is out of range.");
        }

        if (!TryReserveSession(id, request.DurationMinutes, out string? limit))
        {
            LogSessionLimited(logger, id);
            return new PluginStartSessionResponse(false, limit);
        }

        (bool started, string? error) = await deps.StartSessionAsync(request.DurationMinutes, cancellationToken).ConfigureAwait(false);
        if (!started)
        {
            ReleaseSession(id);
            return new PluginStartSessionResponse(false, error ?? "The session was not started.");
        }

        LogSessionStartedByPlugin(logger, id, request.DurationMinutes);
        string reason = Printable(request.Reason, SessionLaunchLimits.MaxReasonLength);
        string length = FormatLength(duration);
        await deps.Agent.NotifyAsync(
            new NotificationDto(
                "Focus session started",
                $"{Printable(grant.Name, 40)} started a {length} session{(reason.Length > 0 ? ": " + reason : string.Empty)}. It cannot be shortened; emergency passes still work.",
                NotificationLevel.Info),
            cancellationToken).ConfigureAwait(false);
        return new PluginStartSessionResponse(true, null);
    }

    /// <summary>Handles <see cref="IpcMethods.PluginHostNotify"/>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async ValueTask<PluginNotifyResponse> NotifyAsync(PluginNotifyRequest request, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(request.PluginId, "notify", cancellationToken).ConfigureAwait(false) is not { } id
            || await deps.ResolvePluginAsync(request.PluginId, cancellationToken).ConfigureAwait(false) is not { } grant)
        {
            return new PluginNotifyResponse(false);
        }

        string text = Printable(request.Message, PluginNotifierLimits.MaxMessageLength);
        if (text.Length == 0 || !TryTake(_toasts, id, PluginNotifierLimits.MaxPerHour))
        {
            return new PluginNotifyResponse(false);
        }

        if (!deps.Agent.IsConnected)
        {
            return new PluginNotifyResponse(false);
        }

        await deps.Agent.NotifyAsync(new NotificationDto(Printable(grant.Name, 40), text, NotificationLevel.Info), cancellationToken).ConfigureAwait(false);
        return new PluginNotifyResponse(true);
    }

    private static async ValueTask<TResponse> Guard<TResponse>(IpcRequestContext context, Func<IpcPeer, bool> isCurrentHost, Func<ValueTask<TResponse>> call)
    {
        if (!isCurrentHost(context.Peer))
        {
            throw new IpcException(IpcErrorCode.Unauthorized, "Not authorized.");
        }

        return await call().ConfigureAwait(false);
    }

    private static string Printable(string? text, int maxLength)
    {
        string clean = string.Concat((text ?? string.Empty).Select(static c => char.IsControl(c) ? '?' : c)).Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static string FormatLength(TimeSpan duration) =>
        duration.TotalMinutes % 60 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalHours:0} h")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMinutes:0} min");

    private async ValueTask<PluginId?> ResolveAsync(string pluginId, string capability, CancellationToken cancellationToken)
    {
        if (!PluginId.TryCreate(pluginId, out PluginId id))
        {
            return null;
        }

        PluginGrant? grant = await deps.ResolvePluginAsync(id.Value, cancellationToken).ConfigureAwait(false);
        return grant is not null && grant.Capabilities.Contains(capability) ? id : null;
    }

    private bool TryReserveSession(PluginId id, int minutes, out string? error)
    {
        DateTimeOffset now = deps.Time.GetUtcNow();
        lock (_gate)
        {
            if (!_sessionStarts.TryGetValue(id.Value, out List<(DateTimeOffset At, int Minutes)>? starts))
            {
                starts = [];
                _sessionStarts[id.Value] = starts;
            }

            _ = starts.RemoveAll(s => now - s.At >= TimeSpan.FromHours(24));
            if (starts.Count > 0 && now - starts[^1].At < MinSessionGap)
            {
                error = "A session was started a moment ago.";
                return false;
            }

            if (starts.Sum(s => s.Minutes) + minutes > MaxSessionMinutesPerDay)
            {
                error = "This plugin has already started the most session time allowed per day.";
                return false;
            }

            starts.Add((now, minutes));
            error = null;
            return true;
        }
    }

    private void ReleaseSession(PluginId id)
    {
        lock (_gate)
        {
            if (_sessionStarts.TryGetValue(id.Value, out List<(DateTimeOffset At, int Minutes)>? starts) && starts.Count > 0)
            {
                starts.RemoveAt(starts.Count - 1);
            }
        }
    }

    private bool TryTake(Dictionary<string, Queue<DateTimeOffset>> windows, PluginId id, int limit)
    {
        DateTimeOffset now = deps.Time.GetUtcNow();
        lock (_gate)
        {
            if (!windows.TryGetValue(id.Value, out Queue<DateTimeOffset>? queue))
            {
                queue = new Queue<DateTimeOffset>();
                windows[id.Value] = queue;
            }

            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromHours(1))
            {
                _ = queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} started a {Minutes} minute session.")]
    private static partial void LogSessionStartedByPlugin(ILogger logger, PluginId pluginId, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} was refused a session start by its limits.")]
    private static partial void LogSessionLimited(ILogger logger, PluginId pluginId);
}
