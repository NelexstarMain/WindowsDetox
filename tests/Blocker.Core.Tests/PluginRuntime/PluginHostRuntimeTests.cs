using Blocker.Contracts;
using Blocker.Core.PluginRuntime;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;

namespace Blocker.Core.Tests.PluginRuntime;

public sealed class PluginHostRuntimeTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly ProcessDescriptor Chrome = new(10, 1, "chrome.exe", "C:\\chrome.exe", null, null, DateTimeOffset.UnixEpoch);
    private static readonly ProcessDescriptor Notepad = Chrome with { ImageName = "notepad.exe" };

    private static readonly SessionSnapshot Running =
        new(SessionPhase.Active, Guid.NewGuid(), TimeSpan.FromMinutes(30), null, 2, 2, TimeSpan.FromMinutes(5));

    private static async Task<RuntimeHarness> LoadedAsync(string id, byte[] zip, PluginRuntimeOptions? options = null)
    {
        var harness = new RuntimeHarness(options);
        await harness.InstallAsync(id, zip);
        await harness.Runtime.LoadAsync(Ct);
        return harness;
    }

    [Fact]
    public async Task The_starter_template_loads_from_its_zip_and_renders_its_dashboard_widget()
    {
        await using var h = await LoadedAsync(RuntimePlugins.StarterId, await RuntimePlugins.StarterZipAsync());
        h.Runtime.UpdateSession(Running, DateTimeOffset.UnixEpoch);

        PluginRuntimeStatus status = Assert.Single(h.Runtime.Statuses);
        Assert.Equal(PluginRuntimeState.Running, status.State);
        Assert.Equal("Focus Counter", status.Info!.Name);

        GetContributionsResponse widgets = await h.Runtime.RenderSlotAsync(UiSlot.DashboardWidget, Ct);
        UiContributionDto widget = Assert.Single(widgets.Items);
        Assert.Equal(RuntimePlugins.StarterId, widget.PluginId);
        Assert.Equal("widget-0", widget.ContributionId);
        UiNode tree = UiJson.Deserialize(widget.Json);
        Assert.True(UiTreeValidator.Validate(tree).IsValid);
        UiCard card = Assert.IsType<UiCard>(tree);
        Assert.Null(card.Title);   // the host draws the plugin icon and name
        Assert.Contains(card.Children, c => c is UiStatTile { Label: "Distractions resisted" });
        Assert.Contains(card.Children, c => c is UiProgress);

        UiContributionDto settings = Assert.Single((await h.Runtime.RenderSlotAsync(UiSlot.SettingsSection, Ct)).Items);
        Assert.Equal("Focus Counter", settings.Title);
        Assert.Single((await h.Runtime.RenderSlotAsync(UiSlot.BlockScreen, Ct)).Items);
        Assert.Empty((await h.Runtime.RenderSlotAsync(UiSlot.Page, Ct)).Items);
    }

    [Fact]
    public async Task Core_events_reach_the_plugin_and_ui_actions_change_its_state()
    {
        await using var h = await LoadedAsync(RuntimePlugins.StarterId, await RuntimePlugins.StarterZipAsync());
        h.Runtime.UpdateSession(Running, DateTimeOffset.UnixEpoch);
        await h.Runtime.PublishEventAsync(new ProcessBlocked(Chrome, BlockReason.NotAllowlisted, null, DateTimeOffset.UnixEpoch), Ct);
        await h.Bus.StopAsync(Ct);

        Assert.Contains("\"count\":\"1\"", h.Fs.File.ReadAllText("/data/com.example.focus-counter/storage.json"), StringComparison.Ordinal);
        string json = Assert.Single((await h.Runtime.RenderSlotAsync(UiSlot.DashboardWidget, Ct)).Items).Json;
        Assert.Contains("\"value\":\"1\"", json, StringComparison.Ordinal);

        Assert.Equal(UiActionResult.Delivered, await h.Runtime.SendUiActionAsync(RuntimePlugins.StarterId, "widget-0", UiAction.Click("reset"), Ct));
        Assert.Contains("\"value\":\"0\"", Assert.Single((await h.Runtime.RenderSlotAsync(UiSlot.DashboardWidget, Ct)).Items).Json, StringComparison.Ordinal);
        Assert.Equal(UiActionResult.Invalid, await h.Runtime.SendUiActionAsync(RuntimePlugins.StarterId, "widget-0", UiAction.Click("bad id!"), Ct));
        Assert.Equal(UiActionResult.NotFound, await h.Runtime.SendUiActionAsync(RuntimePlugins.StarterId, "widget-9", UiAction.Click("reset"), Ct));
        Assert.Equal(UiActionResult.NotFound, await h.Runtime.SendUiActionAsync("com.other.plugin", "widget-0", UiAction.Click("reset"), Ct));
    }

    [Fact]
    public async Task The_plugin_clock_follows_the_verified_anchor()
    {
        await using var h = new RuntimeHarness();
        var anchor = new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero);

        h.Runtime.UpdateSession(Running, anchor);
        h.Time.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(anchor + TimeSpan.FromMinutes(3), h.Clock.GetUtcNow());
    }

    [Fact]
    public async Task A_plugin_without_the_capability_fails_static_analysis_and_is_reported_failed()
    {
        await using var h = new RuntimeHarness();
        await h.InstallAsync("com.test.nocap", RuntimePlugins.Zip("com.test.nocap", [], RuntimePlugins.ThrowingWidget));

        PluginRuntimeStatus status = Assert.Single(await h.Runtime.LoadAsync(Ct));

        Assert.Equal(PluginRuntimeState.Failed, status.State);
        Assert.StartsWith("Policy:", status.Error, StringComparison.Ordinal);
        Assert.Empty((await h.Runtime.RenderSlotAsync(UiSlot.DashboardWidget, Ct)).Items);
    }

    [Fact]
    public async Task The_production_context_refuses_what_static_analysis_could_not_see()
    {
        await using var h = new RuntimeHarness();
        await h.InstallAsync("com.test.sneaky", RuntimePlugins.Zip("com.test.sneaky", [], RuntimePlugins.UndeclaredSubscription));

        PluginRuntimeStatus status = Assert.Single(await h.Runtime.LoadAsync(Ct));

        Assert.Equal(PluginRuntimeState.Failed, status.State);
        Assert.Contains("PluginCapabilityException", status.Error, StringComparison.Ordinal);
        Assert.Contains("events.session", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugin_whose_render_throws_is_faulted_after_the_breaker_trips_and_loses_its_contributions()
    {
        await using var h = new RuntimeHarness();
        await h.InstallAsync("com.test.boom", RuntimePlugins.Zip("com.test.boom", ["ui.dashboard"], RuntimePlugins.ThrowingWidget));
        await h.InstallAsync(RuntimePlugins.StarterId, await RuntimePlugins.StarterZipAsync());
        List<string> faulted = [];
        h.Runtime.PluginFaulted += (_, e) => faulted.Add($"{e.PluginId}: {e.Reason}");
        await h.Runtime.LoadAsync(Ct);

        for (int i = 0; i < 4; i++)
        {
            GetContributionsResponse response = await h.Runtime.RenderSlotAsync(UiSlot.DashboardWidget, Ct);
            Assert.Equal(RuntimePlugins.StarterId, Assert.Single(response.Items).PluginId);
        }

        Assert.Empty(faulted);
        await h.Runtime.RenderSlotAsync(UiSlot.DashboardWidget, Ct);

        Assert.Equal("com.test.boom: 5 consecutive failures (last threw InvalidOperationException).", Assert.Single(faulted));
        Assert.Equal(PluginRuntimeState.Faulted, h.Runtime.Statuses.Single(s => s.Id.Value == "com.test.boom").State);
        Assert.Equal(PluginRuntimeState.Running, h.Runtime.Statuses.Single(s => s.Id.Value == RuntimePlugins.StarterId).State);
        Assert.Equal(RuntimePlugins.StarterId, Assert.Single((await h.Runtime.RenderSlotAsync(UiSlot.DashboardWidget, Ct)).Items).PluginId);
        Assert.Equal(UiActionResult.NotFound, await h.Runtime.SendUiActionAsync("com.test.boom", "widget-0", UiAction.Click("x"), Ct));
    }

    [Fact]
    public async Task An_interceptor_that_blocks_wins_and_everything_else_abstains()
    {
        await using var h = await LoadedAsync("com.test.blocker", RuntimePlugins.Zip("com.test.blocker", ["intercept.process"], RuntimePlugins.BlockingInterceptor));

        InterceptResponse block = await h.Runtime.InterceptProcessAsync(Chrome, Ct);
        InterceptResponse abstain = await h.Runtime.InterceptProcessAsync(Notepad, Ct);

        Assert.Equal(new InterceptResponse(true, "No browsers now.", "com.test.blocker"), block);
        Assert.Equal(InterceptResponse.Abstain, abstain);
    }

    [Fact]
    public async Task An_interceptor_that_times_out_abstains_and_counts_as_a_failure()
    {
        await using var h = await LoadedAsync("com.test.hang", RuntimePlugins.Zip("com.test.hang", ["intercept.process"], RuntimePlugins.HangingInterceptor));

        InterceptResponse response = await Spin.AdvanceUntilAsync(h.Time, h.Runtime.InterceptProcessAsync(Chrome, Ct).AsTask(), TimeSpan.FromMilliseconds(60));

        Assert.Equal(InterceptResponse.Abstain, response);
        Assert.Equal(PluginRuntimeState.Running, Assert.Single(h.Runtime.Statuses).State);
    }

    [Fact]
    public async Task A_plugin_that_keeps_timing_out_is_faulted_and_no_longer_asked()
    {
        await using var h = await LoadedAsync("com.test.hang", RuntimePlugins.Zip("com.test.hang", ["intercept.process"], RuntimePlugins.HangingInterceptor));

        for (int i = 0; i < 5; i++)
        {
            await Spin.AdvanceUntilAsync(h.Time, h.Runtime.InterceptProcessAsync(Chrome, Ct).AsTask(), TimeSpan.FromMilliseconds(60));
        }

        Assert.Equal(PluginRuntimeState.Faulted, Assert.Single(h.Runtime.Statuses).State);
        Assert.Equal(InterceptResponse.Abstain, await h.Runtime.InterceptProcessAsync(Chrome, Ct));
    }

    [Fact]
    public async Task Lock_votes_report_vetoes_and_a_failing_contributor_allows()
    {
        await using var h = await LoadedAsync("com.test.locks", RuntimePlugins.Zip("com.test.locks", ["lockpolicy"], RuntimePlugins.LockContributors));

        EvaluateLockResponse uninstall = await h.Runtime.EvaluateLockAsync(LockAction.UninstallApplication, Ct);
        EvaluateLockResponse pass = await h.Runtime.EvaluateLockAsync(LockAction.UseEmergencyPass, Ct);

        Assert.Equal(new LockVetoDto("com.test.locks", "Stay strong."), Assert.Single(uninstall.Vetoes));
        Assert.Empty(pass.Vetoes);
    }

    [Fact]
    public async Task Tick_listeners_run_per_interval_and_ipc_methods_validate_payloads()
    {
        // The fake clock jumps a second per step; a generous budget keeps those jumps from timing out the callbacks themselves.
        var options = new PluginRuntimeOptions { Breaker = new Blocker.Core.Resilience.CircuitBreakerOptions { Budget = TimeSpan.FromHours(1) } };
        await using var h = await LoadedAsync("com.test.ticks", RuntimePlugins.Zip("com.test.ticks", ["tick", "ipc"], RuntimePlugins.TickAndIpc), options);

        async Task<int> TicksAsync()
        {
            using var reply = System.Text.Json.JsonDocument.Parse(await h.Runtime.InvokePluginMethodAsync("com.test.ticks", "ticks", "{}", Ct));
            return reply.RootElement.GetProperty("ticks").GetInt32();
        }

        // The first tick loop creates its timer asynchronously, so keep advancing until it fires; afterwards one second means exactly one tick.
        await Spin.UntilAsync(async () =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return await TicksAsync() >= 1;
        });
        for (int round = 0; round < 2; round++)
        {
            int before = await TicksAsync();
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await Spin.UntilAsync(async () => await TicksAsync() > before);
        }

        Assert.Equal("{\"a\":1}", await h.Runtime.InvokePluginMethodAsync("com.test.ticks", "echo", "{\"a\":1}", Ct));
        Assert.Equal(IpcErrorCode.NotFound, (await Assert.ThrowsAsync<IpcException>(() => h.Runtime.InvokePluginMethodAsync("com.test.ticks", "nope", "{}", Ct).AsTask())).Code);
        Assert.Equal(IpcErrorCode.NotFound, (await Assert.ThrowsAsync<IpcException>(() => h.Runtime.InvokePluginMethodAsync("com.other.x", "echo", "{}", Ct).AsTask())).Code);
        Assert.Equal(IpcErrorCode.ValidationFailed, (await Assert.ThrowsAsync<IpcException>(() => h.Runtime.InvokePluginMethodAsync("com.test.ticks", "echo", "{oops", Ct).AsTask())).Code);
        string huge = "\"" + new string('x', BlockerSdk.MaxPayloadBytes) + "\"";
        Assert.Equal(IpcErrorCode.PayloadTooLarge, (await Assert.ThrowsAsync<IpcException>(() => h.Runtime.InvokePluginMethodAsync("com.test.ticks", "echo", huge, Ct).AsTask())).Code);
    }

    [Fact]
    public async Task Disabling_a_plugin_unloads_it_and_replacing_its_package_reloads_it()
    {
        await using var h = await LoadedAsync(RuntimePlugins.StarterId, await RuntimePlugins.StarterZipAsync());
        Assert.Single(h.Runtime.Statuses);

        await h.Store.SetEnabledAsync(new PluginId(RuntimePlugins.StarterId), false, Ct);
        await h.Runtime.LoadAsync(Ct);
        Assert.Empty(h.Runtime.Statuses);
        Assert.Empty((await h.Runtime.RenderSlotAsync(UiSlot.DashboardWidget, Ct)).Items);

        await h.Store.SetEnabledAsync(new PluginId(RuntimePlugins.StarterId), true, Ct);
        await h.Runtime.LoadAsync(Ct);
        Assert.Equal(PluginRuntimeState.Running, Assert.Single(h.Runtime.Statuses).State);
    }

    [Fact]
    public async Task Shutdown_calls_shutdown_on_the_plugin_and_unloads_everything()
    {
        await using var h = await LoadedAsync(RuntimePlugins.StarterId, await RuntimePlugins.StarterZipAsync());
        h.Runtime.UpdateSession(Running, DateTimeOffset.UnixEpoch);
        await h.Runtime.PublishEventAsync(new ProcessBlocked(Chrome, BlockReason.NotAllowlisted, null, DateTimeOffset.UnixEpoch), Ct);
        await h.Bus.StopAsync(Ct);

        await h.Runtime.ShutdownAsync(Ct);

        Assert.Empty(h.Runtime.Statuses);
        Assert.Contains("\"show-on-block-screen\":\"true\"", h.Fs.File.ReadAllText("/data/com.example.focus-counter/storage.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_plugin_events_cannot_be_injected_by_the_host()
    {
        await using var h = new RuntimeHarness();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Runtime.PublishEventAsync(new CustomPluginEvent("a.b/x", "a.b", "{}", DateTimeOffset.UnixEpoch), Ct).AsTask());
    }
}
