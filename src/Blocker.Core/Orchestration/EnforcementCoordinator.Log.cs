using Blocker.Core.Journal;
using Blocker.Core.Policy;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Orchestration;

public sealed partial class EnforcementCoordinator
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Starting the enforced session failed; rolling back.")]
    private static partial void LogSessionStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bringing up protections failed.")]
    private static partial void LogBringUpFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Step} failed.")]
    private static partial void LogStepFailed(ILogger logger, Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tick failed; the loop continues.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling process {ProcessId} failed.")]
    private static partial void LogProcessFailed(ILogger logger, Exception exception, int processId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "A session is active but its saved allowlist is missing or unreadable; blocking everything outside the system baseline.")]
    private static partial void LogPolicyMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovering the persisted session (phase {Phase}).")]
    private static partial void LogRecovering(ILogger logger, SessionPhase phase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No session is running but the journal has pending changes; rolling back.")]
    private static partial void LogRollingBackLeftovers(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rollback ({Mode}) is incomplete: {Restored} restored, {Failures} failed.")]
    private static partial void LogRollbackIncomplete(ILogger logger, RollbackMode mode, int restored, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "Emergency pass: protections suspended ({Restored} changes rolled back).")]
    private static partial void LogPassSuspended(ILogger logger, int restored);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session torn down ({Restored} changes rolled back).")]
    private static partial void LogTornDown(ILogger logger, int restored);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The process queue is full; the monitor thread blocks until the consumer catches up.")]
    private static partial void LogQueueFull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Process {ProcessId} claims parent {ParentProcessId} but was created by process {CreatorProcessId}; inheriting from the creator only.")]
    private static partial void LogParentMismatch(ILogger logger, int processId, int parentProcessId, int creatorProcessId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blocked {Image} (pid {ProcessId}, {Reason}, terminated: {Terminated}).")]
    private static partial void LogBlocked(ILogger logger, string image, int processId, VerdictReason reason, bool terminated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No user is logged on; the block screen for a blocked process was not shown.")]
    private static partial void LogNoUserSession(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Plugin interceptors exceeded their budget for process {ProcessId}; ignored.")]
    private static partial void LogInterceptorTimedOut(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graceful close of process {ProcessId} timed out; terminating.")]
    private static partial void LogGracefulCloseTimedOut(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Loop} stopped.")]
    private static partial void LogLoopStopped(ILogger logger, string loop);
}
