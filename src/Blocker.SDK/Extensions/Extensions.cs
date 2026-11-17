using Blocker.Sdk.Model;
using Blocker.Sdk.Session;

namespace Blocker.Sdk.Extensions;

/// <summary>
/// Marker for every extension a plugin can register through <see cref="IExtensionRegistry"/>. One object may
/// implement several extension interfaces; the host enforces the capability of each one.
/// </summary>
public interface IPluginExtension
{
}

/// <summary>Registers plugin extensions with the host.</summary>
public interface IExtensionRegistry
{
    /// <summary>Registers an extension object.</summary>
    /// <param name="extension">Object implementing one or more extension interfaces.</param>
    /// <returns>A registration; dispose it to remove the extension.</returns>
    /// <exception cref="PluginCapabilityException">A capability required by one of the implemented interfaces is missing.</exception>
    /// <exception cref="ArgumentException"><paramref name="extension"/> implements no known extension interface.</exception>
    IDisposable Add(IPluginExtension extension);
}

/// <summary>Outcome of an interceptor.</summary>
public enum InterceptVerdict
{
    /// <summary>No opinion; the core allowlist decides.</summary>
    Abstain,

    /// <summary>Block the process or window.</summary>
    Block,
}

/// <summary>
/// Decision returned by <see cref="IProcessInterceptor"/> and <see cref="IWindowInterceptor"/>.
/// Plugins can only make enforcement stricter: there is intentionally no "allow" verdict, and critical
/// Windows processes are never blocked regardless of the verdict.
/// </summary>
public readonly record struct InterceptDecision
{
    private InterceptDecision(InterceptVerdict verdict, string? reason)
    {
        Verdict = verdict;
        Reason = reason;
    }

    /// <summary>Gets the verdict.</summary>
    public InterceptVerdict Verdict { get; }

    /// <summary>Gets the user-visible reason for a block; <see langword="null"/> when abstaining.</summary>
    public string? Reason { get; }

    /// <summary>Gets the decision that leaves the verdict to the core.</summary>
    public static InterceptDecision Abstain { get; } = new(InterceptVerdict.Abstain, null);

    /// <summary>Creates a blocking decision.</summary>
    /// <param name="reason">Short, user-visible reason (shown on the block screen).</param>
    /// <returns>The decision.</returns>
    public static InterceptDecision Block(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new InterceptDecision(InterceptVerdict.Block, reason);
    }
}

/// <summary>
/// Requires <see cref="PluginCapability.ProcessInterception"/>. Consulted for every new process in the
/// interactive session, both during and outside detox sessions.
/// </summary>
public interface IProcessInterceptor : IPluginExtension
{
    /// <summary>Decides whether a process may keep running.</summary>
    /// <param name="process">The new process.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>The decision.</returns>
    ValueTask<InterceptDecision> OnProcessStartingAsync(ProcessDescriptor process, CancellationToken cancellationToken);
}

/// <summary>
/// Requires <see cref="PluginCapability.WindowInterception"/>. Consulted when a window becomes the foreground window.
/// </summary>
public interface IWindowInterceptor : IPluginExtension
{
    /// <summary>Decides whether the owning application may stay in the foreground.</summary>
    /// <param name="window">The newly activated window.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>The decision; <see cref="InterceptVerdict.Block"/> closes the app and shows the block screen.</returns>
    ValueTask<InterceptDecision> OnWindowActivatedAsync(WindowDescriptor window, CancellationToken cancellationToken);
}

/// <summary>Information passed to <see cref="ITickListener.OnTickAsync"/>.</summary>
/// <param name="Now">Verified current time.</param>
/// <param name="Session">Current session snapshot.</param>
public sealed record TickContext(DateTimeOffset Now, SessionSnapshot Session);

/// <summary>Requires <see cref="PluginCapability.Tick"/>. Receives periodic ticks.</summary>
public interface ITickListener : IPluginExtension
{
    /// <summary>Gets the desired interval. Values below one second are raised to one second.</summary>
    TimeSpan Interval { get; }

    /// <summary>Called once per interval.</summary>
    /// <param name="context">Tick information.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>A task that completes when the tick is handled.</returns>
    ValueTask OnTickAsync(TickContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Voluntary user actions a <see cref="ILockPolicyContributor"/> may veto. The natural expiry of a session is
/// deliberately not listed: no plugin can extend a session or prevent its rollback.
/// </summary>
public enum LockAction
{
    /// <summary>Uninstalling Blocker.</summary>
    UninstallApplication,

    /// <summary>Removing a plugin.</summary>
    RemovePlugin,

    /// <summary>Disabling a plugin.</summary>
    DisablePlugin,

    /// <summary>Changing protection settings while idle.</summary>
    ChangeProtectionSettings,

    /// <summary>Starting an emergency pass.</summary>
    UseEmergencyPass,
}

/// <summary>Result of a lock-policy evaluation.</summary>
public readonly record struct LockVote
{
    private LockVote(bool isVeto, string? reason)
    {
        IsVeto = isVeto;
        Reason = reason;
    }

    /// <summary>Gets whether the action is vetoed.</summary>
    public bool IsVeto { get; }

    /// <summary>Gets the user-visible reason for a veto.</summary>
    public string? Reason { get; }

    /// <summary>Gets a vote that does not object to the action.</summary>
    public static LockVote Allow { get; } = new(false, null);

    /// <summary>Creates a veto.</summary>
    /// <param name="reason">Short, user-visible reason.</param>
    /// <returns>The vote.</returns>
    public static LockVote Veto(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new LockVote(true, reason);
    }
}

/// <summary>
/// Requires <see cref="PluginCapability.LockPolicy"/>. Can veto voluntary actions. Faulted or timed-out
/// contributors are treated as <see cref="LockVote.Allow"/> so a broken plugin can never lock the user in.
/// </summary>
public interface ILockPolicyContributor : IPluginExtension
{
    /// <summary>Evaluates whether <paramref name="action"/> may proceed.</summary>
    /// <param name="action">The requested action.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>The vote.</returns>
    ValueTask<LockVote> EvaluateAsync(LockAction action, CancellationToken cancellationToken);
}
