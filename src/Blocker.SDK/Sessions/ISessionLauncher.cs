namespace Blocker.Sdk.Sessions;

/// <summary>
/// Requires <see cref="PluginCapability.SessionStart"/>. Starts a detox session on behalf of the user, for example at a time the
/// user planned in advance. The session uses the allowlist the user saved on the Allowlist page and the default emergency-pass
/// settings; the plugin cannot choose or change either. Like every other session it cannot be shortened or cancelled once it
/// started, and it has no break-glass code (the code is never handed to a plugin), so only emergency passes can interrupt it.
/// </summary>
public interface ISessionLauncher
{
    /// <summary>Starts a session now. Fails (without side effects) while a session is already running.</summary>
    /// <param name="duration">Session length, from <see cref="SessionLaunchLimits.MinDuration"/> to <see cref="SessionLaunchLimits.MaxDuration"/>.</param>
    /// <param name="reason">Short text for the notice the user sees (for example the title of the calendar event).</param>
    /// <param name="cancellationToken">Cancels the call. A cancelled call may still have started the session; check the session state.</param>
    /// <returns>The outcome; never throws for a refusal by the service.</returns>
    ValueTask<SessionStartResult> StartAsync(TimeSpan duration, string reason, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="ISessionLauncher.StartAsync"/>.</summary>
/// <param name="Started">Whether the session started.</param>
/// <param name="Error">Plain-text reason when it did not (a session is running, the allowlist is empty, the service is unavailable, ...).</param>
public sealed record SessionStartResult(bool Started, string? Error);

/// <summary>Limits of <see cref="ISessionLauncher"/>, identical in the service and in the testing kit.</summary>
public static class SessionLaunchLimits
{
    /// <summary>Shortest session a plugin may start (the same minimum as the Setup page).</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);

    /// <summary>Longest session a plugin may start; the Setup page allows more, but a session nobody is present for must stay bounded.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);

    /// <summary>Longest reason text.</summary>
    public const int MaxReasonLength = 80;
}
