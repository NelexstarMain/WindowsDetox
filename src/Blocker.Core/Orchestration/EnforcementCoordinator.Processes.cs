using System.Threading.Channels;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;

namespace Blocker.Core.Orchestration;

// Process pipeline: monitor events -> bounded queue -> single consumer -> policy -> terminate / stub / event.
public sealed partial class EnforcementCoordinator
{
    /// <summary>Queue bound. The queue never drops: when full, the monitor thread is blocked (and it is logged).</summary>
    internal const int QueueCapacity = 4096;

    private static readonly TimeSpan InterceptorBudget = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StubDedupeWindow = TimeSpan.FromSeconds(3);
    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    private readonly string[] _baselineRoots = BaselineRoots(settings.WindowsDirectory);
    private readonly Lock _stubGate = new();
    private readonly Dictionary<string, long> _stubLaunches = new(StringComparer.Ordinal);

    private readonly record struct WorkItem(ProcessInfo? Started, ProcessKey Exited);

    private readonly record struct BlockDecision(ProcessInfo Process, ProcessVerdict Verdict, ProcessDescriptor Descriptor);

    private readonly record struct BudgetResult<T>(bool TimedOut, T? Value);

    private static string[] BaselineRoots(string windowsDirectory)
    {
        string root = WinPath.Normalize(windowsDirectory);
        return [root + @"\system32", root + @"\syswow64", root + @"\systemapps"];
    }

    /// <summary>
    /// Blocks the process behind a foreground window that a plugin's window interceptor vetoed: terminated and replaced by the block screen,
    /// exactly like a disallowed process. Only while the session is enforced (not during a pass), never for critical or Blocker's own
    /// processes, and never when the image is unknown (it cannot be proven non-critical).
    /// </summary>
    /// <returns><see langword="true"/> when the process was handed to the block pipeline.</returns>
    public async ValueTask<bool> BlockWindowProcessAsync(int processId, long startTimeTicks, string imagePath, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        if (_runtime is not { } runtime || session.Snapshot.Phase != SessionPhase.Active || imagePath.Length == 0 || runtime.Critical.IsCritical(imagePath))
        {
            return false;
        }

        var process = new ProcessInfo(processId, 0, 1, imagePath, startTimeTicks);
        var verdict = new ProcessVerdict(VerdictKind.Block, VerdictReason.PluginInterceptor, reason);
        await BlockAsync(runtime, new BlockDecision(process, verdict, Describe(process, null, null)), graceful: false, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void OnProcessStarted(object? sender, ProcessStartedEventArgs e) => Enqueue(new WorkItem(e.Process, default));

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e) =>
        Enqueue(new WorkItem(null, new ProcessKey(e.ProcessId, e.StartTimeTicks)));

    private void Enqueue(WorkItem item)
    {
        if (_runtime is not { } runtime)
        {
            return;
        }

        runtime.Work.Add();
        try
        {
            if (runtime.Queue.Writer.TryWrite(item))
            {
                return;
            }

            LogQueueFull(logger);
            runtime.Queue.Writer.WriteAsync(item, runtime.Stop.Token).AsTask().GetAwaiter().GetResult(); // security: never drop, block the monitor instead
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or ObjectDisposedException)
        {
            runtime.Work.Done();
        }
    }

    private async Task ConsumeAsync(SessionRuntime runtime)
    {
        CancellationToken stop = runtime.Stop.Token;
        try
        {
            while (await runtime.Queue.Reader.WaitToReadAsync(stop).ConfigureAwait(false))
            {
                while (runtime.Queue.Reader.TryRead(out WorkItem item))
                {
                    try
                    {
                        await HandleItemAsync(runtime, item, stop).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogProcessFailed(logger, ex, item.Started?.ProcessId ?? item.Exited.ProcessId);
                    }
                    finally
                    {
                        runtime.Work.Done();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogLoopStopped(logger, "Process pipeline");
        }
    }

    private async ValueTask HandleItemAsync(SessionRuntime runtime, WorkItem item, CancellationToken cancellationToken)
    {
        if (item.Started is not { } process)
        {
            runtime.Tree.MarkExited(item.Exited.ProcessId, item.Exited.StartTimeTicks);
            return;
        }

        if (await DecideAsync(runtime, process, force: false, cancellationToken).ConfigureAwait(false) is { } decision)
        {
            await BlockAsync(runtime, decision, graceful: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enforces over every running process, parents first so inheritance resolves. With <paramref name="graceful"/> (after a pass) the
    /// disallowed processes are asked to close in parallel (bounded by <see cref="GracefulCloseBudget"/>) before they are terminated.
    /// </summary>
    private async ValueTask EnforceRunningAsync(SessionRuntime runtime, bool graceful, CancellationToken cancellationToken)
    {
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

        List<BlockDecision> blocked = [];
        foreach (ProcessInfo process in running.OrderBy(static p => p.StartTimeTicks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await DecideAsync(runtime, process, force: true, cancellationToken).ConfigureAwait(false) is { } decision)
                {
                    blocked.Add(decision);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogProcessFailed(logger, ex, process.ProcessId);
            }
        }

        if (graceful)
        {
            await Task.WhenAll(blocked.Select(d => BlockAsync(runtime, d, graceful: true, cancellationToken).AsTask())).ConfigureAwait(false);
            return;
        }

        foreach (BlockDecision decision in blocked)
        {
            await BlockAsync(runtime, decision, graceful: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Decides one process and records it in the tree; returns a decision only when it must be blocked right now.</summary>
    private async ValueTask<BlockDecision?> DecideAsync(SessionRuntime runtime, ProcessInfo process, bool force, CancellationToken cancellationToken)
    {
        SessionPhase phase = session.Snapshot.Phase;
        if (phase == SessionPhase.Idle || (!force && runtime.Tree.TryGetNode(ProcessKey.Of(process), out _)))
        {
            return null;
        }

        if (process.CreatorProcessId is { } creator && creator != process.ParentProcessId)
        {
            LogParentMismatch(logger, process.ProcessId, process.ParentProcessId, creator);
        }

        string image = process.ImagePath ?? "";
        bool trusted = process.SessionId == 0 || runtime.Critical.IsCritical(image);
        string? sha256 = trusted || image.Length == 0 ? null : await HashAsync(image, cancellationToken).ConfigureAwait(false);
        string? package = trusted ? null : TryGetPackage(process.ProcessId);
        var facts = new ProcessFacts(process, sha256, false, package, runtime.Tree.ParentAllowsInheritance(process));
        ProcessVerdict core = runtime.Decision.Decide(facts);
        if (core is { Kind: VerdictKind.Block, Reason: VerdictReason.NotAllowlisted } && IsBaselinePath(image) && IsMicrosoftSigned(image))
        {
            core = runtime.Decision.Decide(facts with { IsMicrosoftSigned = true });
        }

        ProcessDescriptor descriptor = Describe(process, sha256, package);
        ProcessVerdict effective = core;
        if (phase == SessionPhase.EmergencyPass)
        {
            effective = core.IsAllow ? core : new ProcessVerdict(VerdictKind.Allow, VerdictReason.EmergencyPass);
        }
        else if (core.IsAllow && core.Reason is not (VerdictReason.Session0 or VerdictReason.Critical or VerdictReason.BlockerOwn) && interceptor is not null)
        {
            effective = EnforcementPolicy.CombineWithPlugins(core, await RunInterceptorAsync(descriptor, cancellationToken).ConfigureAwait(false));
        }

        // During a pass the tree still records the real verdict, so inheritance is right when enforcement resumes.
        runtime.Tree.Add(process, (phase == SessionPhase.EmergencyPass ? core : effective).AllowsInheritance);
        return effective.IsAllow ? null : new BlockDecision(process, effective, descriptor);
    }

    private async ValueTask BlockAsync(SessionRuntime runtime, BlockDecision decision, bool graceful, CancellationToken cancellationToken)
    {
        ProcessInfo process = decision.Process;
        if (graceful)
        {
            try
            {
                BudgetResult<bool> closed = await WithBudgetAsync(c => _closeGracefully(process.ProcessId, c), GracefulCloseBudget, cancellationToken).ConfigureAwait(false);
                if (closed.TimedOut)
                {
                    LogGracefulCloseTimedOut(logger, process.ProcessId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogStepFailed(logger, ex, "Closing a window gracefully");
            }
        }

        if (!ReferenceEquals(_runtime, runtime))
        {
            return; // the session ended while we were waiting
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

        LogBlocked(logger, decision.Descriptor.ImageName, process.ProcessId, decision.Verdict.Reason, terminated);
        LaunchStub(process, decision.Verdict.Reason);
        try
        {
            await events.PublishAsync(
                new ProcessBlocked(decision.Descriptor, MapReason(decision.Verdict.Reason), null, time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Publishing the block event");
        }
    }

    private void LaunchStub(ProcessInfo process, VerdictReason reason)
    {
        string image = process.ImagePath ?? "";
        string key = image.Length == 0 ? $"pid:{process.ProcessId}" : WinPath.Normalize(image);
        if (!TryReserveStubLaunch(key))
        {
            return;
        }

        try
        {
            string arguments = $"--blocked \"{image.Replace("\"", string.Empty, StringComparison.Ordinal)}\" --reason {reason}";
            if (launcher.LaunchInActiveSession(_settings.StubExecutablePath, arguments) is null)
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
            if (_stubLaunches.Count > 256)
            {
                foreach (string stale in _stubLaunches.Where(p => time.GetElapsedTime(p.Value, now) >= StubDedupeWindow).Select(static p => p.Key).ToList())
                {
                    _ = _stubLaunches.Remove(stale);
                }
            }

            return true;
        }
    }

    private void ForgetStubLaunches()
    {
        lock (_stubGate)
        {
            _stubLaunches.Clear();
        }
    }

    private async ValueTask<string?> HashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await hasher.GetSha256Async(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Hashing an image");
            return null; // fail closed: no hash, no hash-based allow
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

    private bool IsMicrosoftSigned(string image)
    {
        try
        {
            return signatures.IsMicrosoftSigned(image);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Verifying a signature");
            return false;
        }
    }

    private bool IsBaselinePath(string image)
    {
        string path = WinPath.Normalize(image);
        return _baselineRoots.Any(root => WinPath.IsUnder(path, root));
    }

    private async ValueTask<IReadOnlyList<InterceptDecision>> RunInterceptorAsync(ProcessDescriptor descriptor, CancellationToken cancellationToken)
    {
        try
        {
            BudgetResult<IReadOnlyList<InterceptDecision>> result = await WithBudgetAsync(
                c => interceptor!(descriptor, c).AsTask(), InterceptorBudget, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut)
            {
                LogInterceptorTimedOut(logger, descriptor.ProcessId);
            }

            return result.Value ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, ex, "Plugin process interception");
            return [];
        }
    }

    /// <summary>Runs <paramref name="operation"/> for at most <paramref name="budget"/>; on timeout the token is cancelled and the task abandoned.</summary>
    private async Task<BudgetResult<T>> WithBudgetAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<T> work = operation(linked.Token);
        Task first = await Task.WhenAny(work, Task.Delay(budget, time, linked.Token)).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        if (first == work)
        {
            return new BudgetResult<T>(false, await work.ConfigureAwait(false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new BudgetResult<T>(true, default);
    }

    private ProcessDescriptor Describe(ProcessInfo process, string? sha256, string? package)
    {
        string path = process.ImagePath ?? "";
        string name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        DateTimeOffset started = process.StartTimeTicks > 0 && process.StartTimeTicks <= MaxFileTime
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(process.StartTimeTicks), TimeSpan.Zero)
            : time.GetUtcNow();
        return new ProcessDescriptor(process.ProcessId, process.ParentProcessId, name, path, sha256, package, started);
    }

    private static BlockReason MapReason(VerdictReason reason) => reason switch
    {
        VerdictReason.BypassTool => BlockReason.BypassTool,
        VerdictReason.PluginInterceptor => BlockReason.PluginPolicy,
        _ => BlockReason.NotAllowlisted,
    };
}
