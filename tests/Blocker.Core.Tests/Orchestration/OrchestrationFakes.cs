using Blocker.Core.Orchestration;
using Blocker.Core.Ports;
using Blocker.Core.Session;

namespace Blocker.Core.Tests.Orchestration;

internal sealed class FakeProcessMonitor : IProcessMonitor
{
    public event EventHandler<ProcessStartedEventArgs>? ProcessStarted;

    public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    public List<ProcessInfo> Running { get; } = [];

    public int StartCalls { get; private set; }

    public Exception? StartFault { get; set; }

    public Exception? EnumerateFault { get; set; }

    public Action? OnEnumerate { get; set; }

    public int Handlers => (ProcessStarted?.GetInvocationList().Length ?? 0) + (ProcessExited?.GetInvocationList().Length ?? 0);

    private TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    public Exception? Fault { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCalls++;
        if (StartFault is { } fault)
        {
            return Task.FromException(fault);
        }

        if (_completion.Task.IsCompleted)
        {
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously); // a restart
            Fault = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>Event delivery ends (the ETW session died): <see cref="Completion"/> completes.</summary>
    public void Stop(Exception? fault = null)
    {
        Fault = fault;
        _completion.TrySetResult();
    }

    /// <summary>The process starts but the monitor never reports it (a lost ETW event): it only shows up in the snapshot.</summary>
    public void SpawnUnreported(ProcessInfo process) => Running.Add(process);

    public IReadOnlyList<ProcessInfo> EnumerateRunning()
    {
        OnEnumerate?.Invoke();
        return EnumerateFault is { } fault ? throw fault : [.. Running];
    }

    /// <summary>The process starts: it shows up in the snapshot and a start event is raised.</summary>
    public void Spawn(ProcessInfo process)
    {
        Running.Add(process);
        ProcessStarted?.Invoke(this, new ProcessStartedEventArgs(process));
    }

    /// <summary>The process exits: it leaves the snapshot and an exit event is raised.</summary>
    public void Exit(ProcessInfo process)
    {
        Running.Remove(process);
        ProcessExited?.Invoke(this, new ProcessExitedEventArgs(process.ProcessId, process.StartTimeTicks));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeProcessController : IProcessController
{
    private readonly List<(int ProcessId, long StartTimeTicks)> _terminated = [];
    private readonly List<int> _closed = [];

    public IReadOnlyList<(int ProcessId, long StartTimeTicks)> Terminated
    {
        get
        {
            lock (_terminated)
            {
                return [.. _terminated];
            }
        }
    }

    public IReadOnlyList<int> GracefullyClosed
    {
        get
        {
            lock (_closed)
            {
                return [.. _closed];
            }
        }
    }

    public Dictionary<int, string> Packages { get; } = [];

    /// <summary>What the system would answer for a pid; a pid that is not listed cannot be resolved (exited or protected).</summary>
    public Dictionary<int, string> Images { get; } = [];

    public string? TryGetImagePath(int processId, long startTimeTicks) => Images.GetValueOrDefault(processId);

    public Exception? TerminateFault { get; set; }

    public bool TryTerminate(int processId, long startTimeTicks)
    {
        if (TerminateFault is { } fault)
        {
            throw fault;
        }

        lock (_terminated)
        {
            _terminated.Add((processId, startTimeTicks));
        }

        return true;
    }

    public Task<bool> TryCloseGracefullyAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_closed)
        {
            _closed.Add(processId);
        }

        return Task.FromResult(true);
    }

    public string? GetPackageFamilyName(int processId) => Packages.GetValueOrDefault(processId);
}

internal sealed class FakeUserSessionLauncher : IUserSessionLauncher
{
    private readonly List<(string Path, string Arguments)> _launches = [];

    public IReadOnlyList<(string Path, string Arguments)> Launches
    {
        get
        {
            lock (_launches)
            {
                return [.. _launches];
            }
        }
    }

    public Exception? Fault { get; set; }

    public int? Result { get; set; } = 4242;

    public int? LaunchInActiveSession(string executablePath, string arguments)
    {
        if (Fault is { } fault)
        {
            throw fault;
        }

        lock (_launches)
        {
            _launches.Add((executablePath, arguments));
        }

        return Result;
    }
}

internal sealed class FakeSignatureVerifier : ISignatureVerifier
{
    private readonly List<string> _calls = [];

    public HashSet<string> Signed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public bool IsMicrosoftSigned(string path)
    {
        lock (_calls)
        {
            _calls.Add(path);
        }

        return Signed.Contains(path);
    }
}

internal sealed class FakeBrowserFirewall(List<string> log) : IBrowserFirewall
{
    public bool IsEnabled { get; private set; }

    public Exception? EnableFault { get; set; }

    public Exception? DisableFault { get; set; }

    public List<IReadOnlyList<string>> EnabledWith { get; } = [];

    /// <summary>The WFP dynamic session vanishes when the service process exits.</summary>
    public void ProcessExit() => IsEnabled = false;

    public Task EnableAsync(IReadOnlyList<string> browserExecutables, CancellationToken cancellationToken)
    {
        if (EnableFault is { } fault)
        {
            return Task.FromException(fault);
        }

        lock (log)
        {
            log.Add("firewall.enable");
        }

        EnabledWith.Add(browserExecutables);
        IsEnabled = true;
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken cancellationToken)
    {
        if (DisableFault is { } fault)
        {
            return Task.FromException(fault);
        }

        lock (log)
        {
            log.Add("firewall.disable");
        }

        IsEnabled = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Plans one <c>fake</c> change per IFEO name, then the PAC port, services and directories, so tests can see exactly what the coordinator passed in.
/// Like the Windows planner, IFEO, service and directory changes are best-effort and the PAC change is required.
/// </summary>
internal sealed class FakePlanner : IEnforcementChangePlanner
{
    public List<SessionPlanInput> Inputs { get; } = [];

    public Exception? Fault { get; set; }

    public IReadOnlyList<PlannedChange> PlanSessionChanges(SessionPlanInput input)
    {
        if (Fault is { } fault)
        {
            throw fault;
        }

        Inputs.Add(input);
        List<PlannedChange> plan = [];
        plan.AddRange(input.IfeoImageNames.Select(static n => Change($"ifeo:{n}", bestEffort: true)));
        plan.Add(Change($"pac:{input.ProxyPort}", bestEffort: false));
        plan.AddRange(input.ServiceNames.Select(static n => Change($"service:{n}", bestEffort: true)));
        plan.AddRange(input.ProtectedDirectories.Select(static d => Change($"dir:{d}", bestEffort: true)));
        return plan;
    }

    private static PlannedChange Change(string target, bool bestEffort) =>
        new("fake", target, $$"""{"target":"{{target.Replace("\\", "\\\\", StringComparison.Ordinal)}}"}""", bestEffort);
}

/// <summary>An in-memory session store whose writes can be made to fail.</summary>
internal sealed class FaultySessionStore : ISessionStore
{
    private readonly InMemorySessionStore _inner = new();

    public Exception? SaveFault { get; set; }

    public ValueTask<PersistedSession?> LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);

    public ValueTask SaveAsync(PersistedSession session, CancellationToken cancellationToken = default) =>
        SaveFault is { } fault ? throw fault : _inner.SaveAsync(session, cancellationToken);

    public ValueTask ClearAsync(CancellationToken cancellationToken = default) => _inner.ClearAsync(cancellationToken);
}
