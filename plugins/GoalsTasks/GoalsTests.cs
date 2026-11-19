// Tests run by Blocker before the plugin is installed. Only [Fact] and [Theory]+[InlineData]; the clock is FakeTime.
// FakePluginContext starts at 2026-01-01T09:00:00Z (a Thursday) with UTC as the local time zone.

using System.Text.Json;
using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.UI.Sdk;
using Xunit;

namespace GoalsTasks.Tests;

/// <summary>Behaviour tests of the Goals &amp; Tasks plugin.</summary>
public sealed class GoalsTests
{
    private const string PluginId = "com.windowsdetox.goals";
    private static readonly int Jan1 = new DateOnly(2026, 1, 1).DayNumber;

    private static readonly PluginCapability[] AllCapabilities =
    [
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

        await new GoalsPlugin().InitializeAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private static async Task<GoalsStore> NewStoreAsync(FakePluginContext ctx)
    {
        var store = new GoalsStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private static async Task AddAsync(GoalsStore store, params string[] texts)
    {
        foreach (string text in texts)
        {
            Assert.True(await store.AddTaskAsync(text, CancellationToken.None));
        }
    }

    private static Task AddViaPageAsync(FakePluginContext ctx, string text) =>
        ctx.SendActionAsync(
            ctx.GetExtension<IUiPagePlugin>(),
            UiAction.Submit(GoalsPage.AddFormId, new Dictionary<string, string> { [GoalsPage.AddFieldId] = text }),
            CancellationToken.None).AsTask();

    private static async Task<UiPage> RenderPageAsync(FakePluginContext ctx) =>
        Assert.IsType<UiPage>(await ctx.RenderAsync(ctx.GetExtension<IUiPagePlugin>(), CancellationToken.None));

    private static async Task<UiCard> RenderWidgetAsync(FakePluginContext ctx) =>
        Assert.IsType<UiCard>(await ctx.RenderAsync(ctx.GetExtension<IDashboardWidgetPlugin>(), CancellationToken.None));

    private static async Task<UiNode> RenderSettingsAsync(FakePluginContext ctx) =>
        await ctx.RenderAsync(ctx.GetExtension<ISettingsSectionPlugin>(), CancellationToken.None);

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

    private static UiStatTile[] Tiles(UiPage page) => [.. Assert.IsType<UiGrid>(page.Children[0]).Children.Cast<UiStatTile>()];

    private static UiButton Button(UiNode root, string id) => Descendants(root).OfType<UiButton>().Single(b => b.Id == id);

    private static bool HasText(UiNode root, string text) => Descendants(root).OfType<UiText>().Any(t => t.Text == text);

    private static async Task<JsonDocument> StoredAsync(FakePluginContext ctx)
    {
        string? json = await ctx.Storage.ReadAsync(GoalsStore.DataKey, CancellationToken.None);
        return JsonDocument.Parse(Assert.IsType<string>(json));
    }

    // ---- wiring ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_capability_throws_PluginCapabilityException()
    {
        PluginCapability[] pageOnly = [PluginCapability.UiPage];

        var ex = await Assert.ThrowsAsync<PluginCapabilityException>(() => StartAsync(pageOnly));

        Assert.Equal(PluginCapability.UiDashboardWidget, ex.Capability);
    }

    [Fact]
    public async Task Registers_page_widget_and_settings_section()
    {
        FakePluginContext ctx = await StartAsync();

        Assert.Equal(new UiNavigationItem("Goals", UiIcon.Target), ctx.GetExtension<IUiPagePlugin>().Navigation);
        Assert.Equal(UiWidgetSize.Medium, ctx.GetExtension<IDashboardWidgetPlugin>().Size);
        Assert.Equal("Goals & Tasks", ctx.GetExtension<ISettingsSectionPlugin>().Title);
        Assert.Equal(3, ctx.RegisteredExtensions.Count);
    }

    // ---- store: tasks ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_added_task_is_stored_in_compact_json()
    {
        FakePluginContext ctx = await StartAsync();

        await AddViaPageAsync(ctx, "Write report");

        string expected = $$"""{"v":1,"n":2,"d":[{"d":{{Jan1}},"t":[{"i":1,"x":"Write report"}]}]}""";
        Assert.Equal(expected, await ctx.Storage.ReadAsync("data", CancellationToken.None));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\t\r\n", null)]
    [InlineData("  Read chapter 3  ", "Read chapter 3")]
    [InlineData("two\nlines", "two lines")]
    public void Task_text_is_trimmed_and_cleaned(string raw, string? expected) =>
        Assert.Equal(expected, GoalsStore.CleanText(raw));

    [Fact]
    public void Task_text_is_limited_to_120_characters_without_splitting_a_surrogate_pair()
    {
        Assert.Null(GoalsStore.CleanText(null));
        Assert.Equal(new string('a', 120), GoalsStore.CleanText(new string('a', 500)));
        string emoji = new string('a', 119) + "\U0001F600\U0001F600";

        string? cleaned = GoalsStore.CleanText(emoji);

        Assert.Equal(new string('a', 119), cleaned);
    }

    [Fact]
    public async Task Adding_an_empty_task_changes_nothing()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);

        Assert.False(await store.AddTaskAsync("  ", CancellationToken.None));

        Assert.Empty(store.GetView().Tasks);
        Assert.Null(await ctx.Storage.ReadAsync("data", CancellationToken.None));
    }

    [Fact]
    public async Task A_day_holds_at_most_fifty_tasks()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        for (int i = 0; i < GoalsLimits.MaxTasksPerDay; i++)
        {
            await AddAsync(store, $"task {i}");
        }

        Assert.False(await store.AddTaskAsync("one too many", CancellationToken.None));

        Assert.Equal(50, store.GetView().Tasks.Count);
        Assert.DoesNotContain(store.GetView().Tasks, t => t.Text == "one too many");
    }

    [Fact]
    public async Task A_task_can_be_marked_done_and_open_again()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        await AddAsync(store, "a", "b");
        int id = store.GetView().Tasks[1].Id;

        Assert.True(await store.SetDoneAsync(id, true, CancellationToken.None));
        Assert.False(await store.SetDoneAsync(id, true, CancellationToken.None));

        GoalsView view = store.GetView();
        Assert.Equal([false, true], view.Tasks.Select(t => t.Done));
        Assert.Equal((1, 1), (view.Done, view.Open));

        Assert.True(await store.SetDoneAsync(id, false, CancellationToken.None));

        Assert.Equal(0, store.GetView().Done);
    }

    [Fact]
    public async Task Unknown_task_ids_are_ignored()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        await AddAsync(store, "a");

        Assert.False(await store.SetDoneAsync(99, true, CancellationToken.None));
        Assert.False(await store.DeleteAsync(99, CancellationToken.None));

        GoalsView view = store.GetView();
        Assert.Equal((1, 0), (view.Tasks.Count, view.Done));
    }

    [Fact]
    public async Task Deleting_removes_only_that_task_and_ids_are_never_reused()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        await AddAsync(store, "a", "b", "c");
        int[] ids = [.. store.GetView().Tasks.Select(t => t.Id)];

        Assert.True(await store.DeleteAsync(ids[2], CancellationToken.None));
        await AddAsync(store, "d");

        GoalsView view = store.GetView();
        Assert.Equal(["a", "b", "d"], view.Tasks.Select(t => t.Text));
        Assert.DoesNotContain(ids[2], view.Tasks.Select(t => t.Id));
        Assert.Equal(3, view.Tasks.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public async Task Clearing_completed_removes_only_done_tasks()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        await AddAsync(store, "a", "b", "c");
        await store.SetDoneAsync(store.GetView().Tasks[0].Id, true, CancellationToken.None);
        await store.SetDoneAsync(store.GetView().Tasks[2].Id, true, CancellationToken.None);

        Assert.True(await store.ClearCompletedAsync(CancellationToken.None));
        Assert.False(await store.ClearCompletedAsync(CancellationToken.None));

        GoalsView view = store.GetView();
        Assert.Equal(["b"], view.Tasks.Select(t => t.Text));
        Assert.Equal(0, view.Done);
    }

    [Fact]
    public async Task Tasks_survive_a_restart()
    {
        FakePluginContext ctx = await StartAsync();
        await AddViaPageAsync(ctx, "a");
        await AddViaPageAsync(ctx, "b");
        await ctx.SendActionAsync(ctx.GetExtension<IUiPagePlugin>(), UiAction.Toggle("goals-done-2", true), CancellationToken.None);

        var restarted = new FakePluginContext(PluginId, AllCapabilities);
        await restarted.Storage.WriteAsync("data", Assert.IsType<string>(await ctx.Storage.ReadAsync("data", CancellationToken.None)), CancellationToken.None);
        GoalsStore store = await NewStoreAsync(restarted);
        await AddAsync(store, "c");

        GoalsView view = store.GetView();
        Assert.Equal(["a", "b", "c"], view.Tasks.Select(t => t.Text));
        Assert.Equal([false, true, false], view.Tasks.Select(t => t.Done));
        Assert.Equal([1, 2, 3], view.Tasks.Select(t => t.Id));
    }

    // ---- store: days ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_day_starts_with_an_empty_list_and_keeps_yesterday_in_the_history()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        await AddAsync(store, "a", "b");
        int yesterdayId = store.GetView().Tasks[0].Id;
        await store.SetDoneAsync(yesterdayId, true, CancellationToken.None);

        ctx.FakeTime.Advance(TimeSpan.FromDays(1));

        GoalsView view = store.GetView();
        Assert.Equal(Jan1 + 1, view.Today);
        Assert.Empty(view.Tasks);
        Assert.Equal([0, 0, 0, 0, 0, 1, 0], view.History);
        Assert.False(await store.SetDoneAsync(yesterdayId, false, CancellationToken.None));
        Assert.False(await store.DeleteAsync(yesterdayId, CancellationToken.None));
        await AddAsync(store, "c");
        Assert.Equal(["c"], store.GetView().Tasks.Select(t => t.Text));
        Assert.Equal(1, store.GetView().History[5]);
    }

    [Fact]
    public async Task The_day_changes_at_local_midnight_not_utc_midnight()
    {
        FakePluginContext ctx = await StartAsync();
        ctx.FakeTime.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("plus-ten", TimeSpan.FromHours(10), "plus-ten", "plus-ten"));
        GoalsStore store = await NewStoreAsync(ctx);   // 09:00 UTC is 19:00 local on Jan 1
        await AddAsync(store, "a");

        ctx.FakeTime.Advance(TimeSpan.FromHours(4));   // 23:00 local: still Jan 1

        Assert.Equal(Jan1, store.CurrentDay);
        Assert.Single(store.GetView().Tasks);

        ctx.FakeTime.Advance(TimeSpan.FromHours(2));   // 01:00 local on Jan 2

        Assert.Equal(Jan1 + 1, store.CurrentDay);
        Assert.Empty(store.GetView().Tasks);
    }

    [Fact]
    public async Task Only_the_last_seven_days_are_kept()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        await AddAsync(store, "old");
        ctx.FakeTime.Advance(TimeSpan.FromDays(6));
        await AddAsync(store, "still kept");
        ctx.FakeTime.Advance(TimeSpan.FromDays(1));
        await AddAsync(store, "new");

        using JsonDocument doc = await StoredAsync(ctx);

        int[] days = [.. doc.RootElement.GetProperty("d").EnumerateArray().Select(d => d.GetProperty("d").GetInt32())];
        Assert.Equal([Jan1 + 6, Jan1 + 7], days);
    }

    [Fact]
    public async Task The_history_counts_done_tasks_of_each_day()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        for (int day = 0; day < 3; day++)
        {
            await AddAsync(store, "a", "b", "c");
            for (int i = 0; i <= day; i++)
            {
                await store.SetDoneAsync(store.GetView().Tasks[i].Id, true, CancellationToken.None);
            }

            ctx.FakeTime.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal([0, 0, 0, 1, 2, 3, 0], store.GetView().History);
    }

    // ---- store: goals ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_goal_defaults_to_three_and_todays_goal_is_clamped()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        Assert.Equal(3, store.GetView().Goal);

        Assert.True(await store.SetTodayGoalAsync(7, CancellationToken.None));
        Assert.Equal(7, store.GetView().Goal);
        Assert.False(await store.SetTodayGoalAsync(7, CancellationToken.None));
        Assert.True(await store.SetTodayGoalAsync(0, CancellationToken.None));
        Assert.Equal(GoalsLimits.MinGoal, store.GetView().Goal);
        Assert.True(await store.SetTodayGoalAsync(99, CancellationToken.None));
        Assert.Equal(GoalsLimits.MaxGoal, store.GetView().Goal);
    }

    [Fact]
    public async Task Todays_own_goal_ends_with_the_day_but_the_default_stays()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        await store.SetTodayGoalAsync(8, CancellationToken.None);
        await store.SetDefaultGoalAsync(5, CancellationToken.None);

        Assert.Equal(8, store.GetView().Goal);

        ctx.FakeTime.Advance(TimeSpan.FromDays(1));

        Assert.Equal(5, store.GetView().Goal);
    }

    [Fact]
    public async Task The_default_goal_is_clamped_persisted_and_restored()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);

        await store.SetDefaultGoalAsync(99, CancellationToken.None);
        Assert.Equal("20", await ctx.Storage.ReadAsync("default-goal", CancellationToken.None));
        await store.SetDefaultGoalAsync(-4, CancellationToken.None);
        Assert.Equal("1", await ctx.Storage.ReadAsync("default-goal", CancellationToken.None));
        await store.SetDefaultGoalAsync(6, CancellationToken.None);

        var restarted = new FakePluginContext(PluginId, AllCapabilities);
        await restarted.Storage.WriteAsync("default-goal", "6", CancellationToken.None);
        Assert.Equal(6, (await NewStoreAsync(restarted)).DefaultGoal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("")]
    public async Task An_unreadable_stored_default_goal_falls_back_to_the_initial_goal(string stored)
    {
        FakePluginContext ctx = await StartAsync(arrange: c => c.Storage.WriteAsync("default-goal", stored, CancellationToken.None));

        Assert.Equal(GoalsLimits.InitialGoal, (await NewStoreAsync(ctx)).DefaultGoal);
    }

    [Fact]
    public async Task Resetting_today_removes_its_tasks_and_goal_but_not_yesterdays()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        await AddAsync(store, "yesterday");
        await store.SetDoneAsync(store.GetView().Tasks[0].Id, true, CancellationToken.None);
        ctx.FakeTime.Advance(TimeSpan.FromDays(1));
        await AddAsync(store, "a", "b");
        await store.SetTodayGoalAsync(9, CancellationToken.None);

        Assert.True(await store.ResetTodayAsync(CancellationToken.None));
        Assert.False(await store.ResetTodayAsync(CancellationToken.None));

        GoalsView view = store.GetView();
        Assert.Empty(view.Tasks);
        Assert.Equal(GoalsLimits.InitialGoal, view.Goal);
        Assert.Equal(1, view.History[5]);
    }

    // ---- store: stored data is untrusted --------------------------------------------------------------------------

    [Fact]
    public async Task Unreadable_stored_data_starts_empty_and_the_plugin_keeps_working()
    {
        FakePluginContext ctx = await StartAsync(arrange: c => c.Storage.WriteAsync("data", "{ not json", CancellationToken.None));

        UiPage page = await RenderPageAsync(ctx);
        await AddViaPageAsync(ctx, "fresh");

        Assert.True(HasText(page, "No tasks yet. Add your first one above."));
        Assert.Equal(["fresh"], (await NewStoreAsync(ctx)).GetView().Tasks.Select(t => t.Text));
    }

    [Fact]
    public async Task Invalid_values_in_stored_data_are_repaired()
    {
        string json = $$"""
            {"v":1,"n":0,"d":[
              {"d":{{Jan1}},"g":500,"t":[{"i":4,"x":"  keep  "},{"i":4,"x":"duplicate id"},{"i":0,"x":"bad id"},{"i":7,"x":"   "},{"i":8,"x":"done","k":true}]},
              {"d":{{Jan1}},"g":2,"t":[{"i":9,"x":"second record of the same day"}]},
              {"d":5,"t":[{"i":10,"x":"far too old"}]},
              {"d":{{Jan1 - 1}},"g":-3,"t":null}
            ]}
            """;
        FakePluginContext ctx = await StartAsync(arrange: c => c.Storage.WriteAsync("data", json, CancellationToken.None));
        GoalsStore store = await NewStoreAsync(ctx);

        GoalsView view = store.GetView();

        Assert.Equal(GoalsLimits.MaxGoal, view.Goal);
        Assert.Equal(["keep", "done"], view.Tasks.Select(t => t.Text));
        Assert.Equal(1, view.Done);
        await AddAsync(store, "next");
        Assert.True(store.GetView().Tasks[^1].Id > 8);
    }

    // ---- page -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_empty_page_has_a_title_four_tiles_a_tasks_card_and_no_duplicate_header()
    {
        FakePluginContext ctx = await StartAsync();

        UiPage page = await RenderPageAsync(ctx);

        Assert.True(UiTreeValidator.Validate(page).IsValid);
        Assert.Equal("Goals & Tasks", page.Title);
        Assert.False(string.IsNullOrWhiteSpace(page.Subtitle));
        Assert.Empty(Descendants(page).OfType<UiGlyph>());
        Assert.DoesNotContain(Descendants(page).OfType<UiText>(), t => t.Text == "Goals & Tasks");
        Assert.Equal(["Today's tasks", "Completed per day"], page.Children.OfType<UiCard>().Select(c => c.Title));
        Assert.Equal(4, Assert.IsType<UiGrid>(page.Children[0]).Columns);
        Assert.Equal(
            [("Today", "0 / 3", "3 to go"), ("Done this week", "0", "Last 7 days"), ("Streak", "0", "Days in a row"), ("Open tasks", "0", "Today")],
            Tiles(page).Select(t => (t.Label, t.Value, t.Caption)));
        Assert.All(Tiles(page), t => Assert.True(t.Value.Length <= 6));
        Assert.Empty(Descendants(page).OfType<UiProgress>());
        Assert.Empty(page.Children.OfType<UiCard>().SelectMany(c => c.Children).OfType<UiStatTile>());
        UiTextField field = Assert.IsType<UiTextField>(Assert.Single(Single<UiForm>(page).Fields));
        Assert.Equal((GoalsPage.AddFieldId, GoalsLimits.MaxTaskLength), (field.Id, field.MaxLength));
        Assert.Equal("Add", Single<UiForm>(page).SubmitLabel);
        Assert.True(HasText(page, "No tasks yet. Add your first one above."));
        Assert.Empty(Descendants(page).OfType<UiToggle>());
        Assert.DoesNotContain(Descendants(page).OfType<UiButton>(), b => b.Id == GoalsPage.ClearButtonId);
        Assert.Empty(Descendants(page).OfType<UiBarChart>());
        Assert.True(HasText(page, "Completed tasks show up here."));
    }

    [Fact]
    public async Task The_page_lists_tasks_with_toggles_and_remove_buttons_and_styles_done_tasks()
    {
        FakePluginContext ctx = await StartAsync();
        await AddViaPageAsync(ctx, "Write report");
        await AddViaPageAsync(ctx, "Call mum");
        await ctx.SendActionAsync(ctx.GetExtension<IUiPagePlugin>(), UiAction.Toggle("goals-done-2", true), CancellationToken.None);

        UiPage page = await RenderPageAsync(ctx);

        Assert.True(UiTreeValidator.Validate(page).IsValid);
        UiToggle[] toggles = [.. Descendants(page).OfType<UiToggle>()];
        Assert.Equal(
            [new UiToggle("goals-done-1", "Write report", false), new UiToggle("goals-done-2", "Call mum", true, "Done")],
            toggles);
        Assert.Equal(["goals-del-1", "goals-del-2"], Descendants(page).OfType<UiButton>().Where(b => b.Id.StartsWith("goals-del-", StringComparison.Ordinal)).Select(b => b.Id));
        Assert.Equal("Clear completed", Button(page, GoalsPage.ClearButtonId).Label);
        Assert.Equal("1 open, 1 done", Descendants(page).OfType<UiCard>().Single(c => c.Title == "Today's tasks").Subtitle);
        Assert.Equal(["1 / 3", "1", "0", "1"], Tiles(page).Select(t => t.Value));
    }

    [Fact]
    public async Task Reaching_the_goal_is_celebrated_and_the_bar_never_exceeds_the_goal()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);   // goal 2
        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);   // goal 1
        for (int i = 1; i <= 3; i++)
        {
            await AddViaPageAsync(ctx, $"task {i}");
            await ctx.SendActionAsync(page, UiAction.Toggle($"goals-done-{i}", true), CancellationToken.None);
        }

        UiPage rendered = await RenderPageAsync(ctx);

        UiStatTile stat = Tiles(rendered)[0];
        Assert.Equal(("3 / 1", "Goal reached", UiTone.Success), (stat.Value, stat.Caption, stat.Tone));
        UiStatTile streak = Tiles(rendered)[2];
        Assert.Equal(("1", UiTone.Success), (streak.Value, streak.Tone));
    }

    [Fact]
    public async Task Submitting_the_form_adds_a_trimmed_task_and_requests_a_render()
    {
        FakePluginContext ctx = await StartAsync();
        await RenderPageAsync(ctx);   // gives the page its surface
        int before = ctx.RenderRequests;

        await AddViaPageAsync(ctx, "  Plan the week  ");

        Assert.Equal(before + 1, ctx.RenderRequests);
        Assert.Equal("Plan the week", Single<UiToggle>(await RenderPageAsync(ctx)).Label);
    }

    [Fact]
    public async Task Overlong_text_is_cut_and_blank_submissions_are_ignored()
    {
        FakePluginContext ctx = await StartAsync();

        await AddViaPageAsync(ctx, new string('x', 400));
        await AddViaPageAsync(ctx, "   ");
        await ctx.SendActionAsync(
            ctx.GetExtension<IUiPagePlugin>(),
            UiAction.Submit(GoalsPage.AddFormId, new Dictionary<string, string> { ["other-field"] = "ignored" }),
            CancellationToken.None);

        UiToggle toggle = Single<UiToggle>(await RenderPageAsync(ctx));
        Assert.Equal(120, toggle.Label.Length);
    }

    [Fact]
    public async Task A_full_list_replaces_the_form_with_a_hint()
    {
        FakePluginContext ctx = await StartAsync();
        GoalsStore store = await NewStoreAsync(ctx);
        for (int i = 0; i < GoalsLimits.MaxTasksPerDay; i++)
        {
            await AddAsync(store, $"task {i}");
        }

        string json = Assert.IsType<string>(await ctx.Storage.ReadAsync("data", CancellationToken.None));
        FakePluginContext full = await StartAsync(arrange: c => c.Storage.WriteAsync("data", json, CancellationToken.None));
        UiPage page = await RenderPageAsync(full);

        Assert.True(UiTreeValidator.Validate(page).IsValid);
        Assert.Empty(Descendants(page).OfType<UiForm>());
        Assert.Equal(50, Descendants(page).OfType<UiToggle>().Count());
        Assert.Contains(Descendants(page).OfType<UiText>(), t => t.Text.StartsWith("Today's list is full", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Toggling_removing_and_clearing_work_through_actions()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        foreach (string text in new[] { "a", "b", "c" })
        {
            await AddViaPageAsync(ctx, text);
        }

        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-1", true), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-2", true), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-2", false), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("goals-del-3"), CancellationToken.None);
        UiPage afterRemove = await RenderPageAsync(ctx);
        Assert.Equal(["a", "b"], Descendants(afterRemove).OfType<UiToggle>().Select(t => t.Label));
        Assert.Equal([true, false], Descendants(afterRemove).OfType<UiToggle>().Select(t => t.IsOn));

        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.ClearButtonId), CancellationToken.None);

        UiPage cleared = await RenderPageAsync(ctx);
        Assert.Equal(["b"], Descendants(cleared).OfType<UiToggle>().Select(t => t.Label));
        Assert.DoesNotContain(Descendants(cleared).OfType<UiButton>(), b => b.Id == GoalsPage.ClearButtonId);
    }

    [Fact]
    public async Task The_goal_buttons_change_todays_goal_and_are_disabled_at_the_limits()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();

        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalUpId), CancellationToken.None);

        Assert.Equal("0 / 4", Tiles(await RenderPageAsync(ctx))[0].Value);

        for (int i = 0; i < 30; i++)
        {
            await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalUpId), CancellationToken.None);
        }

        UiPage top = await RenderPageAsync(ctx);
        Assert.Equal("0 / 20", Tiles(top)[0].Value);
        Assert.False(Button(top, GoalsPage.GoalUpId).IsEnabled);
        Assert.True(Button(top, GoalsPage.GoalDownId).IsEnabled);

        for (int i = 0; i < 30; i++)
        {
            await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);
        }

        UiPage bottom = await RenderPageAsync(ctx);
        Assert.Equal("0 / 1", Tiles(bottom)[0].Value);
        Assert.False(Button(bottom, GoalsPage.GoalDownId).IsEnabled);
    }

    [Fact]
    public async Task Invalid_page_actions_are_ignored()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await AddViaPageAsync(ctx, "a");
        string before = Assert.IsType<string>(await ctx.Storage.ReadAsync("data", CancellationToken.None));
        var badValue = new UiAction("goals-done-1", UiActionKind.Toggle, "maybe", new Dictionary<string, string>());

        await ctx.SendActionAsync(page, badValue, CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("goals-done-1"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-del-1", true), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-abc", true), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done--1", true), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-99999999999", true), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("goals-del-"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("goals-del-7"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("unknown"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Submit("other-form", new Dictionary<string, string> { [GoalsPage.AddFieldId] = "x" }), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.AddFormId), CancellationToken.None);

        Assert.Equal(before, await ctx.Storage.ReadAsync("data", CancellationToken.None));
    }

    [Fact]
    public async Task The_history_chart_shows_seven_days_ending_today()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await AddViaPageAsync(ctx, "a");
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-1", true), CancellationToken.None);
        ctx.FakeTime.Advance(TimeSpan.FromDays(1));
        await AddViaPageAsync(ctx, "b");
        await AddViaPageAsync(ctx, "c");
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-2", true), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-3", true), CancellationToken.None);

        UiPage rendered = await RenderPageAsync(ctx);
        UiBarChart chart = Single<UiBarChart>(rendered);

        Assert.Equal(
            [new UiDataPoint("Sat", 0), new UiDataPoint("Sun", 0), new UiDataPoint("Mon", 0), new UiDataPoint("Tue", 0), new UiDataPoint("Wed", 0), new UiDataPoint("Thu", 1), new UiDataPoint("Today", 2)],
            chart.Points);
        Assert.Equal("done", chart.ValueSuffix);
        Assert.Equal("Completed per day", Descendants(rendered).OfType<UiCard>().Single(c => c.Children.Contains(chart)).Title);
        Assert.Equal("3", Tiles(rendered)[1].Value);
    }

    [Fact]
    public async Task The_goal_buttons_sit_in_the_tasks_card()
    {
        FakePluginContext ctx = await StartAsync();

        UiPage page = await RenderPageAsync(ctx);

        UiCard tasks = page.Children.OfType<UiCard>().First();
        Assert.Equal([GoalsPage.GoalDownId, GoalsPage.GoalUpId], Descendants(tasks).OfType<UiButton>().Where(b => b.Id.StartsWith("goals-goal-", StringComparison.Ordinal)).Select(b => b.Id));
        Assert.True(HasText(tasks, "Goal for today: 3 tasks"));
    }

    [Fact]
    public async Task The_streak_counts_days_in_a_row_with_the_goal_reached()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);   // goal 1 today
        await AddViaPageAsync(ctx, "a");
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-1", true), CancellationToken.None);
        Assert.Equal("1", Tiles(await RenderPageAsync(ctx))[2].Value);

        ctx.FakeTime.Advance(TimeSpan.FromDays(1));   // the default goal of 3 applies, yesterday's own goal 1 was reached
        Assert.Equal("1", Tiles(await RenderPageAsync(ctx))[2].Value);   // today still open: streak ends yesterday

        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);
        await AddViaPageAsync(ctx, "b");
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-2", true), CancellationToken.None);
        Assert.Equal("2", Tiles(await RenderPageAsync(ctx))[2].Value);

        ctx.FakeTime.Advance(TimeSpan.FromDays(2));   // a day without reaching the goal breaks the streak
        Assert.Equal("0", Tiles(await RenderPageAsync(ctx))[2].Value);
    }

    [Fact]
    public async Task The_streak_is_capped_at_the_retention_window()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        for (int day = 0; day < 9; day++)
        {
            await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);
            await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);
            await AddViaPageAsync(ctx, $"t{day}");
            await ctx.SendActionAsync(page, UiAction.Toggle($"goals-done-{day + 1}", true), CancellationToken.None);
            ctx.FakeTime.Advance(TimeSpan.FromDays(1));
        }

        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(GoalsPage.GoalDownId), CancellationToken.None);
        await AddViaPageAsync(ctx, "last");
        await ctx.SendActionAsync(page, UiAction.Toggle("goals-done-10", true), CancellationToken.None);

        Assert.Equal("7+", Tiles(await RenderPageAsync(ctx))[2].Value);
    }

    // ---- widget ---------------------------------------------------------------------------------------------------

    private static void AssertFlatWidget(UiCard card)
    {
        Assert.True(UiTreeValidator.Validate(card).IsValid);
        Assert.Equal((null, null, null), (card.Title, card.Subtitle, card.Icon));
        Assert.Empty(Descendants(card).OfType<UiGlyph>());
        Assert.Single(Descendants(card).OfType<UiCard>());
        Assert.Single(Descendants(card).OfType<UiStatTile>());
        Assert.Single(Descendants(card).OfType<UiProgress>());
        Assert.True(Descendants(card).OfType<UiToggle>().Count() <= GoalsWidget.VisibleTasks);
    }

    [Fact]
    public async Task The_widget_without_tasks_shows_progress_and_a_hint()
    {
        FakePluginContext ctx = await StartAsync();

        UiCard card = await RenderWidgetAsync(ctx);

        AssertFlatWidget(card);
        Assert.Equal("0 / 3 tasks", Single<UiStatTile>(card).Value);
        Assert.Equal(new UiProgress(0, 3, null, UiTone.Accent), Single<UiProgress>(card));
        Assert.Empty(Descendants(card).OfType<UiToggle>());
        Assert.True(HasText(card, "Add tasks on the Goals page"));
    }

    [Fact]
    public async Task The_widget_shows_the_next_three_open_tasks_and_lets_you_tick_them()
    {
        FakePluginContext ctx = await StartAsync();
        foreach (string text in new[] { "a", "b", "c", "d", "e" })
        {
            await AddViaPageAsync(ctx, text);
        }

        await ctx.SendActionAsync(ctx.GetExtension<IDashboardWidgetPlugin>(), UiAction.Toggle("goals-done-1", true), CancellationToken.None);
        UiCard card = await RenderWidgetAsync(ctx);

        AssertFlatWidget(card);
        Assert.Equal("1 / 3 tasks", Single<UiStatTile>(card).Value);
        Assert.Equal(["b", "c", "d"], Descendants(card).OfType<UiToggle>().Select(t => t.Label));
        Assert.All(Descendants(card).OfType<UiToggle>(), t => Assert.False(t.IsOn));
        Assert.True(HasText(card, "+1 more on the Goals page"));
        Assert.Equal(["goals-done-2", "goals-done-3", "goals-done-4"], Descendants(card).OfType<UiToggle>().Select(t => t.Id));
    }

    [Fact]
    public async Task The_widget_says_so_when_every_task_is_done()
    {
        FakePluginContext ctx = await StartAsync();
        await AddViaPageAsync(ctx, "a");
        await ctx.SendActionAsync(ctx.GetExtension<IDashboardWidgetPlugin>(), UiAction.Toggle("goals-done-1", true), CancellationToken.None);

        UiCard card = await RenderWidgetAsync(ctx);

        AssertFlatWidget(card);
        Assert.Empty(Descendants(card).OfType<UiToggle>());
        Assert.True(HasText(card, "All tasks done. Nice work!"));
        Assert.Equal("1 / 3 tasks", Single<UiStatTile>(card).Value);
    }

    [Fact]
    public async Task A_change_on_the_page_asks_the_widget_to_render_again()
    {
        FakePluginContext ctx = await StartAsync();
        await RenderWidgetAsync(ctx);   // gives the widget its surface
        int before = ctx.RenderRequests;

        await AddViaPageAsync(ctx, "a");

        Assert.True(ctx.RenderRequests > before);
        Assert.Single(Descendants(await RenderWidgetAsync(ctx)).OfType<UiToggle>());
    }

    // ---- settings -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_default_goal_is_changed_in_settings_and_used_for_today()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        UiNode initial = await RenderSettingsAsync(ctx);
        Assert.True(UiTreeValidator.Validate(initial).IsValid);
        Assert.True(HasText(initial, "Default daily goal: 3 tasks"));

        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.DefaultUpId), CancellationToken.None);
        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.DefaultUpId), CancellationToken.None);

        Assert.True(HasText(await RenderSettingsAsync(ctx), "Default daily goal: 5 tasks"));
        Assert.Equal("5", await ctx.Storage.ReadAsync("default-goal", CancellationToken.None));
        Assert.Equal("0 / 5", Tiles(await RenderPageAsync(ctx))[0].Value);

        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.DefaultDownId), CancellationToken.None);

        Assert.Equal("4", await ctx.Storage.ReadAsync("default-goal", CancellationToken.None));
    }

    [Fact]
    public async Task The_default_goal_buttons_are_disabled_at_the_limits()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();

        for (int i = 0; i < 30; i++)
        {
            await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.DefaultUpId), CancellationToken.None);
        }

        UiNode top = await RenderSettingsAsync(ctx);
        Assert.True(HasText(top, "Default daily goal: 20 tasks"));
        Assert.False(Button(top, GoalsSettings.DefaultUpId).IsEnabled);

        for (int i = 0; i < 30; i++)
        {
            await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.DefaultDownId), CancellationToken.None);
        }

        UiNode bottom = await RenderSettingsAsync(ctx);
        Assert.True(HasText(bottom, "Default daily goal: 1 task"));
        Assert.False(Button(bottom, GoalsSettings.DefaultDownId).IsEnabled);
    }

    [Fact]
    public async Task Resetting_today_needs_a_second_click()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        await AddViaPageAsync(ctx, "a");
        await RenderSettingsAsync(ctx);   // gives the section its surface
        int before = ctx.RenderRequests;
        Assert.Equal("Reset today", Button(await RenderSettingsAsync(ctx), GoalsSettings.ResetButtonId).Label);

        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.ResetButtonId), CancellationToken.None);

        Assert.Equal(before + 1, ctx.RenderRequests);
        Assert.Single((await NewStoreAsync(ctx)).GetView().Tasks);
        Assert.Equal("Click again to confirm", Button(await RenderSettingsAsync(ctx), GoalsSettings.ResetButtonId).Label);

        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.ResetButtonId), CancellationToken.None);

        Assert.Empty((await NewStoreAsync(ctx)).GetView().Tasks);
        Assert.Equal("Reset today", Button(await RenderSettingsAsync(ctx), GoalsSettings.ResetButtonId).Label);
        Assert.True(HasText(await RenderPageAsync(ctx), "No tasks yet. Add your first one above."));
    }

    [Fact]
    public async Task The_reset_confirmation_expires()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        await AddViaPageAsync(ctx, "a");

        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.ResetButtonId), CancellationToken.None);
        ctx.FakeTime.Advance(GoalsSettings.ConfirmWindow + TimeSpan.FromSeconds(1));
        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.ResetButtonId), CancellationToken.None);

        Assert.Single((await NewStoreAsync(ctx)).GetView().Tasks);
    }

    [Fact]
    public async Task A_confirmed_reset_with_nothing_to_reset_leaves_the_armed_state()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        await RenderSettingsAsync(ctx);

        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.ResetButtonId), CancellationToken.None);
        await ctx.SendActionAsync(settings, UiAction.Click(GoalsSettings.ResetButtonId), CancellationToken.None);

        Assert.Equal("Reset today", Button(await RenderSettingsAsync(ctx), GoalsSettings.ResetButtonId).Label);
    }

    [Fact]
    public async Task Invalid_settings_actions_are_ignored()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();

        await ctx.SendActionAsync(settings, UiAction.Toggle(GoalsSettings.DefaultUpId, true), CancellationToken.None);
        await ctx.SendActionAsync(settings, UiAction.Click("unknown-id"), CancellationToken.None);
        await ctx.SendActionAsync(settings, UiAction.Toggle("goals-done-1", true), CancellationToken.None);

        Assert.Null(await ctx.Storage.ReadAsync("default-goal", CancellationToken.None));
        Assert.Null(await ctx.Storage.ReadAsync("data", CancellationToken.None));
    }

    // ---- formatting -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 3, "0 / 3 tasks")]
    [InlineData(3, 5, "3 / 5 tasks")]
    [InlineData(7, 5, "7 / 5 tasks")]
    public void Progress_reads_done_of_goal(int done, int goal, string expected) =>
        Assert.Equal(expected, GoalsFormat.Progress(done, goal));

    [Theory]
    [InlineData(0, 3, "3 to go")]
    [InlineData(2, 3, "1 to go")]
    [InlineData(3, 3, "Goal reached")]
    [InlineData(4, 3, "Goal reached")]
    public void Remaining_tells_how_far_the_goal_is(int done, int goal, string expected) =>
        Assert.Equal(expected, GoalsFormat.Remaining(done, goal));

    [Theory]
    [InlineData(1, "1 task")]
    [InlineData(0, "0 tasks")]
    [InlineData(2, "2 tasks")]
    public void Counts_are_pluralized(int count, string expected) =>
        Assert.Equal(expected, GoalsFormat.Count(count, "task"));
}
