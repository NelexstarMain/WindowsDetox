using Blocker.Sdk;

namespace Blocker.Core.Resilience;

/// <summary>
/// Guards calls into one plugin: enforces a time budget and moves the plugin to <see cref="BreakerState.Faulted"/>
/// after repeated failures. There is no automatic recovery; <see cref="Reset"/> is an explicit admin action.
/// </summary>
/// <param name="pluginId">Guarded plugin.</param>
/// <param name="timeProvider">Clock for budgets and the failure window.</param>
/// <param name="options">Thresholds; defaults when <see langword="null"/>.</param>
public sealed class PluginCircuitBreaker(PluginId pluginId, TimeProvider timeProvider, CircuitBreakerOptions? options = null)
{
    private readonly CircuitBreakerOptions _options = options ?? new CircuitBreakerOptions();
    private readonly Lock _gate = new();
    private readonly Queue<long> _failureTimes = new();
    private int _consecutive;
    private BreakerState _state;
    private string? _faultReason;

    /// <summary>Raised (outside any lock) when the state changes.</summary>
    public event EventHandler<BreakerStateChangedEventArgs>? StateChanged;

    /// <summary>Gets the guarded plugin.</summary>
    public PluginId PluginId { get; } = pluginId;

    /// <summary>Gets the current state.</summary>
    public BreakerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets why the breaker faulted, or <see langword="null"/> when closed.</summary>
    public string? FaultReason
    {
        get
        {
            lock (_gate)
            {
                return _faultReason;
            }
        }
    }

    /// <summary>Closes the breaker and forgets all recorded failures.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _consecutive = 0;
            _failureTimes.Clear();
            if (_state == BreakerState.Closed)
            {
                return;
            }

            _state = BreakerState.Closed;
            _faultReason = null;
        }

        StateChanged?.Invoke(this, new BreakerStateChangedEventArgs(BreakerState.Closed, null));
    }

    /// <summary>Runs <paramref name="call"/> within the budget; on failure, timeout or rejection returns <paramref name="fallback"/>.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="call">The plugin call; receives a token cancelled when the budget elapses.</param>
    /// <param name="fallback">Value returned when the call does not succeed.</param>
    /// <param name="cancellationToken">Caller cancellation; propagates as <see cref="OperationCanceledException"/> and is not a failure.</param>
    /// <returns>The outcome with the call's value or the fallback.</returns>
    public async ValueTask<BreakerResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> call,
        T fallback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();
        if (State == BreakerState.Faulted)
        {
            return new BreakerResult<T>(fallback, BreakerOutcome.Rejected);
        }

        using CancellationTokenSource budget = new(_options.Budget, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        Task<T>? task = null;
        try
        {
            task = call(linked.Token).AsTask();
            T value = await task.WaitAsync(_options.Budget, timeProvider, cancellationToken).ConfigureAwait(false);
            RecordSuccess();
            return new BreakerResult<T>(value, BreakerOutcome.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Abandon(task, linked);
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && budget.IsCancellationRequested))
        {
            Abandon(task, linked);
            RecordFailure("timed out");
            return new BreakerResult<T>(fallback, BreakerOutcome.Timeout, ex);
        }
        catch (Exception ex)
        {
            Abandon(task, linked);
            RecordFailure("threw " + ex.GetType().Name);
            return new BreakerResult<T>(fallback, BreakerOutcome.Exception, ex);
        }
    }

    /// <summary>Runs <paramref name="call"/> within the budget. <see cref="BreakerResult{T}.Value"/> is whether it succeeded.</summary>
    /// <param name="call">The plugin call.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>The outcome.</returns>
    public async ValueTask<BreakerResult<bool>> ExecuteAsync(Func<CancellationToken, ValueTask> call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        return await ExecuteAsync(
            async ct =>
            {
                await call(ct).ConfigureAwait(false);
                return true;
            },
            false,
            cancellationToken).ConfigureAwait(false);
    }

    // Cancels the call and observes any late exception so it never surfaces as an unobserved task exception.
    private static void Abandon(Task? task, CancellationTokenSource linked)
    {
        linked.Cancel();
        task?.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutive = 0;
        }
    }

    private void RecordFailure(string what)
    {
        BreakerStateChangedEventArgs? change = null;
        lock (_gate)
        {
            if (_state == BreakerState.Faulted)
            {
                return;
            }

            long now = timeProvider.GetTimestamp();
            while (_failureTimes.TryPeek(out long oldest) && timeProvider.GetElapsedTime(oldest, now) >= _options.Window)
            {
                _failureTimes.Dequeue();
            }

            _failureTimes.Enqueue(now);
            _consecutive++;
            string? reason = null;
            if (_consecutive >= _options.ConsecutiveFailuresToTrip)
            {
                reason = $"{_consecutive} consecutive failures (last {what}).";
            }
            else if (_failureTimes.Count >= _options.WindowFailuresToTrip)
            {
                reason = $"{_failureTimes.Count} failures within {_options.Window.TotalSeconds:0} s (last {what}).";
            }

            if (reason is not null)
            {
                _state = BreakerState.Faulted;
                _faultReason = reason;
                change = new BreakerStateChangedEventArgs(BreakerState.Faulted, reason);
            }
        }

        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
        }
    }
}
