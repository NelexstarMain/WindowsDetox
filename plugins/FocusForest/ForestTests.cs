// Tests of the Focus Forest game: rules, shop, placement, field growth, persistence and the forest tab of the page.
// FakePluginContext starts at 2026-01-01T09:00:00Z with UTC as the local time zone.

using System.Text.Json;
using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Events;
using Blocker.UI.Sdk;
using Xunit;

namespace FocusInsights.Tests;

/// <summary>Behaviour tests of the forest inside the Focus Insights plugin.</summary>
public sealed class ForestTests
{
    private const string PluginId = "com.windowsdetox.insights";

    private static readonly PluginCapability[] AllCapabilities =
    [
        PluginCapability.SessionEvents,
        PluginCapability.ProcessEvents,
        PluginCapability.UiPage,
        PluginCapability.UiDashboardWidget,
        PluginCapability.UiSettingsSection,
    ];

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static async Task<FakePluginContext> StartAsync(Func<FakePluginContext, ValueTask>? arrange = null)
    {
        var ctx = new FakePluginContext(PluginId, AllCapabilities);
        if (arrange is not null)
        {
            await arrange(ctx);
        }

        await new InsightsPlugin().InitializeAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private static async Task<ForestStore> LoadAsync(FakePluginContext ctx)
    {
        var store = new ForestStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private static async Task<ForestStore> NewStoreAsync() => await LoadAsync(new FakePluginContext(PluginId, AllCapabilities));

    private static SessionRecord Session(int minutes, string reason = SessionOutcome.Completed) =>
        new() { Start = 1_767_258_000, Planned = minutes * 60, Actual = minutes * 60, Reason = reason };

    private static async Task GiveCoinsAsync(ForestStore store, int minutes) =>
        await store.OnSessionRecordedAsync(Session(minutes), CancellationToken.None);

    private static async Task RunSessionAsync(FakePluginContext ctx, int minutes, SessionEndReason reason = SessionEndReason.Expired)
    {
        Guid id = Guid.NewGuid();
        await ctx.PublishAsync(new SessionStarted(id, TimeSpan.FromMinutes(minutes), ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        ctx.FakeTime.Advance(TimeSpan.FromMinutes(minutes));
        await ctx.PublishAsync(new SessionEnded(id, reason, ctx.FakeTime.GetUtcNow()), CancellationToken.None);
    }

    private static async Task<UiPage> RenderPageAsync(FakePluginContext ctx) =>
        Assert.IsType<UiPage>(await ctx.RenderAsync(ctx.GetExtension<IUiPagePlugin>(), CancellationToken.None));

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

    private static UiScene Garden(UiPage page) => Descendants(page).OfType<UiScene>().Single(s => s.Id == ForestIds.Scene);

    private static UiButton ButtonWithId(UiPage page, string id) => Descendants(page).OfType<UiButton>().Single(b => b.Id == id);

    private static UiAction Pick(int column, int row) =>
        new(ForestIds.Scene, UiActionKind.Pick, null, new Dictionary<string, string> { ["column"] = column.ToString(), ["row"] = row.ToString() });

    private static ForestObject ObjectAt(ForestView view, int column, int row) => view.Objects.Single(o => o.Column == column && o.Row == row);

    // ---- earning and planting ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_completed_session_earns_one_coin_per_minute_and_plants_the_default_oak()
    {
        FakePluginContext ctx = await StartAsync();

        await RunSessionAsync(ctx, 52);

        ForestView view = (await LoadAsync(ctx)).GetView();
        Assert.Equal(52, view.Coins);
        ForestObject oak = Assert.Single(view.Objects);
        Assert.Equal("oak", oak.Species);
        Assert.Equal(ForestStage.Mature, oak.StageValue);
        Assert.Equal(52, oak.Minutes);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), oak.Planted);
    }

    [Theory]
    [InlineData(10, ForestStage.Sapling)]
    [InlineData(24, ForestStage.Sapling)]
    [InlineData(25, ForestStage.Young)]
    [InlineData(49, ForestStage.Young)]
    [InlineData(50, ForestStage.Mature)]
    [InlineData(89, ForestStage.Mature)]
    [InlineData(90, ForestStage.Majestic)]
    [InlineData(240, ForestStage.Majestic)]
    public async Task The_stage_follows_the_session_length(int minutes, ForestStage expected)
    {
        ForestStore store = await NewStoreAsync();

        await GiveCoinsAsync(store, minutes);

        ForestView view = store.GetView();
        Assert.Equal(expected, Assert.Single(view.Objects).StageValue);
        Assert.Equal(minutes, view.Coins);
    }

    [Fact]
    public async Task A_session_ended_with_break_glass_earns_nothing_and_plants_a_withered_tree()
    {
        FakePluginContext ctx = await StartAsync();

        await RunSessionAsync(ctx, 40, SessionEndReason.BreakGlass);

        ForestView view = (await LoadAsync(ctx)).GetView();
        Assert.Equal(0, view.Coins);
        ForestObject withered = Assert.Single(view.Objects);
        Assert.Equal(ForestStage.Withered, withered.StageValue);
        Assert.Equal("oak", withered.Species);
        Assert.Equal(40, withered.Minutes);
    }

    [Fact]
    public async Task Other_session_ends_change_nothing()
    {
        FakePluginContext ctx = await StartAsync();

        await RunSessionAsync(ctx, 40, SessionEndReason.DeveloperAbort);

        ForestView view = (await LoadAsync(ctx)).GetView();
        Assert.Equal(0, view.Coins);
        Assert.Empty(view.Objects);
        Assert.Null(await ctx.Storage.ReadAsync(ForestStore.DataKey, CancellationToken.None));
    }

    [Fact]
    public async Task The_chosen_species_is_planted()
    {
        ForestStore store = await NewStoreAsync();

        Assert.True(await store.SelectSpeciesAsync("pine", CancellationToken.None));
        await GiveCoinsAsync(store, 30);
        await store.OnSessionRecordedAsync(Session(20, SessionOutcome.EndedEarly), CancellationToken.None);

        Assert.All(store.GetView().Objects, o => Assert.Equal("pine", o.Species));
    }

    // ---- shop ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unlocking_needs_enough_coins_and_spends_them_once()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 149);

        Assert.False(await store.UnlockAsync("birch", CancellationToken.None));
        Assert.Equal(149, store.GetView().Coins);
        Assert.DoesNotContain("birch", store.GetView().Unlocked);

        await GiveCoinsAsync(store, 1);

        Assert.True(await store.UnlockAsync("birch", CancellationToken.None));
        Assert.Equal(0, store.GetView().Coins);
        Assert.Contains("birch", store.GetView().Unlocked);
        Assert.False(await store.UnlockAsync("birch", CancellationToken.None));
    }

    [Theory]
    [InlineData("bench")]
    [InlineData("nope")]
    [InlineData("oak")]
    public async Task Decorations_unknown_ids_and_free_species_cannot_be_unlocked(string id)
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 500);

        Assert.False(await store.UnlockAsync(id, CancellationToken.None));
        Assert.Equal(500, store.GetView().Coins);
    }

    [Fact]
    public async Task Only_unlocked_trees_and_bushes_can_be_selected()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 200);

        Assert.False(await store.SelectSpeciesAsync("birch", CancellationToken.None));
        Assert.False(await store.SelectSpeciesAsync("bench", CancellationToken.None));
        Assert.True(await store.UnlockAsync("birch", CancellationToken.None));
        Assert.True(await store.SelectSpeciesAsync("birch", CancellationToken.None));

        Assert.Equal("birch", store.GetView().Chosen);
    }

    [Fact]
    public async Task Decorations_are_bought_per_piece_into_the_inventory()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 100);

        Assert.True(await store.BuyDecorationAsync("rock", CancellationToken.None));
        Assert.True(await store.BuyDecorationAsync("rock", CancellationToken.None));
        Assert.False(await store.BuyDecorationAsync("rock", CancellationToken.None));
        Assert.False(await store.BuyDecorationAsync("birch", CancellationToken.None));

        ForestView view = store.GetView();
        Assert.Equal(20, view.Coins);
        Assert.Equal(2, view.Inventory["rock"]);
    }

    // ---- placement and moving ------------------------------------------------------------------------------------

    [Fact]
    public async Task New_plants_spiral_out_from_the_centre()
    {
        ForestStore store = await NewStoreAsync();

        for (int i = 0; i < 6; i++)
        {
            await GiveCoinsAsync(store, 10);
        }

        Assert.Equal(
            [(2, 2), (3, 2), (2, 3), (3, 3), (2, 1), (3, 1)],
            store.GetView().Objects.Select(o => (o.Column, o.Row)).ToArray());
    }

    [Fact]
    public async Task A_bought_decoration_is_placed_by_clicking_an_empty_tile()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 130);
        await store.BuyDecorationAsync("bench", CancellationToken.None);

        store.TogglePlacing("bench");
        Assert.True(store.GetView().IsPicking);
        Assert.False(await store.PickAsync(2, 2, CancellationToken.None));   // occupied by the oak
        Assert.False(await store.PickAsync(6, 0, CancellationToken.None));   // outside the field
        Assert.True(await store.PickAsync(0, 0, CancellationToken.None));

        ForestView view = store.GetView();
        Assert.Equal("bench", ObjectAt(view, 0, 0).Species);
        Assert.Empty(view.Inventory);
        Assert.False(view.IsPicking);
    }

    [Fact]
    public async Task Placing_is_refused_for_decorations_that_are_not_owned()
    {
        ForestStore store = await NewStoreAsync();

        store.TogglePlacing("bench");

        Assert.Null(store.GetView().PlacingDecoration);
        Assert.False(await store.PickAsync(0, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Selecting_an_object_and_picking_an_empty_tile_moves_it()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 30);
        int id = store.GetView().Objects[0].Id;

        store.ToggleSelect(id);
        Assert.Equal(id, store.GetView().SelectedObject);
        Assert.True(await store.PickAsync(5, 5, CancellationToken.None));

        ForestView view = store.GetView();
        Assert.Equal((5, 5), (view.Objects[0].Column, view.Objects[0].Row));
        Assert.Null(view.SelectedObject);
        Assert.False(await store.PickAsync(0, 0, CancellationToken.None));   // nothing selected any more
    }

    [Fact]
    public async Task Clicking_the_selected_object_again_or_cancelling_deselects_it()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 30);
        int id = store.GetView().Objects[0].Id;

        store.ToggleSelect(id);
        store.ToggleSelect(id);
        Assert.Null(store.GetView().SelectedObject);

        store.ToggleSelect(id);
        store.CancelSelection();
        Assert.Null(store.GetView().SelectedObject);

        store.ToggleSelect(9999);
        Assert.Null(store.GetView().SelectedObject);
    }

    [Fact]
    public async Task A_withered_tree_can_be_removed_for_a_fee()
    {
        ForestStore store = await NewStoreAsync();
        await store.OnSessionRecordedAsync(Session(20, SessionOutcome.EndedEarly), CancellationToken.None);
        await GiveCoinsAsync(store, 25);
        int withered = store.GetView().Objects.Single(o => o.StageValue == ForestStage.Withered).Id;
        int healthy = store.GetView().Objects.Single(o => o.StageValue != ForestStage.Withered).Id;

        Assert.False(await store.RemoveWitheredAsync(healthy, CancellationToken.None));
        Assert.True(await store.RemoveWitheredAsync(withered, CancellationToken.None));

        ForestView view = store.GetView();
        Assert.Equal(25 - ForestLimits.WitheredRemovalFee, view.Coins);
        Assert.Equal([healthy], view.Objects.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task Removing_a_withered_tree_needs_the_coins()
    {
        ForestStore store = await NewStoreAsync();
        await store.OnSessionRecordedAsync(Session(20, SessionOutcome.EndedEarly), CancellationToken.None);

        Assert.False(await store.RemoveWitheredAsync(store.GetView().Objects[0].Id, CancellationToken.None));
        Assert.Single(store.GetView().Objects);
    }

    // ---- growing field -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_field_grows_at_three_quarters_full_and_objects_stay_centred()
    {
        ForestStore store = await NewStoreAsync();
        for (int i = 0; i < 26; i++)
        {
            await GiveCoinsAsync(store, 10);
        }

        ForestView before = store.GetView();
        Assert.Equal(6, before.Size);
        (int column, int row) first = (before.Objects[0].Column, before.Objects[0].Row);

        await GiveCoinsAsync(store, 10);

        ForestView after = store.GetView();
        Assert.Equal(8, after.Size);
        Assert.Equal(27, after.Objects.Count);
        Assert.Equal((first.column + 1, first.row + 1), (after.Objects[0].Column, after.Objects[0].Row));
        Assert.All(after.Objects, o => Assert.InRange(o.Column, 0, 7));
        Assert.Equal(27, after.Objects.Select(o => (o.Column, o.Row)).Distinct().Count());
    }

    [Fact]
    public async Task The_field_stops_at_sixteen_and_the_object_count_is_bounded()
    {
        FakePluginContext ctx = await StartAsync();
        for (int i = 0; i < ForestLimits.MaxObjects + 4; i++)
        {
            await RunSessionAsync(ctx, 1);
        }

        ForestView view = (await LoadAsync(ctx)).GetView();

        Assert.Equal(ForestLimits.MaxSize, view.Size);
        Assert.Equal(ForestLimits.MaxObjects, view.Objects.Count);
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Level == PluginLogLevel.Warning);
        Assert.True(UiTreeValidator.Validate(await RenderPageAsync(ctx)).IsValid);
    }

    // ---- persistence ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_forest_survives_a_restart()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 60);
        await RunSessionAsync(ctx, 30, SessionEndReason.BreakGlass);
        await RunSessionAsync(ctx, 100);
        ForestStore first = await LoadAsync(ctx);
        await first.UnlockAsync("rose", CancellationToken.None);
        await first.SelectSpeciesAsync("rose", CancellationToken.None);
        await first.BuyDecorationAsync("rock", CancellationToken.None);

        ForestView expected = first.GetView();
        ForestView reloaded = (await LoadAsync(ctx)).GetView();

        Assert.Equal(expected.Coins, reloaded.Coins);
        Assert.Equal(expected.Chosen, reloaded.Chosen);
        Assert.Equal(expected.Size, reloaded.Size);
        Assert.Equal(expected.Unlocked.Order(), reloaded.Unlocked.Order());
        Assert.Equal(expected.Inventory, reloaded.Inventory);
        Assert.Equal(
            expected.Objects.Select(o => (o.Id, o.Species, o.Stage, o.Column, o.Row, o.Planted, o.Minutes)),
            reloaded.Objects.Select(o => (o.Id, o.Species, o.Stage, o.Column, o.Row, o.Planted, o.Minutes)));
        Assert.Equal(3, reloaded.Objects.Count);
    }

    [Fact]
    public async Task The_stored_document_is_versioned_and_compact()
    {
        FakePluginContext ctx = await StartAsync();

        await RunSessionAsync(ctx, 30);

        using JsonDocument doc = JsonDocument.Parse((await ctx.Storage.ReadAsync(ForestStore.DataKey, CancellationToken.None))!);
        Assert.Equal(ForestData.CurrentVersion, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(30, doc.RootElement.GetProperty("c").GetInt32());
        JsonElement stored = doc.RootElement.GetProperty("o")[0];
        Assert.Equal("oak", stored.GetProperty("k").GetString());
        Assert.Equal(30, stored.GetProperty("m").GetInt32());
    }

    [Fact]
    public async Task Unreadable_data_starts_a_fresh_forest_and_keeps_a_copy()
    {
        FakePluginContext ctx = await StartAsync(c => c.Storage.WriteAsync(ForestStore.DataKey, "{not json", CancellationToken.None));

        ForestView view = (await LoadAsync(ctx)).GetView();

        Assert.Equal(0, view.Coins);
        Assert.Empty(view.Objects);
        Assert.Equal(ForestLimits.StartSize, view.Size);
        Assert.Equal("{not json", await ctx.Storage.ReadAsync(ForestStore.UnreadableKey, CancellationToken.None));
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Level == PluginLogLevel.Warning);
        Assert.True(UiTreeValidator.Validate(await RenderPageAsync(ctx)).IsValid);
    }

    [Fact]
    public async Task Invalid_values_in_stored_data_are_repaired()
    {
        const string json = """
            {"v":1,"c":-5,"u":["birch","bench","nope"],"s":"cherry","z":7,"b":{"rock":500,"oak":2,"flowers":0},
             "o":[{"i":1,"k":"oak","g":2,"c":1,"r":1,"m":50},
                  {"i":1,"k":"pine","g":2,"c":2,"r":2},
                  {"i":2,"k":"pine","g":2,"c":1,"r":1},
                  {"i":3,"k":"nope","g":2,"c":3,"r":3},
                  {"i":4,"k":"pine","g":9,"c":3,"r":3},
                  {"i":5,"k":"pine","g":2,"c":9,"r":3},
                  {"i":6,"k":"bench","g":4,"c":4,"r":4},
                  {"i":7,"k":"pine","g":1,"c":5,"r":5,"m":-3}]}
            """;
        FakePluginContext ctx = await StartAsync(c => c.Storage.WriteAsync(ForestStore.DataKey, json, CancellationToken.None));

        ForestView view = (await LoadAsync(ctx)).GetView();

        Assert.Equal(0, view.Coins);
        Assert.Equal(6, view.Size);
        Assert.Equal("oak", view.Chosen);
        Assert.Contains("birch", view.Unlocked);
        Assert.DoesNotContain("bench", view.Unlocked);
        Assert.Equal(ForestLimits.MaxInventoryPerItem, view.Inventory["rock"]);
        Assert.Single(view.Inventory);
        Assert.Equal([1, 7], view.Objects.Select(o => o.Id).ToArray());
        Assert.Equal(0, view.Objects[1].Minutes);
        await GiveCoinsAsync(await LoadAsync(ctx), 10);   // the next id must not collide with a stored one
        Assert.Equal(3, (await LoadAsync(ctx)).GetView().Objects.Select(o => o.Id).Distinct().Count());
    }

    [Fact]
    public async Task Data_of_a_newer_version_is_shown_but_never_overwritten()
    {
        const string json = """{"v":2,"c":77,"o":[{"i":1,"k":"oak","g":2,"c":1,"r":1,"m":50}]}""";
        FakePluginContext ctx = await StartAsync(c => c.Storage.WriteAsync(ForestStore.DataKey, json, CancellationToken.None));
        await RunSessionAsync(ctx, 30);
        ForestStore store = await LoadAsync(ctx);

        await store.OnSessionRecordedAsync(Session(30), CancellationToken.None);

        Assert.Equal(77, (await LoadAsync(ctx)).GetView().Coins);
        Assert.Equal(json, await ctx.Storage.ReadAsync(ForestStore.DataKey, CancellationToken.None));
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Level == PluginLogLevel.Warning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(37)]
    [InlineData(1000)]
    public void Rotation_is_a_stable_quarter_turn_of_the_id(int id)
    {
        int rotation = ForestStore.RotationOf(id);

        Assert.InRange(rotation, 0, 3);
        Assert.Equal(rotation, ForestStore.RotationOf(id));
    }

    // ---- page ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_forest_tab_is_the_default_and_shows_coins_scene_and_shop()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 52);

        UiPage page = await RenderPageAsync(ctx);

        Assert.True(UiTreeValidator.Validate(page).IsValid);
        Assert.Empty(Descendants(page).OfType<UiGlyph>());
        UiStatTile coins = Descendants(page).OfType<UiStatTile>().First();
        Assert.Equal(("Coins", "52"), (coins.Label, coins.Value));
        UiScene garden = Garden(page);
        Assert.Equal(720, garden.Height);
        Assert.False(garden.AllowPick);
        UiSceneItem item = Assert.Single(garden.Items);
        Assert.Equal("Oak · 52 min · Jan 1", item.Label);
        UiSceneModel model = Assert.Single(garden.Models);
        Assert.Equal(item.Model, model.Key);
        Assert.Contains(Descendants(page).OfType<UiCard>(), c => c is { Title: "Your forest", Subtitle: "Planting next: Oak" });
    }

    [Fact]
    public async Task The_shop_lists_one_category_at_a_time_with_a_thumbnail_and_the_right_button_per_entry()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 160);
        IUiPagePlugin plugin = ctx.GetExtension<IUiPagePlugin>();

        UiPage trees = await RenderPageAsync(ctx);

        UiScene[] thumbnails = [.. Descendants(trees).OfType<UiScene>().Where(s => s.Id != ForestIds.Scene)];
        Assert.Equal(ForestCatalog.All.Count(s => s.Kind == ForestKind.Tree), thumbnails.Length);
        Assert.All(thumbnails, t => Assert.Equal((1, 1, false, false, 150d), (t.Columns, t.Rows, t.ShowGround, t.AllowRotate, t.Height)));
        Assert.Equal(new UiButton("forest-select-oak", "Planting", UiButtonVariant.Primary, UiIcon.Checkmark, false), ButtonWithId(trees, "forest-select-oak"));
        Assert.True(ButtonWithId(trees, "forest-select-pine").IsEnabled);
        Assert.Equal("Unlock", ButtonWithId(trees, "forest-unlock-birch").Label);
        Assert.True(ButtonWithId(trees, "forest-unlock-birch").IsEnabled);
        Assert.False(ButtonWithId(trees, "forest-unlock-maple").IsEnabled);   // 250 coins

        await ctx.SendActionAsync(plugin, UiAction.Click(ForestIds.ShopTab(ShopCategory.Decorations)), CancellationToken.None);
        UiPage decorations = await RenderPageAsync(ctx);

        Assert.Equal(ForestCatalog.All.Count(s => s.Kind == ForestKind.Decoration), Descendants(decorations).OfType<UiScene>().Count(s => s.Id != ForestIds.Scene));
        Assert.True(ButtonWithId(decorations, "forest-buy-bench").IsEnabled);
        Assert.False(ButtonWithId(decorations, "forest-buy-cabin").IsEnabled);

        await ctx.SendActionAsync(plugin, UiAction.Click(ForestIds.ShopTab(ShopCategory.Bushes)), CancellationToken.None);

        Assert.Equal("Select", ButtonWithId(await RenderPageAsync(ctx), "forest-select-bush").Label);
    }

    [Fact]
    public async Task A_forest_with_every_species_and_stage_still_fits_the_document_limit()
    {
        List<string> objects = [];
        int index = 0;
        foreach (ForestSpecies species in ForestCatalog.All)
        {
            foreach (int stage in species.Kind == ForestKind.Decoration ? [2] : new[] { 0, 1, 2, 3, 4 })
            {
                objects.Add($$"""{"i":{{index + 1}},"k":"{{species.Id}}","g":{{stage}},"c":{{index % 10}},"r":{{index / 10}},"m":30}""");
                index++;
            }
        }

        string json = $$"""{"v":1,"z":10,"o":[{{string.Join(",", objects)}}]}""";
        FakePluginContext ctx = await StartAsync(c => c.Storage.WriteAsync(ForestStore.DataKey, json, CancellationToken.None));

        foreach (ShopCategory category in Enum.GetValues<ShopCategory>())
        {
            await ctx.SendActionAsync(ctx.GetExtension<IUiPagePlugin>(), UiAction.Click(ForestIds.ShopTab(category)), CancellationToken.None);
            UiPage page = await RenderPageAsync(ctx);

            Assert.True(UiTreeValidator.Validate(page).IsValid);
            UiScene garden = Garden(page);
            Assert.Equal(index, garden.Items.Count);
            Assert.All(garden.Items, i => Assert.Contains(garden.Models, m => m.Key == i.Model));
        }
    }

    [Fact]
    public async Task Shop_actions_unlock_select_and_buy_through_the_page()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 90);
        await RunSessionAsync(ctx, 90);
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);

        await ctx.SendActionAsync(page, UiAction.Click("forest-unlock-birch"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("forest-select-birch"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ShopTab(ShopCategory.Decorations)), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("forest-buy-rock"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("forest-unlock-nope"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Toggle("forest-unlock-willow", true), CancellationToken.None);

        ForestView view = (await LoadAsync(ctx)).GetView();
        Assert.Equal("birch", view.Chosen);
        Assert.Equal(30, view.Coins);
        Assert.Empty(view.Inventory);
        UiButton buy = ButtonWithId(await RenderPageAsync(ctx), "forest-buy-rock");
        Assert.False(buy.IsEnabled);
    }

    [Fact]
    public async Task Moving_through_the_page_selects_then_picks_and_cancel_clears()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 30);
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);
        string objectId = ForestIds.ObjectPrefix + "1";

        await ctx.SendActionAsync(page, UiAction.Click(objectId), CancellationToken.None);
        UiPage selected = await RenderPageAsync(ctx);
        Assert.True(Garden(selected).AllowPick);
        Assert.True(Assert.Single(Garden(selected).Items).IsSelected);
        Assert.NotNull(ButtonWithId(selected, ForestIds.Cancel));

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Cancel), CancellationToken.None);
        Assert.False(Garden(await RenderPageAsync(ctx)).AllowPick);

        await ctx.SendActionAsync(page, UiAction.Click(objectId), CancellationToken.None);
        await ctx.SendActionAsync(page, Pick(4, 1), CancellationToken.None);
        await ctx.SendActionAsync(page, new UiAction(ForestIds.Scene, UiActionKind.Pick, null, new Dictionary<string, string> { ["column"] = "x", ["row"] = "-1" }), CancellationToken.None);

        UiSceneItem moved = Assert.Single(Garden(await RenderPageAsync(ctx)).Items);
        Assert.Equal((4, 1, false), (moved.Column, moved.Row, moved.IsSelected));
    }

    [Fact]
    public async Task A_selected_withered_tree_offers_removal_that_needs_the_fee()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 30, SessionEndReason.BreakGlass);
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ObjectPrefix + "1"), CancellationToken.None);

        UiPage withCoinsMissing = await RenderPageAsync(ctx);
        Assert.False(ButtonWithId(withCoinsMissing, ForestIds.Remove).IsEnabled);
        Assert.Equal("Withered Oak · 30 min · Jan 1", Assert.Single(Garden(withCoinsMissing).Items).Label);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Remove), CancellationToken.None);
        Assert.Single((await LoadAsync(ctx)).GetView().Objects);

        await RunSessionAsync(ctx, 30);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Remove), CancellationToken.None);

        ForestView view = (await LoadAsync(ctx)).GetView();
        Assert.Equal(30 - ForestLimits.WitheredRemovalFee, view.Coins);
        Assert.Equal(ForestStage.Young, Assert.Single(view.Objects).StageValue);
    }

    [Fact]
    public async Task Choosing_an_owned_decoration_places_it_on_the_next_picked_tile()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 60);
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("forest-buy-mushrooms"), CancellationToken.None);

        UiPage inventory = await RenderPageAsync(ctx);
        UiButton entry = ButtonWithId(inventory, "forest-place-mushrooms");
        Assert.Equal("Mushrooms ×1", entry.Label);

        await ctx.SendActionAsync(page, UiAction.Click("forest-place-mushrooms"), CancellationToken.None);
        Assert.True(Garden(await RenderPageAsync(ctx)).AllowPick);

        await ctx.SendActionAsync(page, Pick(0, 5), CancellationToken.None);

        UiPage done = await RenderPageAsync(ctx);
        Assert.Contains(Garden(done).Items, i => i is { Column: 0, Row: 5 } && i.Model == "mushrooms" && i.Label == "Mushrooms");
        Assert.DoesNotContain(Descendants(done).OfType<UiButton>(), b => b.Id == "forest-place-mushrooms");
        Assert.True(UiTreeValidator.Validate(done).IsValid);
    }

    [Fact]
    public async Task The_example_shows_a_valid_read_only_sample_forest_and_stores_nothing()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(InsightsPage.StatsTabId), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(InsightsPage.ExampleButtonId), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(InsightsPage.ForestTabId), CancellationToken.None);

        UiPage example = await RenderPageAsync(ctx);
        await ctx.SendActionAsync(page, UiAction.Click("forest-unlock-birch"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click("forest-buy-rock"), CancellationToken.None);

        Assert.True(UiTreeValidator.Validate(example).IsValid);
        UiScene garden = Garden(example);
        Assert.True(garden.Items.Count > 30);
        Assert.False(garden.AllowPick);
        Assert.All(Descendants(example).OfType<UiButton>().Where(b => b.Id.StartsWith("forest-", StringComparison.Ordinal) && !b.Id.StartsWith(ForestIds.ShopTabPrefix, StringComparison.Ordinal)), b => Assert.False(b.IsEnabled));
        Assert.Equal(UiJson.Serialize(example), UiJson.Serialize(await RenderPageAsync(ctx)));
        Assert.Empty(await ctx.Storage.ListKeysAsync(null, CancellationToken.None));
    }

    [Fact]
    public void The_sample_forest_is_deterministic_and_fits_its_field()
    {
        ForestView a = ForestSampleData.Create(1000, TimeZoneInfo.Utc);
        ForestView b = ForestSampleData.Create(1000, TimeZoneInfo.Utc);

        Assert.Equal(a.Objects.Select(o => (o.Id, o.Species, o.Stage, o.Column, o.Row)), b.Objects.Select(o => (o.Id, o.Species, o.Stage, o.Column, o.Row)));
        Assert.All(a.Objects, o => Assert.True(o.Column < a.Size && o.Row < a.Size));
        Assert.Equal(a.Objects.Count, a.Objects.Select(o => (o.Column, o.Row)).Distinct().Count());
        Assert.Contains(a.Objects, o => o.StageValue == ForestStage.Withered);
        Assert.Contains(a.Objects, o => ForestCatalog.Find(o.Species)!.Kind == ForestKind.Decoration);
    }

    [Fact]
    public void The_example_forest_scene_has_a_model_of_the_right_species_for_every_plant_and_decoration_and_fits_the_document_limit()
    {
        ForestView view = ForestSampleData.Create(1000, TimeZoneInfo.Utc);
        string[] speciesById = [.. view.Objects.Select(o => o.Species)];

        foreach (ShopCategory category in Enum.GetValues<ShopCategory>())
        {
            UiPage page = Ui.Page("Forest", [.. ForestLayout.Body(view, TimeZoneInfo.Utc, false, category)]);
            UiScene garden = Descendants(page).OfType<UiScene>().Single(s => s.Id == ForestIds.Scene);

            Assert.Equal(view.Objects.Count, garden.Items.Count);
            foreach (ForestObject o in view.Objects)
            {
                UiSceneItem item = garden.Items.Single(i => i.Id == ForestIds.ObjectPrefix + o.Id);
                Assert.True(item.Model == o.Species || item.Model.StartsWith(o.Species + "-", StringComparison.Ordinal), $"{o.Species} #{o.Id} is drawn as {item.Model}.");
            }

            Assert.True(UiTreeValidator.Validate(page).IsValid);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(UiJson.Serialize(page)) <= UiJson.MaxDocumentBytes, $"The {category} page is over the document limit.");
        }

        Assert.True(speciesById.Distinct().Count() >= 20);
    }

    [Fact]
    public async Task The_stats_tab_is_still_reachable_and_the_tabs_switch_back_and_forth()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);

        await ctx.SendActionAsync(page, UiAction.Click(InsightsPage.StatsTabId), CancellationToken.None);
        UiPage stats = await RenderPageAsync(ctx);
        Assert.Contains(Descendants(stats).OfType<UiCard>(), c => c.Title == "No data yet");
        Assert.Empty(Descendants(stats).OfType<UiScene>());

        await ctx.SendActionAsync(page, UiAction.Click(InsightsPage.ForestTabId), CancellationToken.None);
        Assert.NotEmpty(Descendants(await RenderPageAsync(ctx)).OfType<UiScene>());
    }
}
