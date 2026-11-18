namespace Blocker.Core.Tests.TestSupport;

/// <summary>Simulates a callback that never completes on its own.</summary>
internal static class Hang
{
    /// <summary>Completes (as cancelled) only when <paramref name="cancellationToken"/> is signalled.</summary>
    public static Task UntilCancelled(CancellationToken cancellationToken) =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
}
