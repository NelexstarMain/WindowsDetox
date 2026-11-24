using System.Threading.Channels;
using Blocker.Contracts;
using Blocker.Core.Abstractions;
using Blocker.Core.Orchestration;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Sdk.Events;
using Blocker.Sdk.Model;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.AppRules;

/// <summary>
/// Per-app rules outside sessions, running all day next to the session coordinator. It does two things. Metering: the foreground app reported by
/// the agent is matched to its rule (hash, path or package, like the allowlist) and counted once a second while the agent is connected and the
/// user is not idle. Enforcement: a process that starts, and every process found by a reconcile pass every few seconds, is terminated and replaced
/// by the block screen when its rule blocks it right now (scheduled block or used-up pool); session 0, critical Windows processes, programs in
/// the Windows directory and Blocker's own processes are never touched. While a session runs nothing is enforced (the session decides) but usage
/// still counts. The system is not changed in any way (no IFEO, ACL or policy), so there is nothing to journal or roll back.
/// </summary>
/// <param name="engine">Rules, ledger and decisions.</param>
/// <param name="monitor">Process monitor shared with the coordinator.</param>
/// <param name="controller">Terminates processes.</param>
/// <param name="launcher">Shows the block screen in the user's session.</param>
/// <param name="hasher">Cached SHA-256 of images.</param>
/// <param name="critical">The processes that are never killed.</param>
/// <param name="stubExecutablePath">Path of the block screen executable.</param>
/// <param name="events">Receives <see cref="ProcessBlocked"/>.</param>
/// <param name="sessionRunning">Whether a session (including an emergency pass) runs.</param>
/// <param name="agentConnected">Whether the user-session agent is connected.</param>
/// <param name="time">Time source for every timer and measured interval.</param>
/// <param name="logger">Logger.</param>
internal sealed partial class AppRuleEnforcer(
    AppRuleEngine engine,
    IProcessMonitor monitor,
    IProcessController controller,
    IUserSessionLauncher launcher,
    FileHasher hasher,
    CriticalProcesses critical,
    string stubExecutablePath,
    IEventPublisher events,
    Func<bool> sessionRunning,
    Func<bool> agentConnected,
    TimeProvider time,
    ILogger<AppRuleEnforcer> logger) : IAsyncDisposable
{
    /// <summary>How often the usage is counted.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>How often every running process is checked against the rules that block, as a second chance after the monitor's events.</summary>
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

    /// <summary>Longest an image is hashed for an app rule before it counts as unrecognized.</summary>
    internal static readonly TimeSpan HashTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Largest image that is hashed for an app rule; a bigger one is not recognized by hash.</summary>
    internal const long MaxHashBytes = 512L * 1024 * 1024;

    private const int QueueCapacity = 4096;
    private const int MaxHandled = 4096;
    private const int MaxStubLaunches = 256;
    private const int MaxHashGiveUps = 256;
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxMeterStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StubDedupeWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HashRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly WorkTracker _work = new();
    private readonly Channel<ProcessInfo> _queue = Channel.CreateBounded<ProcessInfo>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Lock _handledGate = new();
    private readonly HashSet<ProcessKey> _handled = [];
    private readonly Lock _stubGate = new();
    private readonly Dictionary<string, long> _stubLaunches = new(StringComparer.Ordinal);
    private readonly Lock _hashGate = new();
    private readonly Dictionary<string, long> _hashGiveUps = new(StringComparer.Ordinal);
    private Task _consumer = Task.CompletedTask;
    private ForegroundState? _foreground;
    private volatile bool _idle;
    private volatile bool _reconcileNow;
    private long? _meterStamp;
    private long? _persistStamp;
    private long? _reconcileStamp;
    private long _carryMilliseconds;
    private bool _attached;
    private bool _monitorStarted;
    private bool _monitorFailureLogged;
    private int _disposed;

    /// <summary>The foreground image and the rules it was matched against, so a changed rule set re-matches it.</summary>
    private sealed record ForegroundState(string ImagePath, string? PackageFamilyName, AppRuleMatcher Matcher, IReadOnlyList<string> Keys);

    /// <summary>
    /// Starts the monitor when a rule can block, begins reading process starts and checks what already runs. Rules saved later are picked up by
    /// the next tick. Call <see cref="RunAsync"/> to start the ticks.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the first check is done.</returns>
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        long now = time.GetTimestamp();
        (_meterStamp, _persistStamp, _reconcileStamp) = (now, now, now);
        engine.RulesChanged += OnRulesChanged;
        _consumer = ConsumeAsync();
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the one-second tick until cancelled.</summary>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when the loop ended.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TickInterval, time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await TickOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(logger, ex);
            }
        }
    }

    /// <summary>
    /// Handles a foreground report of the agent: remembers which rules the app belongs to, so the ticks can count it. A foreground change also
    /// ends an idle state: the agent sends no heartbeat, so an idle report that was forged or went stale would otherwise pause the metering
    /// for good. Failures are logged and not thrown (an unreadable image simply matches no hash rule), because the caller also performs the
    /// window checks of a running session.
    /// </summary>
    /// <param name="imagePath">Image path of the foreground process.</param>
    /// <param name="packageFamilyName">Its package family name, if any.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the app is matched.</returns>
    public async ValueTask OnForegroundAsync(string imagePath, string? packageFamilyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        _idle = false;
        try
        {
            _ = await MatchForegroundAsync(imagePath, packageFamilyName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Matching the foreground app to its rule");
        }
    }

    /// <summary>Records whether the user is idle; an idle user is not counted.</summary>
    /// <param name="isIdle">Whether the user is idle.</param>
    public void SetIdle(bool isIdle) => _idle = isIdle;

    /// <summary>Waits until every queued process start was handled (tests).</summary>
    internal Task DrainAsync() => _work.WhenIdle;

    /// <summary>Runs one tick: counts usage, writes it out when due and reconciles when due (tests; the loop does the same every second).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the tick is done.</returns>
    internal async ValueTask TickOnceAsync(CancellationToken cancellationToken)
    {
        bool reconcileDue;
        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MeterAsync(cancellationToken).ConfigureAwait(false);
            await PersistIfDueAsync(cancellationToken).ConfigureAwait(false);
            reconcileDue = _reconcileNow || IsDue(_reconcileStamp, ReconcileInterval);
            if (reconcileDue)
            {
                _reconcileNow = false;
                _reconcileStamp = time.GetTimestamp();
            }
        }
        finally
        {
            _ = _tickGate.Release();
        }

        // Outside the tick gate: a pass hashes the images of everything that runs, and the metering of the next tick must not queue up behind it.
        if (reconcileDue)
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops reading process starts and writes the usage out; the monitor itself is left to its owner.</summary>
    /// <returns>A task that completes when everything stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        engine.RulesChanged -= OnRulesChanged;
        if (_attached)
        {
            monitor.ProcessStarted -= OnProcessStarted;
            _attached = false;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _ = _queue.Writer.TryComplete();
        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogLoopStopped(logger);
        }

        try
        {
            await engine.PersistUsageAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Writing the app usage");
        }

        _work.Reset();
        _stop.Dispose();
    }

    private bool IsDue(long? stamp, TimeSpan interval) => stamp is not { } last || time.GetElapsedTime(last) >= interval;

    private void OnRulesChanged(object? sender, EventArgs e) => _reconcileNow = true;

    private void OnProcessStarted(object? sender, ProcessStartedEventArgs e)
    {
        // Runs on the monitor's thread, shared with the session pipeline: keep it cheap and never block it. A start that does not fit the queue
        // is not lost, the next reconcile pass finds the process.
        if (!engine.CanBlock || sessionRunning())
        {
            return;
        }

        _work.Add();
        if (!_queue.Writer.TryWrite(e.Process))
        {
            _work.Done();
            LogQueueFull(logger);
        }
    }

    private async Task ConsumeAsync()
    {
        CancellationToken stop = _stop.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stop).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out ProcessInfo? process))
                {
                    try
                    {
                        await JudgeStartedAsync(process, stop).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogProcessFailed(logger, ex, process.ProcessId);
                    }
                    finally
                    {
                        _work.Done();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogLoopStopped(logger);
        }
    }

    private async ValueTask JudgeStartedAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        if (sessionRunning())
        {
            return;
        }

        IReadOnlyList<BlockingRule> blocks = engine.CurrentBlocks();
        if (blocks.Count > 0)
        {
            await JudgeAsync(process, blocks, new AppRuleMatcher(blocks.Select(static b => b.Rule)), cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureMonitorAsync(CancellationToken cancellationToken)
    {
        if (!engine.CanBlock)
        {
            return;
        }

        if (!_attached)
        {
            monitor.ProcessStarted += OnProcessStarted;
            _attached = true;
        }

        if (_monitorStarted && !monitor.Completion.IsCompleted)
        {
            return;
        }

        try
        {
            await monitor.StartAsync(cancellationToken).ConfigureAwait(false);
            (_monitorStarted, _monitorFailureLogged) = (true, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal: the reconcile pass below still finds every process within one interval.
            if (!_monitorFailureLogged)
            {
                _monitorFailureLogged = true;
                LogMonitorFailed(logger, ex);
            }
        }
    }

    /// <summary>Starts or restarts the monitor, then terminates every running process that belongs to a rule that blocks right now.</summary>
    private async ValueTask ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _reconcileGate.Release();
        }
    }

    private async ValueTask ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureMonitorAsync(cancellationToken).ConfigureAwait(false);
        if (sessionRunning())
        {
            return;
        }

        IReadOnlyList<BlockingRule> blocks = engine.CurrentBlocks();
        if (blocks.Count == 0)
        {
            return;
        }

        IReadOnlyList<ProcessInfo> running;
        try
        {
            running = monitor.EnumerateRunning();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Enumerating running processes");
            return;
        }

        var matcher = new AppRuleMatcher(blocks.Select(static b => b.Rule));
        foreach (ProcessInfo process in running.OrderBy(static p => p.StartTimeTicks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await JudgeAsync(process, blocks, matcher, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogProcessFailed(logger, ex, process.ProcessId);
            }
        }
    }

    private async ValueTask JudgeAsync(ProcessInfo process, IReadOnlyList<BlockingRule> blocks, AppRuleMatcher matcher, CancellationToken cancellationToken)
    {
        if (process.SessionId == 0 || IsHandled(ProcessKey.Of(process)))
        {
            return;
        }

        if (string.IsNullOrEmpty(process.ImagePath))
        {
            // Fail open for a process that cannot be named (as the session pipeline does): it is judged again by the next reconcile pass.
            if (TryResolveImage(process) is not { } resolved)
            {
                return;
            }

            process = process with { ImagePath = resolved };
        }

        if (critical.IsCritical(process.ImagePath) || critical.IsInTrustedSystemDirectory(process.ImagePath))
        {
            return;
        }

        // The cheap identities first: an image that its path or package already names is not hashed at all.
        string? package = matcher.NeedsPackage ? TryGetPackage(process.ProcessId) : null;
        string? sha256 = null;
        IReadOnlyList<string> keys = matcher.Match(process.ImagePath, null, package);
        if (keys.Count == 0 && matcher.NeedsHash && await HashAsync(process.ImagePath, cancellationToken).ConfigureAwait(false) is { } hash)
        {
            sha256 = hash;
            keys = matcher.Match(process.ImagePath, hash, package);
        }

        if (keys.Count > 0)
        {
            BlockingRule rule = blocks.First(b => keys.Contains(b.Key));
            await BlockAsync(process, rule.State, sha256, package, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask BlockAsync(ProcessInfo process, AppRuleState state, string? sha256, string? package, CancellationToken cancellationToken)
    {
        // Looked at again right before acting: a session that just started decides on its own. A process is acted on once: after a successful
        // kill it may linger in the snapshot for a moment, and a failed kill (almost always a process that is already gone) is not worth a
        // block screen every few seconds.
        if (sessionRunning() || !TryMarkHandled(ProcessKey.Of(process)))
        {
            return;
        }

        bool terminated = false;
        try
        {
            terminated = controller.TryTerminate(process.ProcessId, process.StartTimeTicks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Terminating a process");
        }

        VerdictReason reason = state == AppRuleState.ScheduledBlock ? VerdictReason.AppScheduledBlock : VerdictReason.AppBudgetExhausted;
        ProcessDescriptor descriptor = Describe(process, sha256, package);
        LogBlocked(logger, descriptor.ImageName, process.ProcessId, reason, terminated);
        LaunchStub(process.ImagePath, reason);
        try
        {
            // The plugin SDK has no reason for app rules yet; the closest existing one is reported.
            await events.PublishAsync(new ProcessBlocked(descriptor, BlockReason.NotAllowlisted, null, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Publishing the block event");
        }
    }

    private async ValueTask MeterAsync(CancellationToken cancellationToken)
    {
        long now = time.GetTimestamp();
        TimeSpan elapsed = _meterStamp is { } last ? time.GetElapsedTime(last, now) : TimeSpan.Zero;
        _meterStamp = now;
        if (!agentConnected())
        {
            // Nobody reports the foreground without an agent, and a new agent starts from a fresh report.
            (_foreground, _idle, _carryMilliseconds) = (null, false, 0);
            return;
        }

        if (_idle || Volatile.Read(ref _foreground) is not { } foreground)
        {
            return;
        }

        if (!ReferenceEquals(foreground.Matcher, engine.Matcher))
        {
            foreground = await MatchForegroundAsync(foreground.ImagePath, foreground.PackageFamilyName, cancellationToken).ConfigureAwait(false);
        }

        if (foreground.Keys.Count == 0)
        {
            return;
        }

        // A stretch longer than the cap means the loop stalled or the clock jumped; what happened in between is not counted.
        _carryMilliseconds += (long)Math.Clamp(elapsed.TotalMilliseconds, 0, MaxMeterStep.TotalMilliseconds);
        long seconds = _carryMilliseconds / 1000;
        _carryMilliseconds -= seconds * 1000;
        if (seconds > 0 && engine.AddUsage(foreground.Keys, seconds))
        {
            _reconcileNow = true; // the pool just ran out: do not wait for the next interval
        }
    }

    private async ValueTask<ForegroundState> MatchForegroundAsync(string imagePath, string? package, CancellationToken cancellationToken)
    {
        AppRuleMatcher matcher = engine.Matcher;
        string? sha256 = matcher.NeedsHash && imagePath.Length > 0 ? await HashAsync(imagePath, cancellationToken).ConfigureAwait(false) : null;
        var state = new ForegroundState(imagePath, package, matcher, matcher.Match(imagePath, sha256, package));
        Volatile.Write(ref _foreground, state);
        return state;
    }

    private async ValueTask PersistIfDueAsync(CancellationToken cancellationToken)
    {
        if (!IsDue(_persistStamp, PersistInterval))
        {
            return;
        }

        _persistStamp = time.GetTimestamp();
        try
        {
            await engine.PersistUsageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Writing the app usage");
        }
    }

    private bool IsHandled(ProcessKey key)
    {
        lock (_handledGate)
        {
            return _handled.Contains(key);
        }
    }

    private bool TryMarkHandled(ProcessKey key)
    {
        lock (_handledGate)
        {
            if (_handled.Count >= MaxHandled)
            {
                _handled.Clear(); // bounded: forgetting at worst repeats one kill attempt
            }

            return _handled.Add(key);
        }
    }

    private void LaunchStub(string imagePath, VerdictReason reason)
    {
        if (!TryReserveStubLaunch(WinPath.Normalize(imagePath)))
        {
            return;
        }

        try
        {
            string arguments = $"--blocked \"{imagePath.Replace("\"", string.Empty, StringComparison.Ordinal)}\" --reason {reason}";
            if (launcher.LaunchInActiveSession(stubExecutablePath, arguments) is null)
            {
                LogNoUserSession(logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Launching the block screen");
        }
    }

    private bool TryReserveStubLaunch(string key)
    {
        long now = time.GetTimestamp();
        lock (_stubGate)
        {
            if (_stubLaunches.TryGetValue(key, out long last) && time.GetElapsedTime(last, now) < StubDedupeWindow)
            {
                return false;
            }

            _stubLaunches[key] = now;
            if (_stubLaunches.Count > MaxStubLaunches)
            {
                foreach (string stale in _stubLaunches.Where(p => time.GetElapsedTime(p.Value, now) >= StubDedupeWindow).Select(static p => p.Key).ToList())
                {
                    _ = _stubLaunches.Remove(stale);
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Hashes an image for a hash rule, within limits: only a file on a local drive (a share or device path is never opened) and outside the
    /// system directory (never enforced, so never worth recognizing), no larger than
    /// <see cref="MaxHashBytes"/>, and for no longer than <see cref="HashTimeout"/>. An image that timed out is not tried again for a minute, so
    /// a stalled disk does not cost the timeout on every pass. No hash means no hash rule matches: the app is not recognized.
    /// </summary>
    private async ValueTask<string?> HashAsync(string path, CancellationToken cancellationToken)
    {
        if (!WinPath.IsLocalDrivePath(path) || critical.IsInTrustedSystemDirectory(path) || IsHashGivenUp(path))
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(HashTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await hasher.GetSha256Async(path, MaxHashBytes, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            GiveUpHashing(path);
            LogHashTimedOut(logger, path, HashTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Hashing an image");
            return null;
        }
    }

    private bool IsHashGivenUp(string path)
    {
        lock (_hashGate)
        {
            return _hashGiveUps.TryGetValue(WinPath.Normalize(path), out long since) && time.GetElapsedTime(since) < HashRetryDelay;
        }
    }

    private void GiveUpHashing(string path)
    {
        lock (_hashGate)
        {
            if (_hashGiveUps.Count >= MaxHashGiveUps)
            {
                _hashGiveUps.Clear(); // bounded: forgetting at worst repeats one timeout
            }

            _hashGiveUps[WinPath.Normalize(path)] = time.GetTimestamp();
        }
    }

    private string? TryResolveImage(ProcessInfo process)
    {
        try
        {
            return controller.TryGetImagePath(process.ProcessId, process.StartTimeTicks) is { Length: > 0 } image ? image : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Resolving an image path by process id");
            return null;
        }
    }

    private string? TryGetPackage(int processId)
    {
        try
        {
            return controller.GetPackageFamilyName(processId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Reading the package family name");
            return null;
        }
    }

    private ProcessDescriptor Describe(ProcessInfo process, string? sha256, string? package)
    {
        string path = process.ImagePath;
        DateTimeOffset started = process.StartTimeTicks > 0 && process.StartTimeTicks <= MaxFileTime
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(process.StartTimeTicks), TimeSpan.Zero)
            : time.GetUtcNow();
        return new ProcessDescriptor(process.ProcessId, process.ParentProcessId, path[(path.LastIndexOfAny(['\\', '/']) + 1)..], path, sha256, package, started);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The app rule process pipeline stopped.")]
    private static partial void LogLoopStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "App rule blocked {Image} (pid {ProcessId}, {Reason}, terminated: {Terminated}).")]
    private static partial void LogBlocked(ILogger logger, string image, int processId, VerdictReason reason, bool terminated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The process monitor could not be started for the app rules; running programs are still checked every few seconds.")]
    private static partial void LogMonitorFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hashing {Image} took longer than {Seconds} s and was given up; it is not recognized by hash for a minute.")]
    private static partial void LogHashTimedOut(ILogger logger, string image, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The app rule queue is full; the next reconcile pass finds the process that did not fit.")]
    private static partial void LogQueueFull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling process {ProcessId} for the app rules failed.")]
    private static partial void LogProcessFailed(ILogger logger, Exception exception, int processId);

    [LoggerMessage(Level = LogLevel.Error, Message = "An app rule tick failed; the loop continues.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Step} failed.")]
    private static partial void LogStepFailed(ILogger logger, Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No user is logged on; the block screen for an app rule was not shown.")]
    private static partial void LogNoUserSession(ILogger logger);
}
