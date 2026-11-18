using Blocker.Contracts;

namespace Blocker.UI.Services;

/// <summary>Classifies failures of calls to the Blocker service and words them for the user.</summary>
internal static class ServiceFault
{
    /// <summary>Returns whether <paramref name="exception"/> is an expected failure of a service call (as opposed to a bug).</summary>
    /// <param name="exception">Exception thrown by a call.</param>
    /// <returns><see langword="true"/> for IPC errors, a broken or unreachable pipe and timeouts.</returns>
    public static bool IsExpected(Exception exception) =>
        exception is IpcException or IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException;

    /// <summary>Words a failure for a snackbar or an inline message.</summary>
    /// <param name="exception">Exception thrown by a call.</param>
    /// <returns>A short, user-readable sentence.</returns>
    public static string Describe(Exception exception) => exception switch
    {
        IpcException { Code: IpcErrorCode.Unauthorized } => "This needs administrator rights. Start Blocker as administrator and try again.",
        IpcException { Code: IpcErrorCode.NotAllowedDuringSession } => "This is locked while a session is running.",
        IpcException { Code: IpcErrorCode.ValidationFailed } ex => ex.Message,
        IpcException { Code: IpcErrorCode.RateLimited } => "Too many requests. Wait a moment and try again.",
        IpcException { Code: IpcErrorCode.PayloadTooLarge } => "The file is too large to send to the service.",
        IpcException => "The Blocker service could not complete the request.",
        _ => "The Blocker service is not reachable.",
    };
}
