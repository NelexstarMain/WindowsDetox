using System.Globalization;
using System.Threading.Channels;
using Blocker.Core.Abstractions;
using Blocker.Core.Journal;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Core.Session;
using Blocker.Core.WebFilter;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Orchestration;

/// <summary>Creates the (not yet started) filter proxy for a session; <paramref name="port"/> 0 means an ephemeral port.</summary>
public delegate WebFilterProxy WebFilterProxyFactory(IWebFilterPolicy policy, int port);

/// <summary>
/// The service's brain: runs a session end to end. At start it validates, starts the state machine, the filter proxy, applies the planned
/// system changes through the rollback journal, enables the browser firewall, enforces once over the running processes and starts the
/// process pipeline and the 1 s tick loop. An emergency pass rolls the whole journal back and re-applies the plan when it ends; expiry,
/// break-glass and developer abort tear everything down. Lifecycle operations are serialized; every external call is guarded so that one
/// failing port never stops the loops. <see cref="DisposeAsync"/> only stops activity (service shutdown): the session and the journal stay
/// intact, <see cref="RecoverOnStartupAsync"/> resumes them.
/// </summary>
public sealed partial class EnforcementCoordinator(
    SessionStateMachine session,
    RollbackJournal journal,
    IEnforcementChangePlanner planner,
    ISessionPolicyStore policyStore,
    IProcessMonitor monitor,
    IProcessController controller,
    IUserSessionLauncher launcher,
    ISignatureVerifier signatures,
    FileHasher hasher,
    IBrowserFirewall firewall,
    WebFilterProxyFactory proxyFactory,
    IEventPublisher events,
    EnforcementSettings settings,
    TimeProvider time,
    ILogger<EnforcementCoordinator> logger,
    Func<ProcessDescriptor, CancellationToken, ValueTask<IReadOnlyList<InterceptDecision>>>? interceptor = null,
    Func<int, CancellationToken, Task<bool>>? closeGracefully = null,
    BlockNoticeSwitch? blockNotice = null) : IAsyncDisposable
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan GracefulCloseBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReapplyBackoff = TimeSpan.FromSeconds(10);
    private const int MaxListedSkippedTargets = 5;

    /// <summary>How often an active session re-checks the running processes against the monitor's events.</summary>
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

    private EnforcementSettings _settings = Validated(settings);
    private readonly Func<int, CancellationToken, Task<bool>> _closeGracefully =
        closeGracefully ?? ((pid, ct) => controller.TryCloseGracefullyAsync(pid, GracefulCloseBudget, ct));
    private readonly BlockNoticeSwitch _blockNotice = blockNotice ?? new BlockNoticeSwitch();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProgressCounter _ticks = new();
    private SessionRuntime? _runtime;
    private bool _monitorStarted;
    private int _disposed;

    /// <summary>Raised for the user-session agent (toasts). Handler exceptions are logged and swallowed.</summary>
    public event EventHandler<EnforcementNotification>? Notification;

    /// <summary>Gets the latest session snapshot.</summary>
    public SessionSnapshot Snapshot => session.Snapshot;

    /// <summary>Gets whether a session is running (including during an emergency pass).</summary>
    public bool IsSessionActive => session.Snapshot.IsSessionRunning;

    /// <summary>
    /// Starts an enforced session. Any failure after the state machine started rolls everything back and ends the session through its
    /// break-glass code (<c>SessionEnded</c> with reason <c>BreakGlass</c>), so a failed start never leaves a locked half-configured machine.
    /// Domains come from <paramref name="domains"/>; <c>allowlist.Domains</c> is not consulted.
    /// </summary>
    public async ValueTask<StartEnforcedSessionResult> StartSessionAsync(
        SessionConfig config,
        Allowlist allowlist,
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(domains);
        ThrowIfDisposed();
        List<string> errors = [.. domains.Where(static d => !DomainPattern.TryParse(d, out _)).Select(static d => $"Invalid domain pattern '{d}'.")];
        if (errors.Count > 0)
        {
            return StartEnforcedSessionResult.Failed(errors);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is not null)
            {
                return StartEnforcedSessionResult.Failed(["A session is already running."]);
            }

            StartSessionResult started = await session.StartAsync(config, cancellationToken).ConfigureAwait(false);
            if (!started.Started)
            {
                return StartEnforcedSessionResult.Failed(started.Errors);
            }

            StopCleanupRetry();
            var policy = new SessionPolicy([.. allowlist.Entries], [.. domains]);
            SessionRuntime runtime = BuildRuntime(policy);
            _runtime = runtime;
            try
            {
                await policyStore.SaveAsync(policy, cancellationToken).ConfigureAwait(false);
                await BeginAsync(runtime, strict: true, cancellationToken).ConfigureAwait(false);
                return new StartEnforcedSessionResult(true, started.SessionId, started.BreakGlassCode, []) { Warnings = runtime.Warnings };
            }
            catch (Exception ex)
            {
                LogSessionStartFailed(logger, ex);
                await TeardownAsync(fromTickLoop: false, announce: false).ConfigureAwait(false);
                await EndFailedSessionAsync(started.BreakGlassCode).ConfigureAwait(false);
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                return StartEnforcedSessionResult.Failed([$"The session could not be enforced and was rolled back: {ex.Message}"]);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Called when the service starts. Restores the persisted session: when one is still running its protections are rolled back and
    /// re-applied from scratch (deterministic, also when the journal is damaged, in which case the marker sweep only clears the way for the
    /// re-apply) and enforcement resumes; when none is running, leftover journal changes are rolled back.
    /// </summary>
    public async ValueTask RecoverOnStartupAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is not null)
            {
                return;
            }

            _ = await session.RestoreAsync(cancellationToken).ConfigureAwait(false);
            if (!session.Snapshot.IsSessionRunning)
            {
                await RollbackLeftoversAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            SessionPolicy? policy = await LoadPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (policy is null)
            {
                LogPolicyMissing(logger);
                Notify(EnforcementNotificationKind.ProtectionDegraded, "The saved allowlist is unavailable; everything outside the system baseline is blocked until the session ends.");
            }

            StopCleanupRetry();
            SessionRuntime runtime = BuildRuntime(policy ?? new SessionPolicy([], []));
            _runtime = runtime;
            LogRecovering(logger, session.Snapshot.Phase);
            await BeginAsync(runtime, strict: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the installed-application inventory that plans the IFEO redirects of the next session. Ignored while a session runs
    /// (the running session keeps its plan), in which case <see langword="false"/> is returned.
    /// </summary>
    public async ValueTask<bool> UpdateScannedAppsAsync(IReadOnlyList<InstalledApp> apps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is not null)
            {
                return false;
            }

            _settings = _settings with { ScannedApps = [.. apps] };
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts an emergency pass: enforcement and every protection are suspended until the pass ends (see the tick loop).</summary>
    public async ValueTask<EmergencyPassResult> UseEmergencyPassAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is not { } runtime)
            {
                return EmergencyPassResult.NotInSession;
            }

            EmergencyPassResult result = await session.RequestEmergencyPassAsync(cancellationToken).ConfigureAwait(false);
            if (result == EmergencyPassResult.Started)
            {
                await SuspendForPassAsync(runtime).ConfigureAwait(false);
            }
            else if (!session.Snapshot.IsSessionRunning)
            {
                await TeardownAsync(fromTickLoop: false, announce: true).ConfigureAwait(false); // expired while asking
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the session with the recovery code and tears everything down; false for a wrong code or no session.</summary>
    public async ValueTask<bool> BreakGlassAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await session.TryBreakGlassAsync(code, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await TeardownAsync(fromTickLoop: false, announce: true).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the session without a code (DEVMODE builds only) and tears everything down.</summary>
    public async ValueTask<bool> DeveloperAbortAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await session.TryDeveloperAbortAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await TeardownAsync(fromTickLoop: false, announce: true).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops all activity without ending the session or rolling anything back (service shutdown); not safe to use afterwards.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SessionRuntime? runtime = _runtime;
        _cleanupStop?.Cancel(); // before taking the gate: a retry waiting for it must not block the shutdown
        await StopRuntimeLoopsAsync(runtime, fromTickLoop: false).ConfigureAwait(false);
        await GuardedAsync("Stopping the cleanup retry", () => _cleanupRetry).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _runtime, null) is { } current)
            {
                await StopProxyAsync(current).ConfigureAwait(false);
                current.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Waits until the process queue is empty (tests).</summary>
    internal Task DrainAsync() => _runtime?.Work.WhenIdle ?? Task.CompletedTask;

    /// <summary>Waits until the tick loop completed <paramref name="count"/> iterations in total (tests).</summary>
    internal Task WaitForTicksAsync(long count, CancellationToken cancellationToken) => _ticks.WaitAsync(count, cancellationToken);

    /// <summary>Runs one tick iteration synchronously (tests; the loop does the same every second).</summary>
    internal async ValueTask TickOnceAsync(CancellationToken cancellationToken)
    {
        if (_runtime is { } runtime)
        {
            await TickAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    private static EnforcementSettings Validated(EnforcementSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return value;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private SessionRuntime BuildRuntime(SessionPolicy policy)
    {
        var allowlist = new Allowlist(policy.Entries);
        var domains = new DomainMatcher(policy.Domains.Select(static d => DomainPattern.TryParse(d, out DomainPattern? p) ? p : null).OfType<DomainPattern>());
        var critical = new CriticalProcesses(_settings.WindowsDirectory, _settings.InstallDirectory, _settings.ExtraTrustedRoots);
        var decision = new EnforcementPolicy(critical, new BypassTools(_settings.BypassToolHashes), allowlist, _settings.WindowsDirectory);
        return new SessionRuntime(domains, critical, decision, _settings.BuildIfeoImageNames(allowlist), _settings.ResolveBrowsers());
    }

    /// <summary>Brings protections and the process pipeline up. Caller holds the gate and has set <see cref="_runtime"/>.</summary>
    private async ValueTask BeginAsync(SessionRuntime runtime, bool strict, CancellationToken cancellationToken)
    {
        string? error = await BringUpAsync(runtime, protect: session.Snapshot.Phase == SessionPhase.Active, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            if (strict)
            {
                throw new InvalidOperationException(error);
            }

            Degrade(runtime, error);
        }
        else
        {
            ReportSkippedProtections(runtime);
        }

        if (_monitorStarted)
        {
            await EnforceRunningAsync(runtime, graceful: false, cancellationToken).ConfigureAwait(false);
        }

        runtime.ReconcileStamp = time.GetTimestamp();
        runtime.Consumer = ConsumeAsync(runtime);
        runtime.TickLoop = RunTickLoopAsync(runtime);
    }

    /// <summary>
    /// Idempotent bring-up: monitor events, proxy, then (when <paramref name="protect"/>) a deterministic reset of the journal, the planned
    /// changes and the browser firewall. Returns an error text, or <see langword="null"/> on success.
    /// </summary>
    private async ValueTask<string?> BringUpAsync(SessionRuntime runtime, bool protect, CancellationToken cancellationToken)
    {
        // Named in the error text, so a user who only sees the UI message can still tell which protection failed.
        string step = "Starting the process monitor";
        try
        {
            if (!runtime.MonitorAttached)
            {
                monitor.ProcessStarted += OnProcessStarted;
                monitor.ProcessExited += OnProcessExited;
                runtime.MonitorAttached = true;
            }

            if (!_monitorStarted)
            {
                await monitor.StartAsync(cancellationToken).ConfigureAwait(false);
                _monitorStarted = true;
            }

            runtime.ProtectionApplied = false;
            runtime.SkippedTargets = [];
            step = "Rolling back earlier changes";
            RollbackReport reset = await journal.RollbackAllAsync(cancellationToken).ConfigureAwait(false);
            if (!reset.Complete)
            {
                return "Earlier system changes could not be rolled back.";
            }

            if (!protect)
            {
                return null;
            }

            step = "Starting the web filter proxy";
            EnsureProxy(runtime);
            step = "Planning the session changes";
            IReadOnlyList<PlannedChange> plan = planner.PlanSessionChanges(new SessionPlanInput(
                runtime.Proxy!.Port,
                runtime.IfeoImageNames,
                _settings.StubExecutablePath,
                _settings.ResolveProtectedDirectories(),
                _settings.ServiceNames));
            List<string> skipped = [];
            foreach (PlannedChange change in plan)
            {
                step = $"Applying '{change.Kind}' to '{change.Target}'";
                try
                {
                    await journal.ApplyAsync(change.Kind, change.Target, change.DesiredStateJson, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (change.BestEffort && IsAccessFault(ex))
                {
                    // The journal has already undone the failed apply (or kept the entry Pending for the next rollback), so nothing is half applied.
                    LogBestEffortSkipped(logger, ex, step);
                    skipped.Add(change.Target);
                }
            }

            if (runtime.Browsers.Count > 0)
            {
                step = "Enabling the browser firewall";
                await firewall.EnableAsync(runtime.Browsers, cancellationToken).ConfigureAwait(false);
            }

            runtime.ProtectionApplied = true;
            runtime.Degraded = false;
            runtime.SkippedTargets = skipped;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBringUpFailed(logger, step, ex);
            return string.Create(CultureInfo.InvariantCulture, $"{step} failed: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
        }
    }

    /// <summary>The ways Windows refuses a write to a protected key, directory or service; anything else is a bug and stays fatal.</summary>
    private static bool IsAccessFault(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception or System.Security.SecurityException;

    /// <summary>
    /// Tells the user that best-effort changes were refused. They are not retried within the session: the process monitor and the
    /// reconcile pass still stop the programs an IFEO redirect would have caught.
    /// </summary>
    private void ReportSkippedProtections(SessionRuntime runtime)
    {
        IReadOnlyList<string> skipped = runtime.SkippedTargets;
        if (skipped.Count == 0)
        {
            runtime.Warnings = [];
            return;
        }

        string listed = string.Join(", ", skipped.Take(MaxListedSkippedTargets));
        string more = skipped.Count > MaxListedSkippedTargets ? string.Create(CultureInfo.InvariantCulture, $" (+{skipped.Count - MaxListedSkippedTargets} more)") : string.Empty;
        string warning = $"Windows refused some extra protections: {listed}{more}. The session still blocks apps and websites, but these are not hardened.";
        runtime.Warnings = [warning];
        runtime.Degraded = true;
        Notify(EnforcementNotificationKind.ProtectionDegraded, warning);
    }

    private void EnsureProxy(SessionRuntime runtime)
    {
        if (runtime.Proxy is not null)
        {
            return;
        }

        WebFilterProxy proxy = proxyFactory(new FuncWebFilterPolicy(runtime.Domains.IsAllowed), _settings.ProxyPort);
        try
        {
            _ = proxy.Start();
        }
        catch
        {
            _ = proxy.DisposeAsync().AsTask(); // never started: nothing to drain
            throw;
        }

        runtime.Proxy = proxy;
    }

    private void Degrade(SessionRuntime runtime, string error)
    {
        runtime.RetryStamp = time.GetTimestamp();
        if (!runtime.Degraded)
        {
            runtime.Degraded = true;
            Notify(EnforcementNotificationKind.ProtectionDegraded, $"Some protections could not be applied and will be retried: {error}");
        }
    }

    private async ValueTask SuspendForPassAsync(SessionRuntime runtime)
    {
        runtime.ProtectionApplied = false;
        await GuardedAsync("Disabling the firewall", () => firewall.DisableAsync(CancellationToken.None)).ConfigureAwait(false);
        RollbackReport? report = await RollbackAllGuardedAsync().ConfigureAwait(false);
        LogPassSuspended(logger, report?.Restored ?? 0);
    }

    private async Task RunTickLoopAsync(SessionRuntime runtime)
    {
        CancellationToken stop = runtime.Stop.Token;
        using var timer = new PeriodicTimer(TickInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(runtime, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTickFailed(logger, ex);
                }

                _ticks.Increment();
            }
        }
        catch (OperationCanceledException)
        {
            LogLoopStopped(logger, "Tick loop");
        }
    }

    private async ValueTask TickAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        bool enforce = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_runtime, runtime))
            {
                return;
            }

            TickOutcome outcome = await session.TickAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.SessionExpired || !session.Snapshot.IsSessionRunning)
            {
                await TeardownAsync(fromTickLoop: true, announce: true).ConfigureAwait(false);
                return;
            }

            if (outcome.PassWarningDue)
            {
                Notify(EnforcementNotificationKind.PassEndingSoon, "Your emergency pass ends in less than a minute. Save your work.");
            }

            if (outcome.PassEnded)
            {
                Notify(EnforcementNotificationKind.PassEnded, "The emergency pass ended; the session is enforced again.");
                await ResumeAsync(runtime, cancellationToken).ConfigureAwait(false);
                enforce = true;
            }
            else if (!runtime.ProtectionApplied
                && session.Snapshot.Phase == SessionPhase.Active
                && time.GetElapsedTime(runtime.RetryStamp) >= ReapplyBackoff)
            {
                await ResumeAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (enforce)
        {
            await EnforceRunningAsync(runtime, graceful: true, cancellationToken).ConfigureAwait(false);
        }

        if (ReferenceEquals(_runtime, runtime)
            && session.Snapshot.Phase == SessionPhase.Active
            && time.GetElapsedTime(runtime.ReconcileStamp) >= ReconcileInterval)
        {
            runtime.ReconcileStamp = time.GetTimestamp();
            await ReconcileAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ResumeAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        string? error = await BringUpAsync(runtime, protect: true, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            Degrade(runtime, error);
        }
        else
        {
            ReportSkippedProtections(runtime);
        }
    }

    /// <summary>Full, idempotent teardown. Caller holds the gate. Never throws; each step is guarded and uncancellable.</summary>
    private async ValueTask TeardownAsync(bool fromTickLoop, bool announce)
    {
        SessionRuntime? runtime = Interlocked.Exchange(ref _runtime, null);
        await StopRuntimeLoopsAsync(runtime, fromTickLoop).ConfigureAwait(false);
        await GuardedAsync("Disabling the firewall", () => firewall.DisableAsync(CancellationToken.None)).ConfigureAwait(false);
        if (runtime is not null)
        {
            await StopProxyAsync(runtime).ConfigureAwait(false);
        }

        RollbackReport? report = await RollbackAllGuardedAsync().ConfigureAwait(false);
        if (report is not { Complete: true })
        {
            StartCleanupRetry();
        }

        await GuardedAsync("Clearing the saved session policy", () => policyStore.ClearAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        runtime?.Work.Reset();
        ForgetStubLaunches();
        if (!fromTickLoop)
        {
            runtime?.Dispose();
        }

        if (runtime is not null && announce)
        {
            Notify(EnforcementNotificationKind.SessionEnded, "The session ended and all restrictions were removed.");
        }

        LogTornDown(logger, report?.Restored ?? 0);
    }

    private async ValueTask StopRuntimeLoopsAsync(SessionRuntime? runtime, bool fromTickLoop)
    {
        if (runtime is null)
        {
            return;
        }

        if (runtime.MonitorAttached)
        {
            monitor.ProcessStarted -= OnProcessStarted;
            monitor.ProcessExited -= OnProcessExited;
            runtime.MonitorAttached = false;
        }

        await runtime.Stop.CancelAsync().ConfigureAwait(false);
        _ = runtime.Queue.Writer.TryComplete();
        await GuardedAsync("Stopping the process pipeline", () => runtime.Consumer).ConfigureAwait(false);
        if (!fromTickLoop)
        {
            await GuardedAsync("Stopping the tick loop", () => runtime.TickLoop).ConfigureAwait(false);
        }
    }

    private async ValueTask StopProxyAsync(SessionRuntime runtime)
    {
        if (Interlocked.Exchange(ref runtime.Proxy, null) is { } proxy)
        {
            await GuardedAsync("Stopping the filter proxy", () => proxy.StopAsync()).ConfigureAwait(false);
        }
    }

    private async ValueTask<RollbackReport?> RollbackAllGuardedAsync(bool announce = true)
    {
        try
        {
            RollbackReport report = await journal.RollbackAllAsync(CancellationToken.None).ConfigureAwait(false);
            if (!report.Complete)
            {
                LogRollbackIncomplete(logger, report.Mode, report.Restored, report.Failures.Count);
                if (announce)
                {
                    Notify(EnforcementNotificationKind.ProtectionDegraded, "Some system changes could not be reverted yet; Blocker keeps retrying.");
                }
            }

            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Rolling back the journal");
            return null;
        }
    }

    private async ValueTask RollbackLeftoversAsync(CancellationToken cancellationToken)
    {
        bool pending;
        try
        {
            pending = await journal.HasPendingChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Reading the journal");
            pending = true;
        }

        if (pending)
        {
            LogRollingBackLeftovers(logger);
            if (await RollbackAllGuardedAsync().ConfigureAwait(false) is not { Complete: true })
            {
                StartCleanupRetry();
            }
        }

        await GuardedAsync("Clearing the saved session policy", () => policyStore.ClearAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    private async ValueTask EndFailedSessionAsync(string? breakGlassCode)
    {
        if (breakGlassCode is null)
        {
            return;
        }

        try
        {
            _ = await session.TryBreakGlassAsync(breakGlassCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Ending the failed session");
        }
    }

    private async ValueTask<SessionPolicy?> LoadPolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await policyStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Loading the saved session policy");
            return null;
        }
    }

    private async ValueTask GuardedAsync(string step, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, step);
        }
    }

    private void Notify(EnforcementNotificationKind kind, string text)
    {
        if (Notification is not { } handlers)
        {
            return;
        }

        var notification = new EnforcementNotification(kind, text);
        foreach (EventHandler<EnforcementNotification> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception ex)
            {
                LogStepFailed(logger, ex, "Notification handler");
            }
        }
    }

    /// <summary>Per-session state; the coordinator swaps it atomically so late callbacks of an ended session cannot touch the next one.</summary>
    private const int MaxUnnamedReported = 1024;

    private sealed class SessionRuntime(
        DomainMatcher domains,
        CriticalProcesses critical,
        EnforcementPolicy decision,
        IReadOnlyList<string> ifeoImageNames,
        IReadOnlyList<string> browsers) : IDisposable
    {
        public DomainMatcher Domains { get; } = domains;

        public CriticalProcesses Critical { get; } = critical;

        public EnforcementPolicy Decision { get; } = decision;

        public IReadOnlyList<string> IfeoImageNames { get; } = ifeoImageNames;

        public IReadOnlyList<string> Browsers { get; } = browsers;

        public ProcessTree Tree { get; } = new();

        public Channel<WorkItem> Queue { get; } = Channel.CreateBounded<WorkItem>(
            new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        public WorkTracker Work { get; } = new();

        public CancellationTokenSource Stop { get; } = new();

        public Task Consumer { get; set; } = Task.CompletedTask;

        public Task TickLoop { get; set; } = Task.CompletedTask;

        public WebFilterProxy? Proxy;

        public bool MonitorAttached { get; set; }

        public bool ProtectionApplied { get; set; }

        public bool Degraded { get; set; }

        /// <summary>Targets of best-effort changes the last bring-up could not apply.</summary>
        public IReadOnlyList<string> SkippedTargets { get; set; } = [];

        /// <summary>User-visible description of <see cref="SkippedTargets"/>; empty when nothing was skipped.</summary>
        public IReadOnlyList<string> Warnings { get; set; } = [];

        public long RetryStamp { get; set; }

        public long ReconcileStamp { get; set; }

        public bool MonitorDown { get; set; }

        private readonly HashSet<ProcessKey> _unnamedReported = [];

        /// <summary>True the first time this process is reported as unnamed, so a process that stays unreadable is logged once, not every reconcile interval.</summary>
        public bool FirstUnnamedReport(ProcessKey key)
        {
            lock (_unnamedReported)
            {
                if (_unnamedReported.Count >= MaxUnnamedReported)
                {
                    _unnamedReported.Clear(); // bounded: forgetting only repeats a log line
                }

                return _unnamedReported.Add(key);
            }
        }

        public void Dispose() => Stop.Dispose();
    }
}
