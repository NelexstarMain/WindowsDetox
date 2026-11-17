using Blocker.Sdk.Model;

namespace Blocker.Sdk.Events;

/// <summary>
/// Marker for events delivered through <see cref="IEventBus"/>. Core events are published only by the host;
/// plugins can publish <see cref="CustomPluginEvent"/> exclusively.
/// </summary>
public interface IBlockerEvent
{
    /// <summary>Gets the verified time at which the event occurred.</summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Why a session ended.</summary>
public enum SessionEndReason
{
    /// <summary>The planned duration elapsed.</summary>
    Expired,

    /// <summary>The session was ended with the one-time break-glass recovery code.</summary>
    BreakGlass,

    /// <summary>The session was aborted in developer mode (never in release builds).</summary>
    DeveloperAbort,
}

/// <summary>Why a process was blocked.</summary>
public enum BlockReason
{
    /// <summary>The executable is not on the session allowlist and was not started by an allowlisted app.</summary>
    NotAllowlisted,

    /// <summary>The executable is a known bypass tool (Task Manager, Registry Editor, shells, ...).</summary>
    BypassTool,

    /// <summary>A plugin interceptor vetoed the process.</summary>
    PluginPolicy,
}

/// <summary>Requires <see cref="PluginCapability.SessionEvents"/>. A detox session started.</summary>
/// <param name="SessionId">Id of the new session.</param>
/// <param name="PlannedDuration">Planned session length.</param>
/// <param name="OccurredAt">Verified start time.</param>
public sealed record SessionStarted(Guid SessionId, TimeSpan PlannedDuration, DateTimeOffset OccurredAt) : IBlockerEvent;

/// <summary>Requires <see cref="PluginCapability.SessionEvents"/>. A detox session ended and was rolled back.</summary>
/// <param name="SessionId">Id of the session.</param>
/// <param name="Reason">Why it ended.</param>
/// <param name="OccurredAt">Verified end time.</param>
public sealed record SessionEnded(Guid SessionId, SessionEndReason Reason, DateTimeOffset OccurredAt) : IBlockerEvent;

/// <summary>Requires <see cref="PluginCapability.SessionEvents"/>. Enforcement was suspended by an emergency pass.</summary>
/// <param name="SessionId">Id of the session.</param>
/// <param name="Duration">Length of the pass.</param>
/// <param name="PassesLeftToday">Passes left after this one.</param>
/// <param name="OccurredAt">Verified time the pass began.</param>
public sealed record EmergencyPassStarted(Guid SessionId, TimeSpan Duration, int PassesLeftToday, DateTimeOffset OccurredAt) : IBlockerEvent;

/// <summary>Requires <see cref="PluginCapability.SessionEvents"/>. An emergency pass ended and enforcement resumed.</summary>
/// <param name="SessionId">Id of the session.</param>
/// <param name="OccurredAt">Verified time enforcement resumed.</param>
public sealed record EmergencyPassEnded(Guid SessionId, DateTimeOffset OccurredAt) : IBlockerEvent;

/// <summary>Requires <see cref="PluginCapability.ProcessEvents"/>. A process was prevented from running.</summary>
/// <param name="Process">The blocked process.</param>
/// <param name="Reason">Why it was blocked.</param>
/// <param name="BlockedByPlugin">Id of the plugin whose interceptor vetoed it, when <paramref name="Reason"/> is <see cref="BlockReason.PluginPolicy"/>.</param>
/// <param name="OccurredAt">Verified time of the block.</param>
public sealed record ProcessBlocked(ProcessDescriptor Process, BlockReason Reason, string? BlockedByPlugin, DateTimeOffset OccurredAt) : IBlockerEvent;

/// <summary>Requires <see cref="PluginCapability.WindowEvents"/>. The foreground window changed.</summary>
/// <param name="Window">The new foreground window.</param>
/// <param name="OccurredAt">Verified time of the change.</param>
public sealed record ActiveWindowChanged(WindowDescriptor Window, DateTimeOffset OccurredAt) : IBlockerEvent;

/// <summary>Requires <see cref="PluginCapability.IdleEvents"/>. The user became idle or active again.</summary>
/// <param name="IsIdle"><see langword="true"/> when the user just became idle.</param>
/// <param name="IdleDuration">How long no input has been received.</param>
/// <param name="OccurredAt">Verified time of the change.</param>
public sealed record UserIdleChanged(bool IsIdle, TimeSpan IdleDuration, DateTimeOffset OccurredAt) : IBlockerEvent;

/// <summary>
/// Requires <see cref="PluginCapability.CustomEvents"/>. An event published by a plugin. The topic is always
/// <c>{sourcePluginId}/{eventName}</c>, so a plugin cannot impersonate another plugin or the core.
/// </summary>
/// <param name="Topic">Fully-qualified topic.</param>
/// <param name="SourcePluginId">Id of the publishing plugin, set by the host.</param>
/// <param name="PayloadJson">JSON payload, at most <see cref="BlockerSdk.MaxPayloadBytes"/> bytes.</param>
/// <param name="OccurredAt">Verified publish time.</param>
public sealed record CustomPluginEvent(string Topic, string SourcePluginId, string PayloadJson, DateTimeOffset OccurredAt) : IBlockerEvent;
