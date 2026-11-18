// Tests run by Blocker BEFORE the plugin is installed: if one fails (or the file is missing), nothing is installed.
// Rules: only [Fact] and [Theory]+[InlineData], no Skip, no real clock (use ctx.FakeTime), no real I/O.
// Every test builds its own FakePluginContext, so tests are independent of each other.

using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;
using Xunit;

namespace FocusCounter.Tests;

/// <summary>Behaviour tests of the Focus Counter plugin.</summary>
public sealed class FocusCounterTests
{
    private const string PluginId = "com.example.focus-counter";

    private static readonly PluginCapability[] AllCapabilities =
    [
        PluginCapability.SessionEvents,
        PluginCapability.ProcessEvents,
        PluginCapability.UiDashboardWidget,
        PluginCapability.UiSettingsSection,
        PluginCapability.UiBlockScreen,
    ];

    private static async Task<FakePluginContext> StartAsync(PluginCapability[]? capabilities = null, Action<FakePluginContext>? arrange = null)
    {
        var ctx = new FakePluginContext(PluginId, capabilities ?? AllCapabilities);
        arrange?.Invoke(ctx);
        await new PluginMain().InitializeAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private static void StartSession(FakePluginContext ctx) =>
        ctx.FakeSession.Current = PluginTestData.ActiveSession(TimeSpan.FromMinutes(30));

    private static ValueTask Block(FakePluginContext ctx) =>
        ctx.PublishAsync(new ProcessBlocked(PluginTestData.Process(), BlockReason.NotAllowlisted, null, ctx.FakeTime.GetUtcNow()), CancellationToken.None);

    private static async Task<string> CounterValueAsync(FakePluginContext ctx)
    {
        UiCard card = Assert.IsType<UiCard>(await ctx.RenderAsync(ctx.GetExtension<IDashboardWidgetPlugin>(), CancellationToken.None));
        return card.Children.OfType<UiStatTile>().Single().Value;
    }

    [Fact]
    public async Task Missing_capability_throws_PluginCapabilityException()
    {
        // events.session is missing, so subscribing to SessionStarted must be rejected.
        PluginCapability[] withoutSession = [PluginCapability.ProcessEvents, PluginCapability.UiDashboardWidget];

        var ex = await Assert.ThrowsAsync<PluginCapabilityException>(() => StartAsync(withoutSession));

        Assert.Equal(PluginCapability.SessionEvents, ex.Capability);
    }

    [Fact]
    public async Task Counts_blocked_processes_during_a_session()
    {
        FakePluginContext ctx = await StartAsync(arrange: StartSession);

        await Block(ctx);
        await Block(ctx);
        await Block(ctx);

        Assert.Equal("3", await CounterValueAsync(ctx));
    }

    [Fact]
    public async Task Ignores_blocks_while_no_session_is_running()
    {
        FakePluginContext ctx = await StartAsync();
        Assert.Equal(SessionPhase.Idle, ctx.FakeSession.Current.Phase);

        await Block(ctx);

        Assert.Equal("0", await CounterValueAsync(ctx));
        Assert.Null(await ctx.Storage.ReadAsync("count", CancellationToken.None));
    }

    [Fact]
    public async Task Counter_is_persisted_and_restored_after_restart()
    {
        FakePluginContext ctx = await StartAsync(arrange: StartSession);
        await Block(ctx);
        await Block(ctx);
        Assert.Equal("2", await ctx.Storage.ReadAsync("count", CancellationToken.None));

        // Simulate a restart: a new plugin instance on top of the same storage.
        var restarted = new PluginMain();
        var ctx2 = new FakePluginContext(PluginId, AllCapabilities);
        await ctx2.Storage.WriteAsync("count", "2", CancellationToken.None);
        await restarted.InitializeAsync(ctx2, CancellationToken.None);

        Assert.Equal("2", await CounterValueAsync(ctx2));
    }

    [Fact]
    public async Task Session_start_resets_the_counter()
    {
        FakePluginContext ctx = await StartAsync(arrange: StartSession);
        await Block(ctx);
        Assert.Equal("1", await CounterValueAsync(ctx));

        await ctx.PublishAsync(new SessionStarted(Guid.NewGuid(), TimeSpan.FromHours(1), ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        Assert.Equal("0", await CounterValueAsync(ctx));
        Assert.Equal("0", await ctx.Storage.ReadAsync("count", CancellationToken.None));
    }

    [Fact]
    public async Task Reset_button_zeroes_the_counter_and_requests_a_render()
    {
        FakePluginContext ctx = await StartAsync(arrange: StartSession);
        await Block(ctx);
        IDashboardWidgetPlugin widget = ctx.GetExtension<IDashboardWidgetPlugin>();
        await ctx.RenderAsync(widget, CancellationToken.None);   // gives the widget its surface
        int before = ctx.RenderRequests;

        await ctx.SendActionAsync(widget, UiAction.Click(FocusWidget.ResetButtonId), CancellationToken.None);

        Assert.Equal("0", await CounterValueAsync(ctx));
        Assert.Equal(before + 1, ctx.RenderRequests);
    }

    [Fact]
    public async Task Toggle_is_persisted_and_rendered()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();

        var invalid = new UiAction(FocusSettings.ToggleId, UiActionKind.Toggle, "maybe", new Dictionary<string, string>());
        await ctx.SendActionAsync(settings, invalid, CancellationToken.None);
        Assert.Null(await ctx.Storage.ReadAsync("show-on-block-screen", CancellationToken.None));   // invalid input is ignored

        await ctx.SendActionAsync(settings, UiAction.Toggle(FocusSettings.ToggleId, false), CancellationToken.None);

        Assert.Equal("false", await ctx.Storage.ReadAsync("show-on-block-screen", CancellationToken.None));
        UiStack tree = Assert.IsType<UiStack>(await ctx.RenderAsync(settings, CancellationToken.None));
        Assert.False(Assert.IsType<UiToggle>(Assert.Single(tree.Children)).IsOn);
    }

    [Theory]
    [InlineData(1, "You resisted 1 distraction")]
    [InlineData(4, "You resisted 4 distractions")]
    public async Task Block_screen_shows_the_count_when_enabled(int blocks, string expected)
    {
        FakePluginContext ctx = await StartAsync(arrange: StartSession);
        for (int i = 0; i < blocks; i++)
        {
            await Block(ctx);
        }

        UiNode tree = await ctx.RenderAsync(ctx.GetExtension<IBlockScreenPlugin>(), CancellationToken.None);

        Assert.Equal(new UiText(expected, UiTextStyle.Subtitle, UiTone.Success), tree);
    }

    [Fact]
    public async Task Block_screen_is_empty_when_disabled()
    {
        FakePluginContext ctx = await StartAsync(arrange: StartSession);
        await Block(ctx);
        await ctx.SendActionAsync(ctx.GetExtension<ISettingsSectionPlugin>(), UiAction.Toggle(FocusSettings.ToggleId, false), CancellationToken.None);

        UiNode tree = await ctx.RenderAsync(ctx.GetExtension<IBlockScreenPlugin>(), CancellationToken.None);

        Assert.Empty(Assert.IsType<UiStack>(tree).Children);
    }

    [Fact]
    public async Task Dashboard_shows_session_progress_only_while_a_session_runs()
    {
        FakePluginContext ctx = await StartAsync();
        IDashboardWidgetPlugin widget = ctx.GetExtension<IDashboardWidgetPlugin>();

        UiCard idle = Assert.IsType<UiCard>(await ctx.RenderAsync(widget, CancellationToken.None));
        Assert.Empty(idle.Children.OfType<UiProgress>());

        StartSession(ctx);
        UiCard active = Assert.IsType<UiCard>(await ctx.RenderAsync(widget, CancellationToken.None));
        UiProgress progress = Assert.Single(active.Children.OfType<UiProgress>());
        Assert.Equal(1800, progress.Value);
        Assert.Equal(1800, progress.Maximum);
        Assert.Equal("Session time left: 30 min", progress.Label);
    }

    [Fact]
    public async Task Dashboard_widget_follows_the_widget_conventions()
    {
        FakePluginContext ctx = await StartAsync(arrange: StartSession);

        UiCard card = Assert.IsType<UiCard>(await ctx.RenderAsync(ctx.GetExtension<IDashboardWidgetPlugin>(), CancellationToken.None));

        // The host draws the title, icon and name; the widget has one primary value and no nested cards or logo.
        Assert.Null(card.Title);
        Assert.Null(card.Subtitle);
        Assert.Null(card.Icon);
        Assert.Single(card.Children.OfType<UiStatTile>());
        Assert.Empty(card.Children.OfType<UiCard>());
        Assert.Empty(card.Children.OfType<UiGlyph>());
    }
}
