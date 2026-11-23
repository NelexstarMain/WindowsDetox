using Blocker.Core.Journal;

namespace Blocker.Core.Orchestration;

public sealed partial class EnforcementCoordinator
{
    internal static readonly TimeSpan CleanupRetryInitial = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupRetryMax = TimeSpan.FromMinutes(5);

    private readonly ProgressCounter _cleanupAttempts = new();
    private CancellationTokenSource? _cleanupStop; // guarded by _gate
    private Task _cleanupRetry = Task.CompletedTask;
    private volatile bool _cleanupPending;

    /// <summary>
    /// Whether a rollback after the session ended is still incomplete and being retried in the background. While it is, some restrictions
    /// (IFEO redirects, a locked service DACL, deny ACEs, the browser PAC) may still be in place.
    /// </summary>
    public bool CleanupPending => _cleanupPending;

    internal Task CleanupRetryTask => _cleanupRetry;

    internal long CleanupAttempts => _cleanupAttempts.Value;

    internal Task WaitForCleanupAttemptsAsync(long count, CancellationToken cancellationToken) => _cleanupAttempts.WaitAsync(count, cancellationToken);

    internal static TimeSpan NextCleanupDelay(TimeSpan current) => TimeSpan.FromTicks(Math.Min(current.Ticks * 2, CleanupRetryMax.Ticks));

    /// <summary>
    /// Keeps retrying an incomplete rollback while no session runs: without it, a transient failure at session end (an antivirus scan holding a
    /// key) would leave the machine restricted until the service restarts. Caller holds the gate.
    /// </summary>
    private void StartCleanupRetry()
    {
        StopCleanupRetry();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var stop = new CancellationTokenSource(); // never disposed: a late Cancel() from a new session or shutdown must stay safe
        _cleanupStop = stop;
        _cleanupPending = true;
        _cleanupRetry = RetryCleanupAsync(stop.Token); // registers its first delay before returning
    }

    /// <summary>A new session (or service shutdown) takes over: the next bring-up rolls the journal back itself. Caller holds the gate.</summary>
    private void StopCleanupRetry()
    {
        if (Interlocked.Exchange(ref _cleanupStop, null) is { } stop)
        {
            stop.Cancel();
        }

        _cleanupPending = false;
    }

    private async Task RetryCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan delay = CleanupRetryInitial;
            while (true)
            {
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
                if (await TryCleanupOnceAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                delay = NextCleanupDelay(delay);
            }
        }
        catch (OperationCanceledException)
        {
            LogCleanupRetryStopped(logger); // superseded by a new session, or the service is stopping
        }
    }

    /// <summary>One retry; true when nothing is left to retry (done, or a session runs and its bring-up owns the journal).</summary>
    private async Task<bool> TryCleanupOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_runtime is not null)
            {
                return true;
            }

            RollbackReport? report = await RollbackAllGuardedAsync(announce: false).ConfigureAwait(false);
            bool done = report is { Complete: true };
            if (done)
            {
                _cleanupPending = false;
                LogCleanupCompleted(logger);
            }

            _cleanupAttempts.Increment();
            return done;
        }
        finally
        {
            _ = _gate.Release();
        }
    }
}
