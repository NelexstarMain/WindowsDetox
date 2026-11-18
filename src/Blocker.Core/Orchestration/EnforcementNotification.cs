namespace Blocker.Core.Orchestration;

/// <summary>What an <see cref="EnforcementNotification"/> is about.</summary>
public enum EnforcementNotificationKind
{
    /// <summary>The emergency pass ends in about a minute (toast).</summary>
    PassEndingSoon,

    /// <summary>The emergency pass ended and enforcement resumed.</summary>
    PassEnded,

    /// <summary>The session ended and everything was rolled back.</summary>
    SessionEnded,

    /// <summary>Some protection could not be applied or restored; the service keeps retrying or needs attention.</summary>
    ProtectionDegraded,
}

/// <summary>Message for the user-session agent.</summary>
/// <param name="Kind">Category.</param>
/// <param name="Text">Short English text.</param>
public sealed record EnforcementNotification(EnforcementNotificationKind Kind, string Text);

/// <summary>Outcome of <see cref="EnforcementCoordinator.StartSessionAsync"/>.</summary>
/// <param name="Started">The session runs and is fully enforced.</param>
/// <param name="SessionId">Session id when started.</param>
/// <param name="BreakGlassCode">One-time recovery code, shown once; null unless started.</param>
/// <param name="Errors">Why the start was refused or rolled back.</param>
public sealed record StartEnforcedSessionResult(bool Started, Guid? SessionId, string? BreakGlassCode, IReadOnlyList<string> Errors)
{
    internal static StartEnforcedSessionResult Failed(IReadOnlyList<string> errors) => new(false, null, null, errors);
}
