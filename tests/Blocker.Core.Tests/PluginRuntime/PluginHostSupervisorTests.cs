using Blocker.Core.Tests.TestSupport;
using System.IO.Abstractions.TestingHelpers;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.PluginRuntime;
using Blocker.Core.Tests.Ipc;
using Blocker.Sdk;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.PluginRuntime;

public sealed class PluginHostSupervisorTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly ProcessDescriptor Chrome = new(10, 1, "chrome.exe", "C:\\chrome.exe", null, null, DateTimeOffset.UnixEpoch);
    private static readonly SessionSnapshot Active = new(SessionPhase.Active, Guid.NewGuid(), TimeSpan.FromMinutes(30), null, 2, 2, TimeSpan.FromMinutes(5));

    private static PluginHostSupervisorOptions Patient(PluginHostSupervisorOptions options) => options with
    {
        InterceptTimeout = TimeSpan.FromSeconds(20),
        UiTimeout = TimeSpan.FromSeconds(20),
        NotifyTimeout = TimeSpan.FromSeconds(20),
        InvokeTimeout = TimeSpan.FromSeconds(20),
        LockTimeout = TimeSpan.FromSeconds(20),
    };

    private static Task<T> Soon<T>(Task<T> task) => task.WaitAsync(Spin.Patience, Ct);

    private static Task Soon(Task task) => task.WaitAsync(Spin.Patience, Ct);

    // ---- end to end over real pipes with the real host logic ----

    [Fact]
    public async Task The_supervisor_starts_a_host_that_loads_plugins_and_serves_every_call()
    {
        var fs = new MockFileSystem();
        var store = new PluginStore(fs, "/plugins", "/data");
        await store.InstallAsync(new PluginId(RuntimePlugins.StarterId), await RuntimePlugins.StarterZipAsync(), true, Ct);
        await using var h = new SupervisorHarness(HostBodies.Real(fs), TimeProvider.System, Patient);

        h.Supervisor.Start();
        await Soon(h.Supervisor.WaitForStateAsync(PluginHostState.Running, Ct));

        HostPluginStatusDto plugin = Assert.Single(h.Supervisor.Plugins);
        Assert.Equal((RuntimePlugins.StarterId, HostPluginState.Running), (plugin.PluginId, plugin.State));
        await h.Supervisor.NotifySessionChangedAsync(Active, DateTimeOffset.UnixEpoch, Ct);
        UiContributionDto widget = Assert.Single((await h.Supervisor.RenderSlotAsync(UiSlot.DashboardWidget, Ct)).Items);
        Assert.Equal("widget-0", widget.ContributionId);
        Assert.True((await h.Supervisor.SendUiActionAsync(new SendActionRequest(RuntimePlugins.StarterId, "widget-0", Blocker.UI.Sdk.UiAction.Click("reset")), Ct)).Success);
        Assert.False((await h.Supervisor.SendUiActionAsync(new SendActionRequest(RuntimePlugins.StarterId, "widget-7", Blocker.UI.Sdk.UiAction.Click("reset")), Ct)).Success);
        Assert.Equal(InterceptResponse.Abstain, await h.Supervisor.InterceptProcessAsync(Chrome, Ct));
        Assert.Empty((await h.Supervisor.EvaluateLockAsync(Blocker.Sdk.Extensions.LockAction.UninstallApplication, Ct)).Vetoes);
        await h.Supervisor.PublishEventAsync(new Blocker.Sdk.Events.SessionStarted(Guid.NewGuid(), TimeSpan.FromMinutes(1), DateTimeOffset.UnixEpoch), Ct);
        Assert.Equal(IpcErrorCode.NotFound, (await Assert.ThrowsAsync<IpcException>(() => h.Supervisor.InvokePluginMethodAsync(RuntimePlugins.StarterId, "nope", "{}", Ct).AsTask())).Code);

        await h.Supervisor.DisposeAsync();

        Assert.Equal(PluginHostState.Stopped, h.Supervisor.State);
        Assert.Single(h.Launcher.Processes);
    }

    [Fact]
    public async Task Package_evaluation_runs_in_a_separate_host_that_is_killed_afterwards()
    {
        var fs = new MockFileSystem();
        await using var h = new SupervisorHarness(HostBodies.Real(fs), TimeProvider.System, options => options with { EvaluateTimeout = Spin.Patience });

        PluginInstallReportDto ok = await Soon(h.Supervisor.EvaluatePackageAsync(await RuntimePlugins.StarterZipAsync(), Ct).AsTask());
        PluginInstallReportDto failing = await Soon(h.Supervisor.EvaluatePackageAsync(await RuntimePlugins.FailingStarterZipAsync(), Ct).AsTask());
        PluginInstallReportDto garbage = await Soon(h.Supervisor.EvaluatePackageAsync(new byte[] { 1, 2, 3 }, Ct).AsTask());

        Assert.True(ok.Succeeded);
        Assert.Equal(RuntimePlugins.StarterId, ok.PluginId);
        Assert.Equal("Focus Counter", ok.Name);
        Assert.Contains("events.session", ok.Capabilities);
        Assert.True(ok.TestsTotal > 0 && ok.TestsPassed == ok.TestsTotal);
        Assert.False(failing.Succeeded);
        Assert.Equal(PluginInstallStageDto.Tests, failing.Stage);
        Assert.Contains(failing.Errors, e => e.StartsWith("Failed: FocusCounter.Tests.", StringComparison.Ordinal));
        Assert.Equal(PluginInstallStageDto.Package, garbage.Stage);
        Assert.Equal(3, h.Launcher.Processes.Count);
        Assert.All(h.Launcher.Processes, p => Assert.True(p.Killed));
        Assert.Equal(PluginHostState.Stopped, h.Supervisor.State);
    }

    // ---- scripted hosts on a fake clock ----

    [Fact]
    public async Task A_host_that_keeps_crashing_is_relaunched_five_times_then_plugins_become_unavailable()
    {
        var time = new FakeTimeProvider();
        await using var h = new SupervisorHarness(HostBodies.Scripted(), time);

        h.Supervisor.Start();
        await Spin.AdvanceUntilAsync(time, h.Supervisor.WaitForStateAsync(PluginHostState.Unavailable, Ct), TimeSpan.FromSeconds(1));

        Assert.Equal(6, h.Launcher.Processes.Count);
        Assert.Equal(InterceptResponse.Abstain, await h.Supervisor.InterceptProcessAsync(Chrome, Ct));
        Assert.Empty((await h.Supervisor.EvaluateLockAsync(Blocker.Sdk.Extensions.LockAction.RemovePlugin, Ct)).Vetoes);
        Assert.Empty((await h.Supervisor.RenderSlotAsync(UiSlot.Page, Ct)).Items);
        Assert.False((await h.Supervisor.SendUiActionAsync(new SendActionRequest("com.a.b", "page-0", Blocker.UI.Sdk.UiAction.Click("x")), Ct)).Success);
        Assert.Equal(IpcErrorCode.Internal, (await Assert.ThrowsAsync<IpcException>(() => h.Supervisor.InvokePluginMethodAsync("com.a.b", "m", "{}", Ct).AsTask())).Code);
        Assert.Empty(h.Supervisor.Plugins);
    }

    [Fact]
    public async Task A_host_that_never_registers_is_killed_after_the_registration_timeout()
    {
        var time = new FakeTimeProvider();
        await using var h = new SupervisorHarness(launch => Hang.UntilCancelled(launch.Stop), time);

        h.Supervisor.Start();
        await Spin.UntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            return h.Launcher.Processes is [{ Killed: true }, ..];
        });

        Assert.NotEqual(PluginHostState.Running, h.Supervisor.State);
    }

    [Fact]
    public async Task Registrations_with_an_unknown_or_missing_token_are_refused()
    {
        var time = new FakeTimeProvider();
        await using var h = new SupervisorHarness(HostBodies.Scripted(), time);
        IpcClient client = h.Ipc.NewClient();

        foreach (string? token in new string?[] { null, "bogus" })
        {
            PluginHostRegisterResponse reply = await client.InvokeAsync<PluginHostRegisterRequest, PluginHostRegisterResponse>(
                IpcMethods.PluginHostRegister, new PluginHostRegisterRequest(PluginHostProtocol.Version, token), Ct);
            Assert.False(reply.Accepted);
        }

        Assert.Empty(h.Launcher.Processes);
    }

    [Fact]
    public async Task Interceptors_abstain_on_timeout_and_a_host_that_stops_answering_is_killed_and_relaunched()
    {
        var time = new FakeTimeProvider();
        var body = HostBodies.Scripted(
            client =>
            {
                HostBodies.Answer<LoadPluginsRequest, LoadPluginsResponse>(client, IpcMethods.HostLoadPlugins, (_, _) => Task.FromResult(new LoadPluginsResponse([])));
                HostBodies.Answer<InterceptProcessRequest, InterceptResponse>(client, IpcMethods.HostInterceptProcess, async (_, ct) =>
                {
                    await Hang.UntilCancelled(ct);
                    return InterceptResponse.Abstain;
                });
            },
            (_, stop) => Hang.UntilCancelled(stop));
        await using var h = new SupervisorHarness(body, time);
        h.Supervisor.Start();
        await Soon(h.Supervisor.WaitForStateAsync(PluginHostState.Running, Ct));

        for (int i = 0; i < 5; i++)
        {
            InterceptResponse verdict = await Spin.AdvanceUntilAsync(time, h.Supervisor.InterceptProcessAsync(Chrome, Ct).AsTask(), TimeSpan.FromMilliseconds(50));
            Assert.Equal(InterceptResponse.Abstain, verdict);
        }

        Assert.True(h.Launcher.Processes[0].Killed);
        await Spin.UntilAsync(() =>
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
            return h.Launcher.Processes.Count == 2;
        });
        await Soon(h.Supervisor.WaitForStateAsync(PluginHostState.Running, Ct));
    }

    [Fact]
    public async Task The_latest_session_is_what_a_load_request_carries()
    {
        var time = new FakeTimeProvider();
        List<LoadPluginsRequest> loads = [];
        var body = HostBodies.Scripted(
            client =>
            {
                HostBodies.Answer<LoadPluginsRequest, LoadPluginsResponse>(client, IpcMethods.HostLoadPlugins, (r, _) =>
                {
                    lock (loads)
                    {
                        loads.Add(r);
                    }

                    return Task.FromResult(new LoadPluginsResponse([new HostPluginStatusDto("com.test.p", "P", "1.0.0", HostPluginState.Running, null)]));
                });
                HostBodies.Answer<SessionChangedRequest, EmptyDto>(client, IpcMethods.HostSessionChanged, (_, _) => Task.FromResult(new EmptyDto()));
            },
            (_, stop) => Hang.UntilCancelled(stop));
        await using var h = new SupervisorHarness(body, time);
        h.Supervisor.Start();
        await Soon(h.Supervisor.WaitForStateAsync(PluginHostState.Running, Ct));
        var anchor = new DateTimeOffset(2032, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await h.Supervisor.NotifySessionChangedAsync(Active, anchor, Ct);
        IReadOnlyList<HostPluginStatusDto> plugins = await h.Supervisor.LoadPluginsAsync(Ct);

        Assert.Equal("com.test.p", Assert.Single(plugins).PluginId);
        LoadPluginsRequest last;
        lock (loads)
        {
            Assert.Equal(2, loads.Count);
            Assert.Equal(SessionPhase.Idle, loads[0].Session.Phase);
            last = loads[1];
        }

        Assert.Equal(SessionPhase.Active, last.Session.Phase);
        Assert.Equal(anchor, last.VerifiedUtcNow);
    }

    [Fact]
    public async Task Faults_and_render_requests_reported_by_the_host_are_raised_and_recorded()
    {
        var time = new FakeTimeProvider();
        var report = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = HostBodies.Scripted(
            client => HostBodies.Answer<LoadPluginsRequest, LoadPluginsResponse>(client, IpcMethods.HostLoadPlugins, (_, _) =>
                Task.FromResult(new LoadPluginsResponse([new HostPluginStatusDto("com.test.p", "P", "1.0.0", HostPluginState.Running, null)]))),
            async (client, stop) =>
            {
                await report.Task.WaitAsync(stop);
                await client.NotifyAsync(IpcMethods.PluginHostPluginFaulted, IpcJson.ToElement(new PluginFaultedNotification("com.test.p", "too slow")), stop);
                await client.NotifyAsync(IpcMethods.PluginHostRenderRequested, IpcJson.ToElement(new RenderRequestedNotification("com.test.p", "widget-0")), stop);
                await Hang.UntilCancelled(stop);
            });
        await using var h = new SupervisorHarness(body, time);
        var faulted = new TaskCompletionSource<PluginFaultedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new TaskCompletionSource<RenderRequestedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Supervisor.PluginFaulted += (_, e) => faulted.TrySetResult(e);
        h.Supervisor.RenderRequested += (_, e) => rendered.TrySetResult(e);
        h.Supervisor.Start();
        await Soon(h.Supervisor.WaitForStateAsync(PluginHostState.Running, Ct));

        report.SetResult();

        Assert.Equal(new PluginFaultedNotification("com.test.p", "too slow"), await Soon(faulted.Task));
        Assert.Equal(new RenderRequestedNotification("com.test.p", "widget-0"), await Soon(rendered.Task));
        HostPluginStatusDto plugin = Assert.Single(h.Supervisor.Plugins);
        Assert.Equal((HostPluginState.Faulted, "too slow"), (plugin.State, plugin.Error));
    }
}
