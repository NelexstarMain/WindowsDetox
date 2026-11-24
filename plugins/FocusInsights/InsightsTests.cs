// Tests run by Blocker before the plugin is installed. Only [Fact] and [Theory]+[InlineData]; the clock is FakeTime.
// FakePluginContext starts at 2026-01-01T09:00:00Z (a Thursday) with UTC as the local time zone.

using System.IO;
using System.Text.Json;
using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Events;
using Blocker.Sdk.Storage;
using Blocker.UI.Sdk;
using Xunit;

namespace FocusInsights.Tests;

/// <summary>Storage whose writes can be made to fail with a plain <see cref="IOException"/>, like a flaky disk.</summary>
internal sealed class FlakyStorage(InMemoryPluginStorage inner) : IPluginStorage
{
    public bool FailWrites { get; set; }

    public long QuotaBytes => inner.QuotaBytes;

    public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);

    public ValueTask WriteAsync(string key, string value, CancellationToken cancellationToken = default) =>
        FailWrites ? throw new IOException("Disk is unavailable.") : inner.WriteAsync(key, value, cancellationToken);

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);

    public ValueTask<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default) =>
        inner.ListKeysAsync(prefix, cancellationToken);
}

/// <summary>Behaviour tests of the Focus Insights plugin.</summary>
public sealed class InsightsTests
{
    private const string PluginId = "com.windowsdetox.insights";
    private static readonly int Jan1 = new DateOnly(2026, 1, 1).DayNumber;
    private static readonly TimeSpan Thirty = TimeSpan.FromMinutes(30);

    private static readonly PluginCapability[] AllCapabilities =
    [
        PluginCapability.SessionEvents,
        PluginCapability.ProcessEvents,
        PluginCapability.UiPage,
        PluginCapability.UiDashboardWidget,
        PluginCapability.UiSettingsSection,
    ];

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static async Task<FakePluginContext> StartAsync(PluginCapability[]? capabilities = null, Func<FakePluginContext, ValueTask>? arrange = null)
    {
        var ctx = new FakePluginContext(PluginId, capabilities ?? AllCapabilities);
        if (arrange is not null)
        {
            await arrange(ctx);
        }

        await new InsightsPlugin().InitializeAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private static async Task<InsightsStore> NewStoreAsync(FakePluginContext ctx)
    {
        var store = new InsightsStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private static async Task RunSessionAsync(FakePluginContext ctx, TimeSpan planned, TimeSpan actual, SessionEndReason reason = SessionEndReason.Expired)
    {
        Guid id = Guid.NewGuid();
        await ctx.PublishAsync(new SessionStarted(id, planned, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        ctx.FakeTime.Advance(actual);
        await ctx.PublishAsync(new SessionEnded(id, reason, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
    }

    private static async Task RunSessionAsync(InsightsStore store, FakePluginContext ctx, TimeSpan planned, TimeSpan actual, SessionEndReason reason = SessionEndReason.Expired)
    {
        Guid id = Guid.NewGuid();
        await store.OnSessionStartedAsync(new SessionStarted(id, planned, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        ctx.FakeTime.Advance(actual);
        await store.OnSessionEndedAsync(new SessionEnded(id, reason, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
    }

    private static ValueTask BlockAsync(InsightsStore store, FakePluginContext ctx, string image = "game.exe") =>
        store.OnProcessBlockedAsync(
            new ProcessBlocked(PluginTestData.Process(image), BlockReason.NotAllowlisted, null, ctx.FakeTime.GetUtcNow()),
            CancellationToken.None);

    private static ValueTask BlockAsync(FakePluginContext ctx, string image = "game.exe") =>
        ctx.PublishAsync(
            new ProcessBlocked(PluginTestData.Process(image), BlockReason.NotAllowlisted, null, ctx.FakeTime.GetUtcNow()),
            CancellationToken.None);

    private static async Task<UiPage> RenderPageAsync(FakePluginContext ctx) =>
        Assert.IsType<UiPage>(await ctx.RenderAsync(ctx.GetExtension<IUiPagePlugin>(), CancellationToken.None));

    private static async Task<UiCard> RenderWidgetAsync(FakePluginContext ctx) =>
        Assert.IsType<UiCard>(await ctx.RenderAsync(ctx.GetExtension<IDashboardWidgetPlugin>(), CancellationToken.None));

    private static IEnumerable<UiNode> Descendants(UiNode node)
    {
        yield return node;
        IReadOnlyList<UiNode> children = node switch
        {
            UiPage page => page.Children,
            UiStack stack => stack.Children,
            UiGrid grid => grid.Children,
            UiCard card => card.Children,
            _ => Array.Empty<UiNode>(),
        };
        foreach (UiNode child in children)
        {
            foreach (UiNode inner in Descendants(child))
            {
                yield return inner;
            }
        }
    }

    private static T Single<T>(UiNode root)
        where T : UiNode => Descendants(root).OfType<T>().Single();

    private static async Task<JsonDocument> StoredAsync(FakePluginContext ctx)
    {
        string? json = await ctx.Storage.ReadAsync(InsightsStore.DataKey, CancellationToken.None);
        return JsonDocument.Parse(Assert.IsType<string>(json));
    }

    private static async Task<int> StoredBlockedAsync(FakePluginContext ctx)
    {
        using JsonDocument doc = await StoredAsync(ctx);
        return doc.RootElement.GetProperty("d").EnumerateArray().Sum(d => d.TryGetProperty("n", out JsonElement n) ? n.GetInt32() : 0);
    }

    // ---- wiring ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_capability_throws_PluginCapabilityException()
    {
        PluginCapability[] withoutProcess = [PluginCapability.SessionEvents, PluginCapability.UiPage];

        var ex = await Assert.ThrowsAsync<PluginCapabilityException>(() => StartAsync(withoutProcess));

        Assert.Equal(PluginCapability.ProcessEvents, ex.Capability);
    }

    [Fact]
    public async Task Registers_page_widget_and_settings_section()
    {
        FakePluginContext ctx = await StartAsync();

        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        Assert.Equal(new UiNavigationItem("Insights", UiIcon.Chart), page.Navigation);
        Assert.Equal(UiWidgetSize.Small, ctx.GetExtension<IDashboardWidgetPlugin>().Size);
        Assert.Equal("Focus Insights", ctx.GetExtension<ISettingsSectionPlugin>().Title);
        Assert.Equal(3, ctx.RegisteredExtensions.Count);
    }

    // ---- recording ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_completed_session_is_stored_in_compact_json()
    {
        FakePluginContext ctx = await StartAsync();

        await RunSessionAsync(ctx, Thirty, Thirty);

        string expected = $$"""{"v":1,"s":[{"t":1767258000,"p":1800,"a":1800,"r":"c"}],"d":[{"d":{{Jan1}},"f":1800,"c":1}]}""";
        Assert.Equal(expected, await ctx.Storage.ReadAsync("data", CancellationToken.None));
    }

    [Fact]
    public async Task Session_start_is_persisted_so_a_restart_does_not_lose_the_session()
    {
        FakePluginContext ctx = await StartAsync();
        Guid id = Guid.NewGuid();
        await ctx.PublishAsync(new SessionStarted(id, Thirty, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        ctx.FakeTime.Advance(Thirty);

        var restarted = new FakePluginContext(PluginId, AllCapabilities);
        await restarted.Storage.WriteAsync("data", Assert.IsType<string>(await ctx.Storage.ReadAsync("data", CancellationToken.None)), CancellationToken.None);
        restarted.FakeTime.Advance(Thirty);
        InsightsStore store = await NewStoreAsync(restarted);
        await store.OnSessionEndedAsync(new SessionEnded(id, SessionEndReason.Expired, restarted.FakeTime.GetUtcNow()), CancellationToken.None);

        InsightsView view = store.GetView();
        Assert.Equal(1800, view.FocusSecondsToday);
        Assert.Equal(1, view.Completed7d);
    }

    [Fact]
    public async Task Break_glass_is_recorded_as_ended_early_with_the_actual_length()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        await RunSessionAsync(store, ctx, TimeSpan.FromHours(1), TimeSpan.FromMinutes(10), SessionEndReason.BreakGlass);

        InsightsView view = store.GetView();
        SessionRecord session = Assert.Single(view.Recent);
        Assert.Equal((3600, 600, SessionOutcome.EndedEarly), (session.Planned, session.Actual, session.Reason));
        Assert.Equal(1, view.OutcomeEndedEarly);
        Assert.Equal(0, view.OutcomeCompleted);
        Assert.Equal(0, view.Completed7d);
        Assert.Equal(600, view.FocusSeconds7d);
    }

    [Fact]
    public async Task Developer_abort_counts_as_other()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        await RunSessionAsync(store, ctx, Thirty, TimeSpan.FromMinutes(5), SessionEndReason.DeveloperAbort);

        InsightsView view = store.GetView();
        Assert.Equal(1, view.OutcomeOther);
        Assert.Equal(SessionOutcome.Other, Assert.Single(view.Recent).Reason);
    }

    [Fact]
    public async Task A_session_end_without_a_matching_start_is_ignored()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        await store.OnSessionEndedAsync(new SessionEnded(Guid.NewGuid(), SessionEndReason.Expired, ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        Assert.False(store.GetView().HasData);
        Assert.Null(await ctx.Storage.ReadAsync("data", CancellationToken.None));
    }

    [Fact]
    public async Task A_session_end_with_another_id_keeps_the_running_session()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);
        Guid id = Guid.NewGuid();
        await store.OnSessionStartedAsync(new SessionStarted(id, Thirty, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        ctx.FakeTime.Advance(Thirty);

        await store.OnSessionEndedAsync(new SessionEnded(Guid.NewGuid(), SessionEndReason.Expired, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        Assert.Empty(store.GetView().Recent);
        await store.OnSessionEndedAsync(new SessionEnded(id, SessionEndReason.Expired, ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        Assert.Equal(1800, Assert.Single(store.GetView().Recent).Actual);
    }

    [Fact]
    public async Task Emergency_passes_are_counted()
    {
        FakePluginContext ctx = await StartAsync();

        await ctx.PublishAsync(new EmergencyPassStarted(Guid.NewGuid(), TimeSpan.FromMinutes(5), 1, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        await ctx.PublishAsync(new EmergencyPassStarted(Guid.NewGuid(), TimeSpan.FromMinutes(5), 0, ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        UiPage page = await RenderPageAsync(ctx);
        Assert.Contains(Descendants(page).OfType<UiText>(), t => t.Text == "Emergency passes used: 2");
    }

    // ---- aggregation ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Focus_minutes_are_summed_per_day()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        await RunSessionAsync(store, ctx, Thirty, Thirty);
        ctx.FakeTime.Advance(TimeSpan.FromDays(1));
        await RunSessionAsync(store, ctx, TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(45));
        await RunSessionAsync(store, ctx, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

        InsightsView view = store.GetView();
        Assert.Equal(14, view.DailySeconds14.Count);
        Assert.Equal(3600, view.DailySeconds14[13]);
        Assert.Equal(1800, view.DailySeconds14[12]);
        Assert.Equal(0, view.DailySeconds14[11]);
        Assert.Equal(3600, view.FocusSecondsToday);
        Assert.Equal(5400, view.FocusSeconds7d);
        Assert.Equal(3, view.Completed7d);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("0", 1)]
    [InlineData("0,1,2", 3)]
    [InlineData("1,2", 2)]
    [InlineData("0,2", 1)]
    [InlineData("2,3", 0)]
    [InlineData("0,1,2,3,4,5,6,7", 8)]
    public async Task Streak_counts_days_in_a_row_with_a_completed_session(string daysAgo, int expected)
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);
        int[] withSession = daysAgo.Length == 0 ? [] : [.. daysAgo.Split(',').Select(d => int.Parse(d))];

        for (int ago = withSession.Length == 0 ? 0 : withSession.Max(); ago >= 0; ago--)
        {
            TimeSpan used = TimeSpan.Zero;
            if (withSession.Contains(ago))
            {
                await RunSessionAsync(store, ctx, Thirty, Thirty);
                used = Thirty;
            }

            if (ago > 0)
            {
                ctx.FakeTime.Advance(TimeSpan.FromDays(1) - used);
            }
        }

        InsightsView view = store.GetView();
        Assert.Equal(expected, view.Streak);
        Assert.False(view.StreakCapped);
    }

    [Fact]
    public async Task A_day_with_only_an_early_end_does_not_extend_the_streak()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);
        await RunSessionAsync(store, ctx, Thirty, TimeSpan.FromMinutes(5), SessionEndReason.BreakGlass);

        Assert.Equal(0, store.GetView().Streak);
    }

    [Fact]
    public async Task A_blocked_attempt_lands_in_its_weekday_row_and_two_hour_column()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        await BlockAsync(store, ctx);   // Thursday 09:00 UTC

        InsightsView view = store.GetView();
        Assert.Equal(7, view.Heatmap.Count);
        Assert.All(view.Heatmap, row => Assert.Equal(12, row.Count));
        Assert.Equal(1, view.Heatmap[3][4]);
        Assert.Equal(1, view.Heatmap.SelectMany(row => row).Sum());
        Assert.Equal(3, InsightsAggregator.WeekdayRow(Jan1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(9, 4)]
    [InlineData(22, 11)]
    [InlineData(23, 11)]
    public async Task Hours_map_to_two_hour_buckets(int hour, int bucket)
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);
        ctx.FakeTime.Advance(TimeSpan.FromHours((hour - 9 + 24) % 24));

        await BlockAsync(store, ctx);

        InsightsView view = store.GetView();
        Assert.Equal(1, Enumerable.Range(0, 7).Sum(row => view.Heatmap[row][bucket]));
        Assert.Equal(1, view.Heatmap.SelectMany(row => row).Sum());
    }

    [Fact]
    public async Task Days_and_hours_follow_the_local_time_zone()
    {
        FakePluginContext ctx = await StartAsync(arrange: c =>
        {
            c.FakeTime.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test-minus-10", TimeSpan.FromHours(-10), "UTC-10", "UTC-10"));
            return ValueTask.CompletedTask;
        });
        InsightsStore store = await NewStoreAsync(ctx);

        await BlockAsync(store, ctx);   // 09:00 UTC is 23:00 on Wednesday, December 31 in UTC-10

        InsightsView view = store.GetView();
        Assert.Equal(Jan1 - 1, view.Today);
        Assert.Equal(1, view.Heatmap[2][11]);
    }

    [Fact]
    public async Task Top_apps_are_normalized_counted_and_limited_to_six()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        await BlockAsync(store, ctx, "Game.EXE");
        await BlockAsync(store, ctx, "game.exe");
        await BlockAsync(store, ctx, "GAME.exe");
        await BlockAsync(store, ctx, "chat.exe");
        await BlockAsync(store, ctx, "chat.exe");
        await BlockAsync(store, ctx, "video.exe");
        foreach (string name in new[] { "a.exe", "b.exe", "c.exe", "d.exe", "e.exe" })
        {
            await BlockAsync(store, ctx, name);
        }

        InsightsView view = store.GetView();
        Assert.Equal(6, view.TopApps.Count);
        Assert.Equal(new AppCount("game.exe", 3), view.TopApps[0]);
        Assert.Equal(new AppCount("chat.exe", 2), view.TopApps[1]);
        Assert.Equal(new AppCount("a.exe", 1), view.TopApps[2]);
        Assert.Equal(11, view.Blocked7d);
    }

    [Theory]
    [InlineData("  Spaced.exe  ", "spaced.exe")]
    [InlineData("Tab\tbed.exe", "tabbed.exe")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    public void App_names_are_cleaned(string raw, string? expected) =>
        Assert.Equal(expected, InsightsStore.CleanName(raw));

    [Fact]
    public void Long_app_names_are_truncated()
    {
        string? cleaned = InsightsStore.CleanName(new string('A', 100));

        Assert.Equal(new string('a', InsightsLimits.MaxAppNameLength), cleaned);
    }

    // ---- retention and caps ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(89, 2)]
    [InlineData(90, 1)]
    public async Task Days_older_than_the_retention_window_are_dropped(int daysLater, int expectedStoredBlocks)
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);
        await BlockAsync(store, ctx);

        ctx.FakeTime.Advance(TimeSpan.FromDays(daysLater));
        await BlockAsync(store, ctx);

        Assert.Equal(expectedStoredBlocks, store.GetView().StoredBlocked);
        await store.FlushAsync(CancellationToken.None);
        Assert.Equal(expectedStoredBlocks, await StoredBlockedAsync(ctx));
    }

    [Fact]
    public async Task Only_the_latest_thirty_sessions_are_kept_and_the_table_shows_eight()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        for (int i = 0; i < 35; i++)
        {
            await RunSessionAsync(store, ctx, Thirty, TimeSpan.FromMinutes(10 + i));
            ctx.FakeTime.Advance(TimeSpan.FromMinutes(1));
        }

        InsightsView view = store.GetView();
        Assert.Equal(8, view.Recent.Count);
        Assert.Equal((10 + 34) * 60, view.Recent[0].Actual);
        Assert.Equal((10 + 27) * 60, view.Recent[7].Actual);
        using JsonDocument doc = await StoredAsync(ctx);
        Assert.Equal(InsightsLimits.MaxRecentSessions, doc.RootElement.GetProperty("s").GetArrayLength());
        Assert.Equal(35, view.StoredSessions);
    }

    [Fact]
    public async Task App_names_per_day_are_capped_but_attempts_still_count()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        for (int i = 0; i < 35; i++)
        {
            await BlockAsync(store, ctx, $"app{i:00}.exe");
        }

        await store.FlushAsync(CancellationToken.None);
        using JsonDocument doc = await StoredAsync(ctx);
        JsonElement day = Assert.Single(doc.RootElement.GetProperty("d").EnumerateArray());
        Assert.Equal(35, day.GetProperty("n").GetInt32());
        Assert.Equal(InsightsLimits.MaxAppsPerDay, day.GetProperty("a").EnumerateObject().Count());
    }

    [Fact]
    public async Task Storage_stays_small_after_a_busy_quarter()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);

        for (int day = 0; day < 120; day++)
        {
            await RunSessionAsync(store, ctx, Thirty, Thirty);
            for (int i = 0; i < 20; i++)
            {
                await BlockAsync(store, ctx, $"app{i:00}.exe");
            }

            ctx.FakeTime.Advance(TimeSpan.FromDays(1) - Thirty);
        }

        await store.FlushAsync(CancellationToken.None);
        string json = Assert.IsType<string>(await ctx.Storage.ReadAsync("data", CancellationToken.None));
        Assert.True(json.Length < 60_000, $"Stored {json.Length} characters.");
        Assert.True(ctx.FakeStorage.UsedBytes < ctx.FakeStorage.QuotaBytes / 4);
    }

    // ---- persistence ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Blocked_attempts_are_written_at_most_every_ten_seconds_and_flushed_on_shutdown()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        var plugin = new InsightsPlugin();
        await plugin.InitializeAsync(ctx, CancellationToken.None);

        await BlockAsync(ctx);
        Assert.Equal(1, await StoredBlockedAsync(ctx));
        await BlockAsync(ctx);
        Assert.Equal(1, await StoredBlockedAsync(ctx));   // throttled, still only in memory

        ctx.FakeTime.Advance(InsightsStore.FlushInterval);
        Assert.Equal(2, await StoredBlockedAsync(ctx));   // the pending timer wrote it; no further event was needed
        await BlockAsync(ctx);
        Assert.Equal(2, await StoredBlockedAsync(ctx));   // throttled again

        await BlockAsync(ctx);
        await plugin.ShutdownAsync(CancellationToken.None);
        Assert.Equal(4, await StoredBlockedAsync(ctx));
    }

    [Fact]
    public async Task An_isolated_blocked_attempt_is_written_within_ten_seconds_without_any_further_event()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        await new InsightsPlugin().InitializeAsync(ctx, CancellationToken.None);
        await BlockAsync(ctx);   // the first attempt is written at once
        ctx.FakeTime.Advance(TimeSpan.FromSeconds(3));
        await BlockAsync(ctx);
        Assert.Equal(1, await StoredBlockedAsync(ctx));

        ctx.FakeTime.Advance(InsightsStore.FlushInterval - TimeSpan.FromSeconds(4));
        Assert.Equal(1, await StoredBlockedAsync(ctx));
        ctx.FakeTime.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(2, await StoredBlockedAsync(ctx));
    }

    [Fact]
    public async Task Recorded_data_is_restored_after_a_restart()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        var plugin = new InsightsPlugin();
        await plugin.InitializeAsync(ctx, CancellationToken.None);
        await RunSessionAsync(ctx, Thirty, Thirty);
        await BlockAsync(ctx);
        await plugin.ShutdownAsync(CancellationToken.None);   // blocked attempts are written lazily; shutdown flushes them

        var restarted = new FakePluginContext(PluginId, AllCapabilities);
        await restarted.Storage.WriteAsync("data", Assert.IsType<string>(await ctx.Storage.ReadAsync("data", CancellationToken.None)), CancellationToken.None);
        InsightsView view = (await NewStoreAsync(restarted)).GetView();

        Assert.Equal(1800, view.FocusSecondsToday);
        Assert.Equal(1, view.Blocked7d);
        Assert.Equal(new AppCount("game.exe", 1), Assert.Single(view.TopApps));
    }

    [Fact]
    public async Task Unreadable_data_is_ignored_with_a_warning()
    {
        FakePluginContext ctx = await StartAsync(arrange: c => c.Storage.WriteAsync("data", "{not json", CancellationToken.None));

        UiPage page = await RenderPageAsync(ctx);

        Assert.Contains(Descendants(page).OfType<UiCard>(), c => c.Title == "No data yet");
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task Invalid_values_in_stored_data_are_repaired()
    {
        string json = $$"""{"v":1,"d":[{"d":{{Jan1}},"f":-5,"n":3,"h":[1,2]},{"d":{{Jan1}},"f":99},{"d":5,"f":10}],"s":[{"t":1767258000,"p":10,"a":10,"r":"?"}]}""";
        FakePluginContext ctx = await StartAsync(arrange: c => c.Storage.WriteAsync("data", json, CancellationToken.None));
        InsightsStore store = await NewStoreAsync(ctx);

        InsightsView view = store.GetView();

        Assert.Equal(0, view.FocusSecondsToday);
        Assert.Equal(3, view.Blocked7d);
        Assert.Equal(0, view.Heatmap.SelectMany(row => row).Sum());
        Assert.Empty(view.Recent);
        Assert.Equal(3, view.StoredBlocked);
    }

    // ---- store: newer format, quarantine, clock jumps, failed writes ----------------------------------------------

    [Fact]
    public async Task Data_of_a_newer_version_is_shown_but_never_overwritten()
    {
        string newer = $$"""{"v":2,"d":[{"d":{{Jan1}},"n":3,"zz":"unknown"}],"extra":true}""";
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        await ctx.Storage.WriteAsync("data", newer, CancellationToken.None);
        InsightsStore store = await NewStoreAsync(ctx);

        Assert.Equal(3, store.GetView().Blocked7d);
        await BlockAsync(store, ctx);
        await RunSessionAsync(store, ctx, Thirty, Thirty);
        await store.SetRecordNamesAsync(false, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);
        await store.ClearAsync(CancellationToken.None);

        Assert.Equal(newer, await ctx.Storage.ReadAsync("data", CancellationToken.None));
        Assert.Null(await ctx.Storage.ReadAsync("record-names", CancellationToken.None));
        Assert.Single(ctx.FakeLogger.Entries, e => e.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task Unreadable_data_is_copied_to_a_recovery_key_before_starting_empty()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        await ctx.Storage.WriteAsync("data", "{not json", CancellationToken.None);

        InsightsStore store = await NewStoreAsync(ctx);
        await BlockAsync(store, ctx);

        Assert.Equal("{not json", await ctx.Storage.ReadAsync(InsightsStore.UnreadableKey, CancellationToken.None));
        Assert.Equal(1, await StoredBlockedAsync(ctx));
    }

    [Fact]
    public async Task The_first_unreadable_copy_is_kept_and_a_later_corruption_does_not_replace_it()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        await ctx.Storage.WriteAsync("data", "{first", CancellationToken.None);
        await NewStoreAsync(ctx);

        await ctx.Storage.WriteAsync("data", "{second", CancellationToken.None);
        await NewStoreAsync(ctx);

        Assert.Equal("{first", await ctx.Storage.ReadAsync(InsightsStore.UnreadableKey, CancellationToken.None));
    }

    [Fact]
    public async Task The_unreadable_copy_is_capped()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        await ctx.Storage.WriteAsync("data", "{" + new string('x', 200_000), CancellationToken.None);

        await NewStoreAsync(ctx);

        string? kept = await ctx.Storage.ReadAsync(InsightsStore.UnreadableKey, CancellationToken.None);
        Assert.Equal(InsightsStore.MaxUnreadableChars, kept?.Length);
    }

    [Fact]
    public async Task A_clock_that_jumps_far_ahead_does_not_delete_the_history_when_loading()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        InsightsStore store = await NewStoreAsync(ctx);
        await BlockAsync(store, ctx);
        await store.FlushAsync(CancellationToken.None);

        ctx.FakeTime.Advance(TimeSpan.FromDays(400));
        InsightsStore restarted = await NewStoreAsync(ctx);
        await BlockAsync(restarted, ctx);

        Assert.Equal(2, restarted.GetView().StoredBlocked);
    }

    [Fact]
    public async Task A_clock_that_jumps_far_ahead_does_not_delete_the_history_on_the_next_event()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);
        await BlockAsync(store, ctx);

        ctx.FakeTime.Advance(TimeSpan.FromDays(400));
        await BlockAsync(store, ctx);

        Assert.Equal(2, store.GetView().StoredBlocked);
    }

    [Fact]
    public async Task History_older_than_the_window_is_pruned_once_the_clock_is_back_to_normal()
    {
        FakePluginContext ctx = await StartAsync();
        InsightsStore store = await NewStoreAsync(ctx);
        await BlockAsync(store, ctx);
        ctx.FakeTime.Advance(TimeSpan.FromDays(400));
        await BlockAsync(store, ctx);

        ctx.FakeTime.Advance(TimeSpan.FromDays(1));
        await BlockAsync(store, ctx);

        Assert.Equal(2, store.GetView().StoredBlocked);
    }

    [Fact]
    public async Task A_failed_write_does_not_throw_and_is_retried_by_the_next_flush()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        var storage = new FlakyStorage(ctx.FakeStorage) { FailWrites = true };
        var store = new InsightsStore(storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);

        await RunSessionAsync(store, ctx, Thirty, Thirty);   // session events write immediately, and that write fails
        await store.FlushAsync(CancellationToken.None);      // still failing: must stay dirty and not throw
        Assert.Null(await ctx.Storage.ReadAsync("data", CancellationToken.None));

        storage.FailWrites = false;
        await store.FlushAsync(CancellationToken.None);

        using JsonDocument doc = await StoredAsync(ctx);
        Assert.Equal(1, doc.RootElement.GetProperty("s").GetArrayLength());
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task A_blocked_attempt_whose_write_failed_is_retried_by_the_timer()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        var storage = new FlakyStorage(ctx.FakeStorage) { FailWrites = true };
        var store = new InsightsStore(storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);
        await BlockAsync(store, ctx);   // written at once, fails, stays dirty
        await BlockAsync(store, ctx);   // throttled: arms the timer
        storage.FailWrites = false;

        ctx.FakeTime.Advance(InsightsStore.FlushInterval);

        Assert.Equal(2, await StoredBlockedAsync(ctx));
    }

    [Fact]
    public async Task A_failed_privacy_setting_write_is_logged_instead_of_thrown()
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        var storage = new FlakyStorage(ctx.FakeStorage) { FailWrites = true };
        var store = new InsightsStore(storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);

        await store.SetRecordNamesAsync(false, CancellationToken.None);

        Assert.False(store.RecordNames);
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Level == PluginLogLevel.Warning);
    }

    // ---- settings -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Turning_off_name_recording_still_counts_attempts_but_stores_no_names()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();

        await ctx.SendActionAsync(settings, UiAction.Toggle(InsightsSettings.RecordNamesToggleId, false), CancellationToken.None);
        await BlockAsync(ctx, "secret-game.exe");

        Assert.Equal("false", await ctx.Storage.ReadAsync("record-names", CancellationToken.None));
        string json = Assert.IsType<string>(await ctx.Storage.ReadAsync("data", CancellationToken.None));
        Assert.DoesNotContain("secret-game", json, StringComparison.Ordinal);
        Assert.Equal(1, await StoredBlockedAsync(ctx));
        UiPage page = await RenderPageAsync(ctx);
        Assert.Empty(Descendants(page).OfType<UiBarChart>());
        Assert.Contains(Descendants(page).OfType<UiText>(), t => t.Text.StartsWith("No app names were recorded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_privacy_setting_survives_a_restart_and_defaults_to_on()
    {
        FakePluginContext fresh = await StartAsync();
        UiNode defaultTree = await fresh.RenderAsync(fresh.GetExtension<ISettingsSectionPlugin>(), CancellationToken.None);
        Assert.True(Single<UiToggle>(defaultTree).IsOn);

        var restarted = new FakePluginContext(PluginId, AllCapabilities);
        await restarted.Storage.WriteAsync("record-names", "false", CancellationToken.None);
        await new InsightsPlugin().InitializeAsync(restarted, CancellationToken.None);

        UiNode tree = await restarted.RenderAsync(restarted.GetExtension<ISettingsSectionPlugin>(), CancellationToken.None);
        Assert.False(Single<UiToggle>(tree).IsOn);
    }

    [Fact]
    public async Task Invalid_settings_actions_are_ignored()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();

        await ctx.SendActionAsync(settings, new UiAction(InsightsSettings.RecordNamesToggleId, UiActionKind.Toggle, "maybe", new Dictionary<string, string>()), CancellationToken.None);
        await ctx.SendActionAsync(settings, UiAction.Click(InsightsSettings.RecordNamesToggleId), CancellationToken.None);
        await ctx.SendActionAsync(settings, UiAction.Toggle("unknown-id", false), CancellationToken.None);

        Assert.Null(await ctx.Storage.ReadAsync("record-names", CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_needs_a_second_click_and_removes_all_data_but_keeps_the_setting()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        await ctx.SendActionAsync(settings, UiAction.Toggle(InsightsSettings.RecordNamesToggleId, false), CancellationToken.None);
        await RunSessionAsync(ctx, Thirty, Thirty);
        await ctx.RenderAsync(settings, CancellationToken.None);   // gives the section its surface
        int before = ctx.RenderRequests;

        await ctx.SendActionAsync(settings, UiAction.Click(InsightsSettings.ClearButtonId), CancellationToken.None);

        Assert.NotNull(await ctx.Storage.ReadAsync("data", CancellationToken.None));   // armed only
        Assert.Equal(before + 1, ctx.RenderRequests);
        Assert.Equal("Clear recorded data? Click again", Single<UiButton>(await ctx.RenderAsync(settings, CancellationToken.None)).Label);

        await ctx.SendActionAsync(settings, UiAction.Click(InsightsSettings.ClearButtonId), CancellationToken.None);

        Assert.Null(await ctx.Storage.ReadAsync("data", CancellationToken.None));
        Assert.Equal("false", await ctx.Storage.ReadAsync("record-names", CancellationToken.None));
        UiNode tree = await ctx.RenderAsync(settings, CancellationToken.None);
        Assert.Equal("Clear recorded data", Single<UiButton>(tree).Label);
        Assert.Contains(Descendants(tree).OfType<UiText>(), t => t.Text == "Nothing is recorded yet.");
        UiPage page = await RenderPageAsync(ctx);
        Assert.Contains(Descendants(page).OfType<UiCard>(), c => c.Title == "No data yet");
    }

    [Fact]
    public async Task The_clear_confirmation_expires()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        await RunSessionAsync(ctx, Thirty, Thirty);

        await ctx.SendActionAsync(settings, UiAction.Click(InsightsSettings.ClearButtonId), CancellationToken.None);
        ctx.FakeTime.Advance(InsightsSettings.ConfirmWindow + TimeSpan.FromSeconds(1));
        await ctx.SendActionAsync(settings, UiAction.Click(InsightsSettings.ClearButtonId), CancellationToken.None);

        Assert.NotNull(await ctx.Storage.ReadAsync("data", CancellationToken.None));
    }

    // ---- rendering ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_shows_every_visualization_for_recorded_data()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, Thirty, Thirty);
        await RunSessionAsync(ctx, Thirty, TimeSpan.FromMinutes(10), SessionEndReason.BreakGlass);
        await BlockAsync(ctx, "game.exe");
        await BlockAsync(ctx, "chat.exe");
        await BlockAsync(ctx, "game.exe");

        UiPage page = await RenderPageAsync(ctx);

        Assert.Equal("Insights", page.Title);
        Assert.True(UiTreeValidator.Validate(page).IsValid);
        Assert.Empty(Descendants(page).OfType<UiGlyph>());
        Assert.DoesNotContain(Descendants(page).OfType<UiText>(), t => t.Text == "Focus Insights");

        UiGrid tiles = Descendants(page).OfType<UiGrid>().First();
        Assert.Equal(4, tiles.Columns);
        UiStatTile[] stats = [.. tiles.Children.Cast<UiStatTile>()];
        Assert.Equal(("Focus time", "40 min"), (stats[0].Label, stats[0].Value));
        Assert.Equal(("Sessions completed", "1"), (stats[1].Label, stats[1].Value));
        Assert.Equal(("Blocked attempts", "3"), (stats[2].Label, stats[2].Value));
        Assert.Equal(("Current streak", "1 day"), (stats[3].Label, stats[3].Value));

        UiLineChart line = Single<UiLineChart>(page);
        UiSeries series = Assert.Single(line.Series);
        Assert.Equal(14, series.Points.Count);
        Assert.Equal(new UiDataPoint("Jan 1", 40), series.Points[13]);
        Assert.Equal(new UiDataPoint("Dec 19", 0), series.Points[0]);

        UiBarChart bars = Single<UiBarChart>(page);
        Assert.Equal([new UiDataPoint("game.exe", 2), new UiDataPoint("chat.exe", 1)], bars.Points);

        UiDonutChart donut = Single<UiDonutChart>(page);
        Assert.Equal("2", donut.CenterValue);
        Assert.Equal([new UiDataPoint("Completed", 1), new UiDataPoint("Ended early", 1)], donut.Slices);

        UiHeatmap heatmap = Single<UiHeatmap>(page);
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], heatmap.RowLabels);
        Assert.Equal(12, heatmap.ColumnLabels.Count);
        Assert.Equal(["0", "", "4", "", "8", "", "12", "", "16", "", "20", ""], heatmap.ColumnLabels);
        Assert.Equal(3, heatmap.Values[3].Sum());

        UiTable table = Single<UiTable>(page);
        Assert.Equal(["Started", "Outcome", "Planned", "Actual"], table.Columns.Select(c => c.Header));
        Assert.Equal([false, false, true, true], table.Columns.Select(c => c.Monospace));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["Jan 1, 09:30", "Ended early", "30 min", "10 min"], table.Rows[0].Cells);
        Assert.Equal(["Jan 1, 09:00", "Completed", "30 min", "30 min"], table.Rows[1].Cells);
    }

    [Fact]
    public async Task The_table_never_shows_more_than_eight_sessions()
    {
        FakePluginContext ctx = await StartAsync();
        for (int i = 0; i < 12; i++)
        {
            await RunSessionAsync(ctx, Thirty, Thirty);
        }

        UiPage page = await RenderPageAsync(ctx);

        Assert.Equal(8, Single<UiTable>(page).Rows.Count);
    }

    [Fact]
    public async Task A_page_with_only_an_emergency_pass_shows_quiet_captions_instead_of_empty_charts()
    {
        FakePluginContext ctx = await StartAsync();
        await ctx.PublishAsync(new EmergencyPassStarted(Guid.NewGuid(), TimeSpan.FromMinutes(5), 1, ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        UiPage page = await RenderPageAsync(ctx);

        Assert.Empty(Descendants(page).OfType<UiDonutChart>());
        Assert.Empty(Descendants(page).OfType<UiHeatmap>());
        Assert.Empty(Descendants(page).OfType<UiBarChart>());
        Assert.Empty(Descendants(page).OfType<UiTable>());
        Assert.Single(Descendants(page).OfType<UiLineChart>());
        Assert.Contains(Descendants(page).OfType<UiText>(), t => t.Text == "No sessions in this period.");
    }

    [Fact]
    public async Task The_empty_state_explains_when_stats_appear_and_offers_the_example()
    {
        FakePluginContext ctx = await StartAsync();

        UiPage page = await RenderPageAsync(ctx);

        Assert.True(UiTreeValidator.Validate(page).IsValid);
        Assert.Empty(Descendants(page).OfType<UiGlyph>());
        UiCard card = Descendants(page).OfType<UiCard>().Single();
        Assert.Equal("No data yet", card.Title);
        Assert.Contains(card.Children.OfType<UiText>(), t => t.Text.Contains("after your first detox session", StringComparison.Ordinal));
        Assert.Equal(new UiButton("insights-example", "Show example", UiButtonVariant.Secondary), Single<UiButton>(page));
        Assert.Empty(Descendants(page).OfType<UiLineChart>());
        Assert.Empty(Descendants(page).OfType<UiStatTile>());
    }

    [Fact]
    public async Task The_example_preview_renders_every_visualization_from_sample_data_without_saving_anything()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin pagePlugin = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(pagePlugin, CancellationToken.None);   // gives the page its surface
        int before = ctx.RenderRequests;

        await ctx.SendActionAsync(pagePlugin, UiAction.Click(InsightsPage.ExampleButtonId), CancellationToken.None);

        Assert.Equal(before + 1, ctx.RenderRequests);
        UiPage page = await RenderPageAsync(ctx);
        Assert.True(UiTreeValidator.Validate(page).IsValid);
        Assert.Contains(Descendants(page).OfType<UiText>(), t => t.Text == "Example data — your own stats appear after your first session.");
        Assert.Equal(new UiButton("insights-example-hide", "Hide example", UiButtonVariant.Secondary), Single<UiButton>(page));
        Assert.Empty(Descendants(page).OfType<UiGlyph>());
        Assert.Equal(4, Descendants(page).OfType<UiGrid>().First().Children.Count);
        Assert.All(Descendants(page).OfType<UiGrid>().First().Children.Cast<UiStatTile>(), t => Assert.True(t.Value.Length <= 7, t.Value));
        Assert.Equal(14, Assert.Single(Single<UiLineChart>(page).Series).Points.Count);
        Assert.NotEmpty(Single<UiBarChart>(page).Points);
        Assert.NotEmpty(Single<UiDonutChart>(page).Slices);
        Assert.Equal(7, Single<UiHeatmap>(page).Values.Count);
        Assert.Equal(8, Single<UiTable>(page).Rows.Count);
        Assert.Empty(await ctx.Storage.ListKeysAsync(null, CancellationToken.None));
        Assert.DoesNotContain(Descendants(page).OfType<UiCard>(), c => c.Title == "No data yet");
    }

    [Fact]
    public async Task The_example_is_deterministic_and_hide_returns_to_the_empty_state()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin pagePlugin = ctx.GetExtension<IUiPagePlugin>();
        await ctx.SendActionAsync(pagePlugin, UiAction.Click(InsightsPage.ExampleButtonId), CancellationToken.None);

        string first = UiJson.Serialize(await RenderPageAsync(ctx));
        string second = UiJson.Serialize(await RenderPageAsync(ctx));
        var other = new InsightsStore(ctx.Storage, ctx.Time, ctx.Logger);
        InsightsView a = InsightsSampleData.Create(Jan1, TimeZoneInfo.Utc);
        InsightsView b = InsightsSampleData.Create(Jan1, TimeZoneInfo.Utc);

        Assert.Equal(first, second);
        Assert.Equal(other.CurrentDay, a.Today);
        Assert.Equal(a.DailySeconds14, b.DailySeconds14);
        Assert.Equal(a.TopApps, b.TopApps);
        Assert.True(a.Streak >= 5);

        await ctx.SendActionAsync(pagePlugin, UiAction.Click(InsightsPage.HideExampleButtonId), CancellationToken.None);

        UiPage page = await RenderPageAsync(ctx);
        Assert.Contains(Descendants(page).OfType<UiCard>(), c => c.Title == "No data yet");
        Assert.Equal("Show example", Single<UiButton>(page).Label);
    }

    [Fact]
    public async Task Unrelated_page_actions_do_not_switch_to_the_example()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin pagePlugin = ctx.GetExtension<IUiPagePlugin>();

        await ctx.SendActionAsync(pagePlugin, UiAction.Click("other"), CancellationToken.None);
        await ctx.SendActionAsync(pagePlugin, UiAction.Toggle(InsightsPage.ExampleButtonId, true), CancellationToken.None);

        Assert.Equal("Show example", Single<UiButton>(await RenderPageAsync(ctx)).Label);
    }

    [Fact]
    public async Task Real_data_replaces_the_empty_state_and_example_stays_separate()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, Thirty, Thirty);

        UiPage page = await RenderPageAsync(ctx);

        Assert.DoesNotContain(Descendants(page).OfType<UiButton>(), b => b.Label == "Show example");
        Assert.Equal("30 min", Descendants(page).OfType<UiStatTile>().First().Value);
    }

    [Fact]
    public void The_logo_is_valid_glyph_data()
    {
        Assert.True(UiTreeValidator.Validate(InsightsLogo.Glyph(48)).IsValid);
        Assert.True(UiTreeValidator.Validate(InsightsLogo.Glyph(12)).IsValid);
        Assert.True(InsightsLogo.PathData.Length < 4096);
        Assert.All(InsightsLogo.PathData, c => Assert.True(char.IsAsciiDigit(c) || c is ' ' or '.' or '-' || "MLHVAZ".Contains(c, StringComparison.Ordinal)));
    }

    private static void AssertFlatWidget(UiCard card)
    {
        Assert.True(UiTreeValidator.Validate(card).IsValid);
        Assert.Equal((null, null, null), (card.Title, card.Subtitle, card.Icon));
        Assert.Empty(Descendants(card).OfType<UiGlyph>());
        Assert.Single(Descendants(card).OfType<UiCard>());
        Assert.Single(Descendants(card).OfType<UiStatTile>());
        Assert.True(Descendants(card).OfType<UiProgress>().Count() <= 1);
    }

    [Fact]
    public async Task The_widget_shows_only_todays_focus_and_the_week_total()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, Thirty, Thirty);

        UiCard card = await RenderWidgetAsync(ctx);

        AssertFlatWidget(card);
        UiStatTile stat = Single<UiStatTile>(card);
        Assert.Equal(("Focus today", "30 min", "Last 7 days: 30 min"), (stat.Label, stat.Value, stat.Caption));
        Assert.Empty(Descendants(card).OfType<UiBarChart>());
        Assert.Empty(Descendants(card).OfType<UiText>());
    }

    [Fact]
    public async Task The_widget_without_data_shows_a_hint_instead_of_a_subtitle()
    {
        FakePluginContext ctx = await StartAsync();

        UiCard card = await RenderWidgetAsync(ctx);

        AssertFlatWidget(card);
        UiStatTile stat = Single<UiStatTile>(card);
        Assert.Equal(("0 min", null), (stat.Value, stat.Caption));
        Assert.Contains(Descendants(card).OfType<UiText>(), t => t.Text == "Stats appear after your first session.");
    }

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(59, "59 min")]
    [InlineData(60, "1 h 00 min")]
    [InlineData(192, "3 h 12 min")]
    public void Minutes_are_formatted_for_people(int minutes, string expected) =>
        Assert.Equal(expected, InsightsFormat.Minutes(minutes));

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(59, "59 min")]
    [InlineData(60, "1 h")]
    [InlineData(192, "3.2 h")]
    [InlineData(785, "13.1 h")]
    [InlineData(720, "12 h")]
    [InlineData(1445, "24.1 h")]
    public void Compact_durations_fit_a_stat_tile(int minutes, string expected)
    {
        Assert.Equal(expected, InsightsFormat.Compact(minutes));
        Assert.True(expected.Length <= 6);
    }

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(30, "<1 min")]
    [InlineData(1800, "30 min")]
    public void Durations_under_a_minute_are_shown_as_less_than_one_minute(int seconds, string expected) =>
        Assert.Equal(expected, InsightsFormat.Duration(seconds));

    [Theory]
    [InlineData(1, "1 session")]
    [InlineData(0, "0 sessions")]
    [InlineData(2, "2 sessions")]
    public void Counts_are_pluralized(int count, string expected) =>
        Assert.Equal(expected, InsightsFormat.Count(count, "session"));
}
