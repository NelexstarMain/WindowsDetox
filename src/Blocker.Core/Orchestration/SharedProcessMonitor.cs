using Blocker.Core.Ports;

namespace Blocker.Core.Orchestration;

/// <summary>
/// One process monitor for two users, the session coordinator and the always-on app rules, each of which wants it running and restarted when it
/// dies. <see cref="StartAsync"/> starts the monitor only when it is not running (never started, or its delivery ended), and calls are serialized,
/// so two callers can never both start the same ETW session. Everything else passes straight through.
/// </summary>
/// <param name="inner">The real monitor.</param>
internal sealed class SharedProcessMonitor(IProcessMonitor inner) : IProcessMonitor
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _started;

    /// <inheritdoc/>
    public event EventHandler<ProcessStartedEventArgs>? ProcessStarted
    {
        add => inner.ProcessStarted += value;
        remove => inner.ProcessStarted -= value;
    }

    /// <inheritdoc/>
    public event EventHandler<ProcessExitedEventArgs>? ProcessExited
    {
        add => inner.ProcessExited += value;
        remove => inner.ProcessExited -= value;
    }

    /// <inheritdoc/>
    public Task Completion => inner.Completion;

    /// <inheritdoc/>
    public Exception? Fault => inner.Fault;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started && !inner.Completion.IsCompleted)
            {
                return;
            }

            await inner.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _ = _startGate.Release();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProcessInfo> EnumerateRunning() => inner.EnumerateRunning();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
