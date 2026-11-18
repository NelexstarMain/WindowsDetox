using Blocker.Core.Plugins;
using Blocker.Core.Resilience;
using Blocker.Sdk;
using Blocker.Sdk.Extensions;

namespace Blocker.Core.PluginRuntime;

/// <summary>One plugin that was compiled and loaded: its services, its circuit breaker and its running tick loops.</summary>
internal sealed class LoadedPlugin
{
    private const int RunningState = 0;
    private const int FaultedState = 1;
    private static readonly TimeSpan MinTickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxTickInterval = TimeSpan.FromDays(1);

    private readonly TimeProvider _time;
    private readonly HostSessionInfo _session;
    private readonly Action<LoadedPlugin, string> _onFaulted;
    private readonly Lock _gate = new();
    private readonly List<Task> _tickLoops = [];
    private int _state = RunningState;
    private bool _active;

    public LoadedPlugin(
        PluginInfo info,
        string packageHash,
        PluginCircuitBreaker breaker,
        PluginExtensionRegistry extensions,
        PluginIpcRegistry ipc,
        FileBackedPluginStorage storage,
        PluginLoadContext loadContext,
        HostSessionInfo session,
        TimeProvider time,
        Action<LoadedPlugin, string> onFaulted)
    {
        Info = info;
        PackageHash = packageHash;
        Breaker = breaker;
        Extensions = extensions;
        Ipc = ipc;
        Storage = storage;
        LoadContext = loadContext;
        _session = session;
        _time = time;
        _onFaulted = onFaulted;
        extensions.Added += OnExtensionAdded;
        breaker.StateChanged += OnBreakerChanged;
    }

    public PluginInfo Info { get; }

    public PluginId Id => Info.Id;

    public string PackageHash { get; }

    public PluginCircuitBreaker Breaker { get; }

    public PluginExtensionRegistry Extensions { get; }

    public PluginIpcRegistry Ipc { get; }

    public FileBackedPluginStorage Storage { get; }

    public PluginLoadContext LoadContext { get; }

    /// <summary>Gets or sets the plugin object; set while it is being created and initialized.</summary>
    public IBlockerPlugin? Instance { get; set; }

    public bool IsFaulted => Volatile.Read(ref _state) == FaultedState;

    public string? FaultReason { get; private set; }

    /// <summary>
    /// Runs a plugin callback through the circuit breaker. The call is started on a thread-pool thread so that a callback
    /// that blocks synchronously still hits its time budget instead of freezing the caller.
    /// </summary>
    public ValueTask<BreakerResult<T>> GuardAsync<T>(Func<CancellationToken, ValueTask<T>> call, T fallback, CancellationToken cancellationToken) =>
        Breaker.ExecuteAsync(token => new ValueTask<T>(Task.Run(() => call(token).AsTask(), token)), fallback, cancellationToken);

    /// <summary>Same as <see cref="GuardAsync{T}"/> for callbacks without a result; <see cref="BreakerResult{T}.Value"/> says whether it succeeded.</summary>
    public ValueTask<BreakerResult<bool>> GuardAsync(Func<CancellationToken, ValueTask> call, CancellationToken cancellationToken) =>
        Breaker.ExecuteAsync(token => new ValueTask(Task.Run(() => call(token).AsTask(), token)), cancellationToken);

    /// <summary>Starts the tick loops of everything registered so far, and of everything registered later.</summary>
    public void Activate()
    {
        ExtensionEntry[] existing;
        lock (_gate)
        {
            _active = true;
            existing = [.. Extensions.Snapshot()];
        }

        foreach (ExtensionEntry entry in existing)
        {
            StartTicks(entry);
        }
    }

    /// <summary>Removes all contributions and waits for the tick loops to end.</summary>
    public async Task CloseAsync()
    {
        Extensions.Close();
        Ipc.Close();
        Task[] loops;
        lock (_gate)
        {
            loops = [.. _tickLoops];
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
        Storage.Dispose();
    }

    private void OnExtensionAdded(ExtensionEntry entry)
    {
        bool active;
        lock (_gate)
        {
            active = _active;
        }

        if (active)
        {
            StartTicks(entry);
        }
    }

    private void StartTicks(ExtensionEntry entry)
    {
        if (entry.Extension is not ITickListener listener)
        {
            return;
        }

        Task loop = Task.Run(() => RunTicksAsync(entry, listener));
        lock (_gate)
        {
            _tickLoops.Add(loop);
        }
    }

    private async Task RunTicksAsync(ExtensionEntry entry, ITickListener listener)
    {
        CancellationToken stop = entry.Lifetime;
        try
        {
            BreakerResult<TimeSpan> interval = await GuardAsync(_ => ValueTask.FromResult(listener.Interval), TimeSpan.Zero, stop).ConfigureAwait(false);

            // A getter that fails or is slow was already counted by the breaker; the listener then ticks at the minimum interval.
            TimeSpan wanted = interval.Succeeded ? interval.Value : MinTickInterval;
            using var timer = new PeriodicTimer(TimeSpan.FromTicks(Math.Clamp(wanted.Ticks, MinTickInterval.Ticks, MaxTickInterval.Ticks)), _time);
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                _ = await GuardAsync(token => listener.OnTickAsync(new TickContext(_time.GetUtcNow(), _session.Current), token), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The extension was unregistered or the plugin was unloaded: the loop ends.
            return;
        }
    }

    private void OnBreakerChanged(object? sender, BreakerStateChangedEventArgs e)
    {
        if (e.State != BreakerState.Faulted || Interlocked.CompareExchange(ref _state, FaultedState, RunningState) != RunningState)
        {
            return;
        }

        string reason = e.Reason ?? "The plugin was faulted.";
        FaultReason = reason;
        Extensions.Close();
        Ipc.Close();
        _onFaulted(this, reason);
    }
}
