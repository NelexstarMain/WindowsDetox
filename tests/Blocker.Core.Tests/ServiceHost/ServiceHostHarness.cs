using System.IO.Abstractions.TestingHelpers;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Ports;
using Blocker.Core.ServiceHost;
using Blocker.Core.Tests.Clock;
using Blocker.Core.Tests.Ipc;
using Blocker.Core.Tests.Journal;
using Blocker.Core.Tests.Orchestration;
using Blocker.Core.Tests.Security;
using Blocker.Core.Tests.Supervision;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.ServiceHost;

/// <summary>A plugin host whose answers the test scripts; records everything the service sends it.</summary>
internal sealed class FakePluginHost : IPluginHostGateway
{
    private readonly Lock _gate = new();
    private readonly List<IBlockerEvent> _events = [];
    private readonly List<(Func<IBlockerEvent, bool> Predicate, TaskCompletionSource<IBlockerEvent> Source)> _waiters = [];
    private readonly List<SessionSnapshot> _sessionChanges = [];
    private readonly List<(string PluginId, string Method, string Payload)> _invocations = [];
    private int _reloads;

    public IReadOnlyList<HostPluginStatusDto> Plugins { get; set; } = [];

    public Func<LockAction, EvaluateLockResponse> Locks { get; set; } = static _ => new EvaluateLockResponse([]);

    public Func<ReadOnlyMemory<byte>, PluginInstallReportDto> Evaluate { get; set; } = static _ => Ready("com.test.sample");

    public Func<WindowDescriptor, InterceptResponse> WindowVerdict { get; set; } = static _ => InterceptResponse.Abstain;

    public Func<ProcessDescriptor, InterceptResponse> ProcessVerdict { get; set; } = static _ => InterceptResponse.Abstain;

    public Func<SendActionRequest, OperationResponse> OnAction { get; set; } = static _ => new OperationResponse(true, []);

    public Func<string, string, string, string> OnInvoke { get; set; } = static (_, _, _) => "{}";

    public int Reloads => Volatile.Read(ref _reloads);

    public IReadOnlyList<IBlockerEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<SessionSnapshot> SessionChanges
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessionChanges];
            }
        }
    }

    public IReadOnlyList<(string PluginId, string Method, string Payload)> Invocations
    {
        get
        {
            lock (_gate)
            {
                return [.. _invocations];
            }
        }
    }

    public static PluginInstallReportDto Ready(string id) =>
        new(PluginInstallStageDto.Ready, [], id, "Sample", "1.2.3", "Tester", ["tick"], 2, 2, [new("Tests.A", PluginTestOutcomeDto.Passed), new("Tests.B", PluginTestOutcomeDto.Passed)]);

    public ValueTask<IReadOnlyList<HostPluginStatusDto>> ReloadAsync(CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _reloads);
        return ValueTask.FromResult(Plugins);
    }

    public ValueTask<PluginInstallReportDto> EvaluatePackageAsync(ReadOnlyMemory<byte> package, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Evaluate(package));

    public ValueTask<EvaluateLockResponse> EvaluateLockAsync(LockAction action, CancellationToken cancellationToken) => ValueTask.FromResult(Locks(action));

    public ValueTask<GetContributionsResponse> RenderSlotAsync(UiSlot slot, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new GetContributionsResponse(slot, [new UiContributionDto("com.test.sample", "widget-0", "Sample", "{}")]));

    public ValueTask<OperationResponse> SendUiActionAsync(SendActionRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(OnAction(request));

    public ValueTask<string> InvokePluginMethodAsync(string pluginId, string method, string payloadJson, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _invocations.Add((pluginId, method, payloadJson));
        }

        return ValueTask.FromResult(OnInvoke(pluginId, method, payloadJson));
    }

    public ValueTask<InterceptResponse> InterceptProcessAsync(ProcessDescriptor process, CancellationToken cancellationToken) => ValueTask.FromResult(ProcessVerdict(process));

    public ValueTask<InterceptResponse> InterceptWindowAsync(WindowDescriptor window, CancellationToken cancellationToken) => ValueTask.FromResult(WindowVerdict(window));

    public ValueTask NotifySessionChangedAsync(SessionSnapshot snapshot, DateTimeOffset verifiedUtcNow, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sessionChanges.Add(snapshot);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PublishEventAsync(IBlockerEvent evt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _events.Add(evt);
            foreach ((Func<IBlockerEvent, bool> predicate, TaskCompletionSource<IBlockerEvent> source) in _waiters.Where(w => w.Predicate(evt)).ToList())
            {
                _ = source.TrySetResult(evt);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Completes with the first event (already seen or still to come) that matches <paramref name="predicate"/>.</summary>
    public Task<IBlockerEvent> WaitForEventAsync(Func<IBlockerEvent, bool> predicate)
    {
        lock (_gate)
        {
            if (_events.Find(e => predicate(e)) is { } seen)
            {
                return Task.FromResult(seen);
            }

            var source = new TaskCompletionSource<IBlockerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((predicate, source));
            return source.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}

/// <summary>A logger factory whose every logger writes to one list.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public CapturingLogger<object> Logger { get; } = new();

    public ILogger CreateLogger(string categoryName) => Logger;

    public void AddProvider(ILoggerProvider provider)
    {
        // Everything goes to Logger.
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}

internal sealed class FakeServiceInstaller : IServiceInstaller
{
    private readonly List<string> _started = [];

    public IReadOnlyList<string> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public void InstallOrUpdate(string serviceName, string displayName, string executablePath, string arguments)
    {
        // The service never installs itself while running; only EnsureStarted matters here.
    }

    public void Uninstall(string serviceName)
    {
        // See InstallOrUpdate.
    }

    public void EnsureStarted(string serviceName)
    {
        lock (_started)
        {
            _started.Add(serviceName);
        }
    }
}

internal sealed class FakeAppScanner(IReadOnlyList<InstalledApp> apps) : IAppInventoryScanner
{
    public Task<IReadOnlyList<InstalledApp>> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(apps);
}

/// <summary>
/// The whole service composition over fakes: real pipe, real coordinator, journal and stores on an in-memory file system, a fake
/// plugin host and every Windows port faked. <see cref="RestartAsync"/> simulates a service restart over the same persisted state.
/// </summary>
internal sealed class ServiceHostHarness : IAsyncDisposable
{
    private readonly List<IpcClient> _clients = [];
    private readonly bool _devMode;
    private readonly long _anchorTick;

    private ServiceHostHarness(bool devMode)
    {
        _devMode = devMode;
        _anchorTick = Mono.TickCount64;
        Paths = BlockerPaths.Create(MockUnixSupport.Path(@"C:\ProgramData"), MockUnixSupport.Path(@"C:\Program Files\Blocker"));
        Firewall = new FakeBrowserFirewall(Log);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static DateTimeOffset Noon => OrchestrationHarness.Noon;

    public MockFileSystem Fs { get; } = new();

    public FakeSystem System { get; } = new();

    public FakeMonotonicClock Mono { get; } = new(100_000);

    public FakeTimeProvider Time { get; } = new(OrchestrationHarness.Noon);

    public List<string> Log { get; } = [];

    public FakeProcessMonitor Monitor { get; } = new();

    public FakeProcessController Controller { get; } = new();

    public FakeUserSessionLauncher Launcher { get; } = new();

    public FakeSignatureVerifier Signatures { get; } = new();

    public FakeBrowserFirewall Firewall { get; }

    public FakePlanner Planner { get; } = new();

    public FakePluginHost Plugins { get; } = new();

    public FakeServiceInstaller Installer { get; } = new();

    public FakePartner Watchdog { get; } = new();

    public FakeIdentityResolver Identity { get; } = new(IpcCallerRole.User | IpcCallerRole.Agent);

    public string PipeName { get; } = $"blk-svc-{Guid.NewGuid():N}";

    public BlockerPaths Paths { get; }

    public BlockerServiceApp App { get; set; } = null!;

    /// <summary>Gets or sets the logger factory of the next started service.</summary>
    public ILoggerFactory Loggers { get; set; } = NullLoggerFactory.Instance;

    /// <summary>Gets or sets the data root protection of the next started service.</summary>
    public Action<BlockerPaths>? ProtectDataRoot { get; set; }

    public static async Task<ServiceHostHarness> StartAsync(bool devMode = false, Action<ServiceHostHarness>? arrange = null)
    {
        var harness = new ServiceHostHarness(devMode);
        arrange?.Invoke(harness);
        harness.App = await harness.StartAppAsync();
        return harness;
    }

    public Task<BlockerServiceApp> StartAppAsync() =>
        BlockerServiceApp.StartAsync(CreatePorts(), Paths, Loggers, Ct);

    /// <summary>Connects a client with the given roles and waits until the server knows its identity.</summary>
    public async Task<IpcClient> ConnectAsync(IpcCallerRole roles)
    {
        Identity.Roles = roles;
        var client = new IpcClient(PipeName, new CapturingLogger<IpcClient>());
        _clients.Add(client);
        _ = await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);
        return client;
    }

    public void AdvanceSession(TimeSpan by) => Mono.Advance(by);

    /// <summary>Advances the fake time provider in small steps until <paramref name="condition"/> holds (timers register asynchronously).</summary>
    public async Task AdvanceUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100_000 && !condition(); i++)
        {
            Time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        Assert.True(condition(), "The condition was not reached.");
    }

    public ValueTask TickAsync() => App.Coordinator.TickOnceAsync(Ct);

    /// <summary>Stops the service without rolling anything back and starts a new one over the same persisted state.</summary>
    public async Task RestartAsync()
    {
        await DropClientsAsync();
        await App.DisposeAsync();
        Firewall.ProcessExit();
        App = await StartAppAsync();
    }

    public async Task<IReadOnlyList<(string Kind, string Target, Blocker.Core.Journal.JournalEntryState State)>> JournalAsync() =>
        [.. (await App.Journal.GetEntriesAsync(Ct)).Select(static e => (e.Kind, e.Target, e.State))];

    public async ValueTask DisposeAsync()
    {
        await DropClientsAsync();
        await App.DisposeAsync();
    }

    public static StartSessionRequest Request(IReadOnlyList<AllowlistEntryDto>? apps = null, IReadOnlyList<string>? domains = null, int minutes = 60) =>
        new(minutes, 1, 5, false, apps ?? [new AllowlistEntryDto("Editor", OrchestrationHarness.Editor, null, null)], domains ?? ["example.com"]);

    private async Task DropClientsAsync()
    {
        foreach (IpcClient client in _clients)
        {
            await client.DisposeAsync();
        }

        _clients.Clear();
    }

    private BlockerServicePorts CreatePorts() => new()
    {
        FileSystem = Fs,
        SecretProtector = new FakeSecretProtector(),
        ProcessMonitor = Monitor,
        ProcessController = Controller,
        UserSessionLauncher = Launcher,
        SignatureVerifier = Signatures,
        BrowserFirewall = Firewall,
        AppScanner = new FakeAppScanner(OrchestrationHarness.Settings.ScannedApps),
        ChangePlanner = Planner,
        CreateChangeHandlers = installId => [new Journal.FakeHandler(System, installId)],
        ServicePipeFactory = new DefaultPipeListenerFactory(),
        ServiceIdentityResolver = Identity,
        PluginHostGateway = Plugins,
        ServiceInstaller = Installer,
        ServiceControl = Watchdog,
        ProcessWaiter = Watchdog,
        TimeSources = [TimeSource("time-a"), TimeSource("time-b")],
        Clock = Mono,
        Time = Time,
        BootMarker = static () => 5_000,
        Enforcement = OrchestrationHarness.Settings,
        PluginTemplate = static _ => ValueTask.FromResult<byte[]>([1, 2, 3, 4]),
        DevMode = _devMode,
        ServicePipeName = PipeName,
        BreakGlassIterations = 1_000,
        ProtectDataRoot = ProtectDataRoot,
    };

    /// <summary>A network time source that agrees with its peers and advances with the monotonic clock.</summary>
    private FakeTimeSource TimeSource(string name) =>
        new(name, _ => ValueTask.FromResult<DateTimeOffset?>(Noon + TimeSpan.FromMilliseconds(Mono.TickCount64 - _anchorTick)));
}
