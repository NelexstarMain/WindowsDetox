namespace Blocker.Core.Orchestration;

/// <summary>Counts queued work items and exposes a task that completes while none are pending (lets tests await the single consumer without sleeping).</summary>
internal sealed class WorkTracker
{
    private readonly Lock _gate = new();
    private int _pending;
    private TaskCompletionSource _idle = CompletedSource();

    /// <summary>Gets a task that is complete while no work is pending.</summary>
    public Task WhenIdle
    {
        get
        {
            lock (_gate)
            {
                return _idle.Task;
            }
        }
    }

    /// <summary>Registers one item.</summary>
    public void Add()
    {
        lock (_gate)
        {
            if (_pending++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>Marks one item as handled.</summary>
    public void Done()
    {
        lock (_gate)
        {
            if (_pending > 0 && --_pending == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    /// <summary>Forgets all pending items (the consumer stopped).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pending = 0;
            _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

/// <summary>Monotonic counter with awaitable thresholds.</summary>
internal sealed class ProgressCounter
{
    private readonly Lock _gate = new();
    private readonly List<(long Target, TaskCompletionSource Source)> _waiters = [];
    private long _value;

    /// <summary>Gets the current value.</summary>
    public long Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>Adds one and releases satisfied waiters.</summary>
    public void Increment()
    {
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            _value++;
            _waiters.RemoveAll(w =>
            {
                if (w.Target > _value)
                {
                    return false;
                }

                ready.Add(w.Source);
                return true;
            });
        }

        ready.ForEach(static s => s.TrySetResult());
    }

    /// <summary>Completes once the counter reached <paramref name="target"/>.</summary>
    public Task WaitAsync(long target, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_value >= target)
            {
                return Task.CompletedTask;
            }

            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, source));
            return source.Task.WaitAsync(cancellationToken);
        }
    }
}
