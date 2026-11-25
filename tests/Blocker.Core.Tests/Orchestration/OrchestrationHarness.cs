using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.Journal;
using Blocker.Core.Orchestration;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Core.Session;
using Blocker.Core.Tests.Clock;
using Blocker.Core.Tests.Journal;
using Blocker.Core.WebFilter;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.Orchestration;

/// <summary>Everything the coordinator needs, with a fake for every port. <see cref="RestartAsync"/> simulates a service restart over the same persistent stores.</summary>
internal sealed class OrchestrationHarness : IAsyncDisposable
{
    public const string InstallDir = @"C:\Program Files\Blocker";
    public const string StubPath = @"C:\Program Files\Blocker\Blocker.Stub.exe";
    public const string Explorer = @"C:\Windows\explorer.exe";
    public const string Cmd = @"C:\Windows\System32\cmd.exe";
    public const string Notepad = @"C:\Windows\System32\notepad.exe";
    public const string Editor = @"C:\Apps\editor.exe";
    public const string Game = @"C:\Games\game.exe";
    public const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    public const string Edge = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";

    public static readonly DateTimeOffset Noon = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    public static readonly SessionConfig Config = new(TimeSpan.FromHours(1), 2, TimeSpan.FromMinutes(5), false, "UTC");
    public static readonly byte[] HashedBytes = "allowlisted-by-hash"u8.ToArray();
    public static readonly byte[] BypassBytes = "renamed-bypass-binary"u8.ToArray();

    private readonly Func<ProcessDescriptor, CancellationToken, ValueTask<IReadOnlyList<InterceptDecision>>>? _interceptor;
    private readonly Func<int, CancellationToken, Task<bool>>? _closeGracefully;
    private readonly bool _allowAbort;
    private readonly EnforcementSettings _settings;
    private readonly List<EnforcementNotification> _notifications = [];

    public OrchestrationHarness(
        Func<ProcessDescriptor, CancellationToken, ValueTask<IReadOnlyList<InterceptDecision>>>? interceptor = null,
        Func<int, CancellationToken, Task<bool>>? closeGracefully = null,
        bool allowAbort = false,
        EnforcementSettings? settings = null)
    {
        (_interceptor, _closeGracefully, _allowAbort, _settings) = (interceptor, closeGracefully, allowAbort, settings ?? Settings);
        Verified.Utc = Noon;
        Time = new FakeTimeProvider(Noon);
        Firewall = new FakeBrowserFirewall(Log);
        JournalHarness.System.OnApply = target =>
        {
            lock (Log)
            {
                Log.Add($"apply:{target}");
            }
        };
        (Machine, Journal, Coordinator) = Build();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FakeTimeProvider Time { get; }

    public FakeMonotonicClock Mono { get; } = new(100_000);

    public FakeVerifiedClock Verified { get; } = new();

    public RecordingPublisher Events { get; } = new();

    public FaultySessionStore Sessions { get; } = new();

    public InMemorySessionPolicyStore Policies { get; } = new();

    public JournalHarness JournalHarness { get; } = new();

    public List<string> Log { get; } = [];

    public FakeProcessMonitor Monitor { get; } = new();

    public FakeProcessController Controller { get; } = new();

    public FakeUserSessionLauncher Launcher { get; } = new();

    public BlockNoticeSwitch BlockNotice { get; } = new();

    public FakeSignatureVerifier Signatures { get; } = new();

    public FakeBrowserFirewall Firewall { get; }

    public FakePlanner Planner { get; } = new();

    public List<WebFilterProxy> Proxies { get; } = [];

    public List<IWebFilterPolicy> ProxyPolicies { get; } = [];

    public MockFileSystem Fs => JournalHarness.Fs;

    public SessionStateMachine Machine { get; private set; }

    public RollbackJournal Journal { get; private set; }

    public EnforcementCoordinator Coordinator { get; private set; }

    public IReadOnlyList<EnforcementNotification> Notifications
    {
        get
        {
            lock (_notifications)
            {
                return [.. _notifications];
            }
        }
    }

    public static EnforcementSettings Settings { get; } = new()
    {
        InstallDirectory = InstallDir,
        WindowsDirectory = @"C:\Windows",
        StubExecutablePath = StubPath,
        BrowserExecutables = [Chrome, Edge, @"C:\Tools\evil.exe"],
        BypassToolImageNames = ["taskmgr", "cmd", "services"],
        BypassToolHashes = [Sha(BypassBytes)],
        ScannedApps =
        [
            new InstalledApp("Game", Game, null, null, null, AppSource.StartMenu),
            new InstalledApp("Editor", Editor, null, null, null, AppSource.StartMenu),
        ],
    };

    public static Allowlist DefaultAllowlist { get; } = new(
    [
        new AllowlistEntry("Editor", Editor),
        new AllowlistEntry("Hashed tool", null, Sha(HashedBytes)),
    ]);

    /// <summary>A process in session 1 whose creation time grows with its pid (so parents precede children).</summary>
    public static ProcessInfo P(int pid, string path, int parent = 1, int session = 1) => new(pid, parent, session, path, pid * 1_000L);

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static async Task<bool> IsListeningAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, Ct);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public ValueTask<StartEnforcedSessionResult> StartAsync(IReadOnlyList<string>? domains = null, SessionConfig? config = null, Allowlist? allowlist = null) =>
        Coordinator.StartSessionAsync(config ?? Config, allowlist ?? DefaultAllowlist, domains ?? ["example.com", "*.docs.example.com"], Ct);

    public async Task<IReadOnlyList<(string Kind, string Target, JournalEntryState State)>> EntriesAsync() =>
        [.. (await Journal.GetEntriesAsync(Ct)).Select(static e => (e.Kind, e.Target, e.State))];

    /// <summary>Moves the monotonic and the verified clock; the session clock only advances when ticked.</summary>
    public void AdvanceSession(TimeSpan by)
    {
        Mono.Advance(by);
        Verified.Utc += by;
    }

    public ValueTask TickAsync() => Coordinator.TickOnceAsync(Ct);

    public async Task SpawnAsync(ProcessInfo process)
    {
        Monitor.Spawn(process);
        await Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    /// <summary>Advances the fake time provider in steps until <paramref name="task"/> completes (timers are registered asynchronously).</summary>
    public async Task AdvanceUntilAsync(Task task, TimeSpan step)
    {
        for (int i = 0; i < 100_000 && !task.IsCompleted; i++)
        {
            Time.Advance(step);
            await Task.Yield();
        }

        await task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    public IReadOnlyList<ProcessBlocked> BlockedEvents => [.. Events.Events.OfType<ProcessBlocked>()];

    public static IReadOnlyList<string> Names(IEnumerable<(string Kind, string Target, JournalEntryState State)> entries) => [.. entries.Select(static e => e.Target)];

    /// <summary>Simulates a service restart: the old coordinator stops without rolling back, a new one is built over the same stores.</summary>
    public async Task RestartAsync()
    {
        await Coordinator.DisposeAsync();
        Firewall.ProcessExit();
        (Machine, Journal, Coordinator) = Build();
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        foreach (WebFilterProxy proxy in Proxies)
        {
            await proxy.StopAsync();
        }
    }

    private (SessionStateMachine, RollbackJournal, EnforcementCoordinator) Build()
    {
        var machine = new SessionStateMachine(Sessions, Events, Mono, Verified, () => 5_000, _allowAbort, 1_000);
        RollbackJournal journal = JournalHarness.NewJournal();
        var coordinator = new EnforcementCoordinator(
            machine,
            journal,
            Planner,
            Policies,
            Monitor,
            Controller,
            Launcher,
            Signatures,
            new FileHasher(Fs),
            Firewall,
            (policy, port) =>
            {
                var proxy = new WebFilterProxy(policy, new WebFilterProxyOptions { Port = port });
                Proxies.Add(proxy);
                ProxyPolicies.Add(policy);
                return proxy;
            },
            Events,
            _settings,
            Time,
            NullLogger<EnforcementCoordinator>.Instance,
            _interceptor,
            _closeGracefully,
            BlockNotice);
        coordinator.Notification += (_, n) =>
        {
            lock (_notifications)
            {
                _notifications.Add(n);
            }
        };
        return (machine, journal, coordinator);
    }
}
