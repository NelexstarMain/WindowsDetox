using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Blocker.Contracts;
using Blocker.Core.AppRules;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Core.Security;
using Blocker.Core.Tests.Clock;
using Blocker.Core.Tests.Orchestration;
using Blocker.Core.Tests.Security;
using Blocker.Sdk.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.AppRules;

/// <summary>A hasher that records what it was asked to hash and can be told to hang until its caller gives up.</summary>
internal sealed class RecordingHasher(IFileSystem fileSystem) : FileHasher(fileSystem)
{
    private readonly Lock _gate = new();
    private readonly List<(string Path, long MaxBytes)> _calls = [];

    public IReadOnlyList<(string Path, long MaxBytes)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Gets or sets whether hashing never completes on its own (a stalled disk or share).</summary>
    public bool Hang { get; set; }

    /// <summary>Completes when a hashing call started to hang.</summary>
    public TaskCompletionSource HangStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<string?> GetSha256Async(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _calls.Add((path, maxBytes));
        }

        if (Hang)
        {
            _ = HangStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return await base.GetSha256Async(path, maxBytes, cancellationToken);
    }
}

/// <summary>
/// The app rule enforcer over fakes. One clock drives the timers and the rule time (Monday 2026-11-23 10:00 UTC, zone UTC). The enforcer's tick
/// loop is not started: tests advance the clock and tick by hand, so what a tick sees is exactly what the test arranged.
/// </summary>
internal sealed class AppRuleEnforcerHarness : IAsyncDisposable
{
    public const string Hashed = @"C:\Other\renamed.exe";
    public static readonly byte[] HashedBytes = "an-app-the-rule-knows-by-hash"u8.ToArray();
    public static readonly string Root = MockUnixSupport.Path(@"C:\ProgramData\WindowsDetox");

    private readonly SecureFileStore _store;

    public AppRuleEnforcerHarness()
    {
        Hasher = new RecordingHasher(Fs);
        var protector = new FakeSecretProtector();
        _store = new SecureFileStore(Fs, protector, new HmacKeyStore(Fs, protector, Fs.Path.Combine(Root, "hmac.key")));
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));
        Engine = new AppRuleEngine(
            new AppRulesStore(_store, Fs.Path.Combine(Root, "rules.bin")),
            new AppUsageStore(_store, UsagePath),
            Time.GetUtcNow,
            static () => "UTC");
        Enforcer = new AppRuleEnforcer(
            Engine,
            Monitor,
            Controller,
            Launcher,
            Hasher,
            new CriticalProcesses(@"C:\Windows", InstallDir),
            StubPath,
            Events,
            () => SessionRunning,
            () => AgentConnected,
            Time,
            NullLogger<AppRuleEnforcer>.Instance);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MockFileSystem Fs { get; } = new();

    public RecordingHasher Hasher { get; }

    public FakeTimeProvider Time { get; }

    public FakeProcessMonitor Monitor { get; } = new();

    public FakeProcessController Controller { get; } = new();

    public FakeUserSessionLauncher Launcher { get; } = new();

    public RecordingPublisher Events { get; } = new();

    public AppRuleEngine Engine { get; }

    public AppRuleEnforcer Enforcer { get; }

    public bool SessionRunning { get; set; }

    public bool AgentConnected { get; set; } = true;

    public string UsagePath => Fs.Path.Combine(Root, "usage.bin");

    public IReadOnlyList<ProcessBlocked> BlockedEvents => [.. Events.Events.OfType<ProcessBlocked>()];

    public static AppRuleDto AllWeekBlock(AllowlistEntryDto? app = null) =>
        AppRuleTestData.RuleFor(app ?? AppRuleTestData.Game, null, [.. Enumerable.Range(0, 7).Select(d => AppRuleTestData.Range(d, 0, 1440))]);

    public async Task SetRulesAsync(params AppRuleDto[] rules) => Assert.Empty(await Engine.SetAsync(new AppRulesDto(rules), Ct));

    public async Task SpawnAsync(ProcessInfo process)
    {
        Monitor.Spawn(process);
        await Enforcer.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    /// <summary>Lets <paramref name="by"/> pass, runs one tick and waits for the process pipeline.</summary>
    public async Task TickAfterAsync(TimeSpan by)
    {
        Time.Advance(by);
        await Enforcer.TickOnceAsync(Ct);
        await Enforcer.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    public long UsedSeconds(int rule = 0) => Engine.GetStatus(sessionRunning: false).Items[rule].UsedSeconds;

    public ValueTask<AppUsageState> LoadUsageAsync() => new AppUsageStore(_store, UsagePath).LoadAsync(Ct);

    public ValueTask DisposeAsync() => Enforcer.DisposeAsync();
}
