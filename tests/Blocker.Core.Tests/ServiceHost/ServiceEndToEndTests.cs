using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Ports;
using Blocker.Core.Security;
using Blocker.Core.ServiceHost;
using Blocker.Core.Supervision;
using Blocker.Core.Tests.Orchestration;
using Blocker.Core.Tests.Security;
using Blocker.Sdk.Events;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocker.Core.Tests.ServiceHost;

/// <summary>Collects <c>events.sessionChanged</c> pushes and lets a test await one that matches.</summary>
internal sealed class PushedStatuses
{
    private readonly Lock _gate = new();
    private readonly List<SessionStatusDto> _seen = [];
    private readonly List<(Func<SessionStatusDto, bool> Predicate, TaskCompletionSource Source)> _waiters = [];

    public PushedStatuses(IpcClient client) => client.NotificationReceived += OnNotification;

    public IReadOnlyList<SessionStatusDto> Seen
    {
        get
        {
            lock (_gate)
            {
                return [.. _seen];
            }
        }
    }

    public Task WaitForAsync(Func<SessionStatusDto, bool> predicate)
    {
        lock (_gate)
        {
            if (_seen.Exists(s => predicate(s)))
            {
                return Task.CompletedTask;
            }

            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((predicate, source));
            return source.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private void OnNotification(object? sender, IpcNotificationEventArgs e)
    {
        if (e.Method != IpcMethods.EventSessionChanged)
        {
            return;
        }

        SessionStatusDto status = IpcJson.FromElement<SessionStatusDto>(e.Parameters);
        lock (_gate)
        {
            _seen.Add(status);
            foreach ((Func<SessionStatusDto, bool> predicate, TaskCompletionSource source) in _waiters.Where(w => w.Predicate(status)).ToList())
            {
                _ = source.TrySetResult();
            }
        }
    }
}

public sealed class ServiceEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SupervisionStatusFile SupervisionOf(ServiceHostHarness h)
    {
        var protector = new FakeSecretProtector();
        var store = new SecureFileStore(h.Fs, protector, new HmacKeyStore(h.Fs, protector, h.Paths.HmacKeyPath));
        return new SupervisionStatusFile(store, h.Paths.SupervisionStatusPath, NullLogger<SupervisionStatusFile>.Instance);
    }

    [Fact]
    public async Task A_session_runs_from_start_through_an_emergency_pass_to_expiry_and_leaves_nothing_behind()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        var pushed = new PushedStatuses(client);
        SubscribeResponse subscription = await client.InvokeAsync<SubscribeResponse>(IpcMethods.EventsSubscribe, Ct);
        Assert.Equal(SessionPhase.Idle, subscription.Status.Phase);

        StartSessionResponse started = await client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct);

        Assert.True(started.Success, string.Join(" ", started.Errors));
        Assert.False(string.IsNullOrWhiteSpace(started.BreakGlassCode));
        SessionStatusDto status = await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);
        Assert.Equal(SessionPhase.Active, status.Phase);
        Assert.Equal(started.SessionId, status.SessionId);
        Assert.Equal(TimeSpan.FromHours(1).TotalSeconds, status.RemainingSeconds);
        Assert.NotEmpty(await h.JournalAsync());
        Assert.True(h.Firewall.IsEnabled);
        Assert.True(await SupervisionOf(h).ShouldSuperviseAsync(Ct));
        Assert.Contains(BlockerServiceNames.Watchdog, h.Installer.Started);

        // A process outside the allowlist is terminated and replaced by the block screen; an allowlisted one is left alone.
        ProcessInfo game = OrchestrationHarness.P(500, OrchestrationHarness.Game);
        ProcessInfo editor = OrchestrationHarness.P(501, OrchestrationHarness.Editor);
        h.Monitor.Spawn(game);
        h.Monitor.Spawn(editor);
        await h.App.Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Contains((game.ProcessId, game.StartTimeTicks), h.Controller.Terminated);
        Assert.DoesNotContain(h.Controller.Terminated, t => t.ProcessId == editor.ProcessId);
        Assert.Contains(h.Launcher.Launches, l => l.Path == OrchestrationHarness.StubPath && l.Arguments.Contains("game.exe", StringComparison.Ordinal));

        // An emergency pass suspends everything and rolls the journal back.
        EmergencyPassResponse pass = await client.InvokeAsync<EmptyDto, EmergencyPassResponse>(IpcMethods.SessionUseEmergencyPass, new EmptyDto(), Ct);
        Assert.Equal(EmergencyPassOutcome.Started, pass.Outcome);
        Assert.Equal(SessionPhase.EmergencyPass, pass.Status.Phase);
        Assert.Empty(await h.JournalAsync());
        Assert.False(h.Firewall.IsEnabled);

        // When the pass runs out, enforcement and every protection come back.
        h.AdvanceSession(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await h.TickAsync();
        Assert.Equal(SessionPhase.Active, h.App.Snapshot.Phase);
        Assert.NotEmpty(await h.JournalAsync());
        Assert.True(h.Firewall.IsEnabled);

        // When the session itself runs out, the journal is empty again and the machine is back to normal.
        h.AdvanceSession(TimeSpan.FromHours(1));
        await h.TickAsync();
        await h.App.SyncStatusAsync(Ct);
        Assert.Equal(SessionPhase.Idle, h.App.Snapshot.Phase);
        Assert.Empty(await h.JournalAsync());
        Assert.False(h.Firewall.IsEnabled);
        Assert.Empty(h.System.Values);
        Assert.False(await SupervisionOf(h).ShouldSuperviseAsync(Ct));
        await pushed.WaitForAsync(s => s.Phase == SessionPhase.Idle);
        Assert.Contains(pushed.Seen, s => s.Phase == SessionPhase.Active);
        Assert.Contains(pushed.Seen, s => s.Phase == SessionPhase.EmergencyPass);
        Assert.Contains(h.Plugins.SessionChanges, s => s.Phase == SessionPhase.Active);
        Assert.Equal(SessionPhase.Idle, h.Plugins.SessionChanges[^1].Phase);
    }

    [Fact]
    public async Task The_agent_closes_windows_gracefully_and_shows_the_toasts_of_the_coordinator()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient agent = await h.ConnectAsync(IpcCallerRole.User | IpcCallerRole.Agent);
        List<CloseProcessRequest> closes = [];
        var passEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = agent.Register(IpcMethods.AgentCloseProcess, (parameters, _) =>
        {
            lock (closes)
            {
                closes.Add(IpcJson.FromElement<CloseProcessRequest>(parameters));
            }

            return ValueTask.FromResult(IpcJson.ToElement(new CloseProcessResponse(true)));
        });
        _ = agent.Register(IpcMethods.AgentNotify, (parameters, ct) =>
        {
            if (IpcJson.FromElement<NotificationDto>(parameters).Title == "Emergency pass ended")
            {
                _ = passEnded.TrySetResult();
            }

            return ValueTask.FromResult(IpcJson.ToElement(new EmptyDto()));
        });
        AgentRegisterResponse registered = await agent.InvokeAsync<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, new AgentRegisterRequest(AgentProtocol.Version, 1), Ct);
        Assert.True(registered.Accepted);
        Assert.True((await agent.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct)).Success);
        ProcessInfo game = OrchestrationHarness.P(500, OrchestrationHarness.Game);
        _ = await agent.InvokeAsync<EmptyDto, EmergencyPassResponse>(IpcMethods.SessionUseEmergencyPass, new EmptyDto(), Ct);
        h.Monitor.Spawn(game); // runs during the pass: allowed for now

        h.AdvanceSession(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await h.TickAsync();

        await passEnded.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        lock (closes)
        {
            CloseProcessRequest close = Assert.Single(closes);
            Assert.Equal(game.ProcessId, close.ProcessId);
            Assert.Equal(game.StartTimeTicks, close.StartTimeTicks);
        }

        Assert.Contains((game.ProcessId, game.StartTimeTicks), h.Controller.Terminated);
    }

    [Fact]
    public async Task A_window_vetoed_by_a_plugin_has_its_process_blocked_and_every_foreground_change_reaches_the_plugins()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient agent = await h.ConnectAsync(IpcCallerRole.User | IpcCallerRole.Agent);
        h.Plugins.WindowVerdict = window => window.ImageName == "game.exe" ? new InterceptResponse(true, "Too distracting", "com.test.sample") : InterceptResponse.Abstain;
        h.Controller.Images[600] = OrchestrationHarness.Game;
        h.Controller.Images[601] = OrchestrationHarness.Explorer;
        Assert.True((await agent.InvokeAsync<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, new AgentRegisterRequest(AgentProtocol.Version, 1), Ct)).Accepted);
        Assert.True((await agent.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct)).Success);

        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(600, 6_000, OrchestrationHarness.Game), Ct);
        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(601, 6_010, OrchestrationHarness.Explorer), Ct);
        _ = await agent.InvokeAsync<IdleChangedDto, EmptyDto>(IpcMethods.AgentIdleChanged, new IdleChangedDto(true, 150), Ct);

        Assert.Contains((600, 6_000L), h.Controller.Terminated);
        Assert.Contains(h.Launcher.Launches, l => l.Arguments.Contains("game.exe", StringComparison.Ordinal));
        _ = await h.Plugins.WaitForEventAsync(e => e is ActiveWindowChanged { Window.ImageName: "game.exe" });
        _ = await h.Plugins.WaitForEventAsync(e => e is ActiveWindowChanged { Window.ImageName: "explorer.exe" });
        UserIdleChanged idle = Assert.IsType<UserIdleChanged>(await h.Plugins.WaitForEventAsync(e => e is UserIdleChanged));
        Assert.True(idle.IsIdle);
        Assert.Equal(TimeSpan.FromSeconds(150), idle.IdleDuration);
        Assert.DoesNotContain(h.Controller.Terminated, t => t.ProcessId == 601); // critical processes are never touched, whatever a plugin says
    }

    [Fact]
    public async Task A_restart_keeps_the_session_its_protections_and_its_allowlist()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        StartSessionResponse started = await client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct);
        Assert.True(started.Success);

        await h.RestartAsync();

        Assert.Equal(SessionPhase.Active, h.App.Snapshot.Phase);
        Assert.Equal(started.SessionId, h.App.Snapshot.SessionId);
        Assert.True(h.Firewall.IsEnabled);
        Assert.NotEmpty(await h.JournalAsync());
        IpcClient again = await h.ConnectAsync(IpcCallerRole.User);
        ProcessInfo game = OrchestrationHarness.P(500, OrchestrationHarness.Game);
        h.Monitor.Spawn(game);
        await h.App.Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Contains((game.ProcessId, game.StartTimeTicks), h.Controller.Terminated);

        // The break-glass code shown at the start still works and takes everything away again.
        Assert.False((await again.InvokeAsync<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, new BreakGlassRequest("AAAAA-AAAAA-AAAAA-AAAAA"), Ct)).Success);
        Assert.True((await again.InvokeAsync<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, new BreakGlassRequest(started.BreakGlassCode!), Ct)).Success);
        Assert.Equal(SessionPhase.Idle, h.App.Snapshot.Phase);
        Assert.Empty(await h.JournalAsync());
        Assert.Empty(h.System.Values);
    }

    [Fact]
    public async Task A_damaged_session_file_does_not_stop_the_service_and_the_leftover_protections_are_rolled_back()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct)).Success);
        Assert.NotEmpty(h.System.Values);
        await h.App.DisposeAsync();
        h.Fs.File.WriteAllBytes(h.Paths.SessionStorePath, [1, 2, 3, 4, 5]);

        h.App = await h.StartAppAsync();

        Assert.Equal(SessionPhase.Idle, h.App.Snapshot.Phase);
        Assert.Empty(h.System.Values);
        Assert.False(h.Fs.File.Exists(h.Paths.SessionStorePath));
    }

    [Fact]
    public async Task Stopping_the_service_does_not_roll_an_active_session_back()
    {
        ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct)).Success);

        await h.DisposeAsync();

        Assert.True(h.Fs.File.Exists(h.Paths.SessionStorePath));
        Assert.NotEmpty(h.System.Values);
    }

    [Fact]
    public async Task The_watchdog_is_kept_alive_while_a_session_runs_and_left_alone_otherwise()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        for (int i = 0; i < 20; i++)
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        Assert.Equal(0, h.Watchdog.Starts);

        Assert.True((await client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct)).Success);
        await h.AdvanceUntilAsync(() => h.Watchdog.Starts == 1);
        h.Watchdog.Die();

        await h.AdvanceUntilAsync(() => h.Watchdog.Starts == 2);
    }
}
