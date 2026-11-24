// Tests of the Focus Forest plugin: rules, shop, placement, field growth, session tracking, persistence, page, widget and settings.
// FakePluginContext starts at 2026-01-01T09:00:00Z with UTC as the local time zone.

using System.Text.Json;
using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Events;
using Blocker.UI.Sdk;
using Xunit;

namespace FocusForest.Tests;

/// <summary>Behaviour tests of the Focus Forest plugin.</summary>
public sealed class ForestTests
{
    private const string PluginId = "com.windowsdetox.forest";

    private static readonly PluginCapability[] AllCapabilities =
    [
        PluginCapability.SessionEvents,
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

        await new ForestPlugin().InitializeAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private static async Task<ForestStore> LoadAsync(FakePluginContext ctx)
    {
        var store = new ForestStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private static async Task<ForestStore> NewStoreAsync() => await LoadAsync(new FakePluginContext(PluginId, AllCapabilities));

    private static readonly DateTimeOffset SessionStart = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    // Drives the store directly with events, without a plugin context.
    private static async Task RunAsync(ForestStore store, int minutes, SessionEndReason reason = SessionEndReason.Expired)
    {
        Guid id = Guid.NewGuid();
        await store.OnSessionStartedAsync(new SessionStarted(id, TimeSpan.FromMinutes(minutes), SessionStart), CancellationToken.None);
        await store.OnSessionEndedAsync(new SessionEnded(id, reason, SessionStart.AddMinutes(minutes)), CancellationToken.None);
    }

    private static async Task GiveCoinsAsync(ForestStore store, int minutes) => await RunAsync(store, minutes);

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

    // ---- wiring --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_capability_throws_PluginCapabilityException()
    {
        var ctx = new FakePluginContext(PluginId, [PluginCapability.UiPage, PluginCapability.UiDashboardWidget, PluginCapability.UiSettingsSection]);

        var ex = await Assert.ThrowsAsync<PluginCapabilityException>(async () => await new ForestPlugin().InitializeAsync(ctx, CancellationToken.None));

        Assert.Equal(PluginCapability.SessionEvents, ex.Capability);
    }

    [Fact]
    public async Task Registers_page_widget_and_settings_section()
    {
        FakePluginContext ctx = await StartAsync();

        Assert.Equal(new UiNavigationItem("Forest", UiIcon.Leaf), ctx.GetExtension<IUiPagePlugin>().Navigation);
        Assert.Equal(UiWidgetSize.Small, ctx.GetExtension<IDashboardWidgetPlugin>().Size);
        Assert.Equal("Focus Forest", ctx.GetExtension<ISettingsSectionPlugin>().Title);
        Assert.Equal(3, ctx.RegisteredExtensions.Count);
    }

    [Fact]
    public void The_logo_is_valid_path_data()
    {
        Assert.True(UiTreeValidator.IsValidPathData(ForestLogo.PathData));
    }

    // ---- session tracking ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_running_session_is_stored_and_survives_a_restart()
    {
        FakePluginContext ctx = await StartAsync();
        Guid id = Guid.NewGuid();
        await ctx.PublishAsync(new SessionStarted(id, TimeSpan.FromMinutes(45), ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        string stored = Assert.IsType<string>(await ctx.Storage.ReadAsync(ForestStore.DataKey, CancellationToken.None));
        using (JsonDocument doc = JsonDocument.Parse(stored))
        {
            JsonElement pending = doc.RootElement.GetProperty("p");
            Assert.Equal((id.ToString("N"), 1767258000L, 2700), (pending.GetProperty("i").GetString(), pending.GetProperty("t").GetInt64(), pending.GetProperty("p").GetInt32()));
        }

        FakePluginContext restarted = await StartAsync(c => c.Storage.WriteAsync(ForestStore.DataKey, stored, CancellationToken.None));
        restarted.FakeTime.Advance(TimeSpan.FromMinutes(45));
        await restarted.PublishAsync(new SessionEnded(id, SessionEndReason.Expired, restarted.FakeTime.GetUtcNow()), CancellationToken.None);

        ForestView view = (await LoadAsync(restarted)).GetView();
        Assert.Equal(45, view.Coins);
        Assert.Single(view.Objects);
        using JsonDocument after = JsonDocument.Parse(Assert.IsType<string>(await restarted.Storage.ReadAsync(ForestStore.DataKey, CancellationToken.None)));
        Assert.False(after.RootElement.TryGetProperty("p", out _));
    }

    [Fact]
    public async Task An_end_without_a_matching_start_is_ignored()
    {
        FakePluginContext ctx = await StartAsync();
        await ctx.PublishAsync(new SessionStarted(Guid.NewGuid(), TimeSpan.FromMinutes(30), ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        ctx.FakeTime.Advance(TimeSpan.FromMinutes(30));

        await ctx.PublishAsync(new SessionEnded(Guid.NewGuid(), SessionEndReason.Expired, ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        ForestView view = (await LoadAsync(ctx)).GetView();
        Assert.Equal(0, view.Coins);
        Assert.True(Assert.Single(view.Objects).Growing);   // the running session's sapling is untouched
    }

    [Fact]
    public async Task The_actual_length_not_the_planned_one_is_rewarded()
    {
        ForestStore store = await NewStoreAsync();
        Guid id = Guid.NewGuid();
        await store.OnSessionStartedAsync(new SessionStarted(id, TimeSpan.FromMinutes(120), SessionStart), CancellationToken.None);

        await store.OnSessionEndedAsync(new SessionEnded(id, SessionEndReason.BreakGlass, SessionStart.AddMinutes(20).AddSeconds(59)), CancellationToken.None);

        ForestObject withered = Assert.Single(store.GetView().Objects);
        Assert.Equal((ForestStage.Withered, 20), (withered.StageValue, withered.Minutes));
    }

    [Theory]
    [InlineData("""{"v":1,"p":{"i":"","t":5,"p":60}}""")]
    [InlineData("""{"v":1,"p":{"i":"abc","t":0,"p":60}}""")]
    [InlineData("""{"v":1,"p":{"i":"abc","t":5,"p":-1}}""")]
    public async Task A_corrupt_pending_session_is_dropped(string json)
    {
        FakePluginContext ctx = await StartAsync(c => c.Storage.WriteAsync(ForestStore.DataKey, json, CancellationToken.None));
        ForestStore store = await LoadAsync(ctx);

        await store.OnSessionEndedAsync(new SessionEnded(Guid.Parse("abc00000000000000000000000000000"), SessionEndReason.Expired, SessionStart), CancellationToken.None);

        Assert.Empty(store.GetView().Objects);
    }

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
        using JsonDocument doc = JsonDocument.Parse(Assert.IsType<string>(await ctx.Storage.ReadAsync(ForestStore.DataKey, CancellationToken.None)));
        Assert.Equal(0, doc.RootElement.GetProperty("o").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("p", out _));
    }

    [Fact]
    public async Task The_chosen_species_is_planted()
    {
        ForestStore store = await NewStoreAsync();

        Assert.True(await store.SelectSpeciesAsync("pine", CancellationToken.None));
        await GiveCoinsAsync(store, 30);
        await RunAsync(store, 20, SessionEndReason.BreakGlass);

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
    public async Task New_plants_are_placed_by_the_layout_and_never_move_when_another_one_is_added()
    {
        ForestStore store = await NewStoreAsync();

        for (int i = 0; i < 6; i++)
        {
            await GiveCoinsAsync(store, 10);
        }

        ForestView before = store.GetView();
        await GiveCoinsAsync(store, 10);
        ForestView after = store.GetView();

        Assert.Equal(6, before.Objects.Select(o => (o.Column, o.Row)).Distinct().Count());
        Assert.DoesNotContain(before.Objects, o => (o.Column, o.Row) == (before.Size / 2, before.Size / 2));
        Assert.Equal(before.Objects.Select(o => (o.Id, o.Column, o.Row)), after.Objects.Take(6).Select(o => (o.Id, o.Column, o.Row)));
    }

    [Fact]
    public async Task A_bought_decoration_is_placed_by_clicking_an_empty_tile()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 130);
        await store.BuyDecorationAsync("bench", CancellationToken.None);

        ForestObject oak = store.GetView().Objects.Single();
        int size = store.GetView().Size;
        (int column, int row) = (oak.Column == 0 && oak.Row == 0) ? (1, 0) : (0, 0);

        store.TogglePlacing("bench");
        Assert.True(store.GetView().IsPicking);
        Assert.False(await store.PickAsync(oak.Column, oak.Row, CancellationToken.None));   // occupied by the oak
        Assert.False(await store.PickAsync(size / 2, size / 2, CancellationToken.None));    // the plaza belongs to the village
        Assert.False(await store.PickAsync(size, 0, CancellationToken.None));               // outside the field
        Assert.True(await store.PickAsync(column, row, CancellationToken.None));

        ForestView view = store.GetView();
        Assert.Equal("bench", ObjectAt(view, column, row).Species);
        Assert.True(ObjectAt(view, column, row).Pinned);
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
        await RunAsync(store, 20, SessionEndReason.BreakGlass);
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
        await RunAsync(store, 20, SessionEndReason.BreakGlass);

        Assert.False(await store.RemoveWitheredAsync(store.GetView().Objects[0].Id, CancellationToken.None));
        Assert.Single(store.GetView().Objects);
    }

    // ---- growing field -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_field_grows_when_less_than_a_third_of_it_would_stay_free_and_objects_keep_their_place()
    {
        ForestStore store = await NewStoreAsync();
        for (int i = 0; i < 20; i++)
        {
            await GiveCoinsAsync(store, 10);
        }

        ForestView before = store.GetView();
        Assert.Equal(6, before.Size);
        (int, int)[] tiles = [.. before.Objects.Select(o => (o.Column, o.Row))];

        for (int i = 0; i < 6; i++)
        {
            await GiveCoinsAsync(store, 10);
        }

        ForestView after = store.GetView();
        Assert.Equal(8, after.Size);
        Assert.Equal(26, after.Objects.Count);
        Assert.Equal(tiles.Select(t => (t.Item1 + 1, t.Item2 + 1)), after.Objects.Take(20).Select(o => (o.Column, o.Row)));
        Assert.Equal(26, after.Objects.Select(o => (o.Column, o.Row)).Distinct().Count());
        Assert.All(after.Objects, o => Assert.InRange(o.Column, 0, 7));
        Assert.True(after.Objects.Count + after.Paths.Count <= after.Size * after.Size * 0.65);
    }

    [Fact]
    public async Task The_field_stops_at_sixteen_and_the_forest_stops_taking_plants_when_it_is_full()
    {
        FakePluginContext ctx = await StartAsync();
        for (int i = 0; i < ForestLimits.MaxObjects + 4; i++)
        {
            await RunSessionAsync(ctx, 1);
        }

        ForestView view = (await LoadAsync(ctx)).GetView();

        Assert.Equal(ForestLimits.MaxSize, view.Size);
        Assert.Equal((ForestLimits.MaxSize * ForestLimits.MaxSize) - 1, view.Objects.Count);   // every tile but the plaza
        Assert.Equal(view.Objects.Count, view.Objects.Select(o => (o.Column, o.Row)).Distinct().Count());
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Level == PluginLogLevel.Warning);
        Assert.True(UiTreeValidator.Validate(await RenderPageAsync(ctx)).IsValid);
    }

    // ---- village: paths, pins, arranging -------------------------------------------------------------------------

    [Fact]
    public async Task A_removed_decoration_returns_to_the_inventory_without_refund_and_its_path_goes_away()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 400);
        await store.BuyDecorationAsync("well", CancellationToken.None);
        (int column, int row) = await PlaceAsync(store, "well", 0, 0);
        int coins = store.GetView().Coins;
        int wellId = store.GetView().Objects.Single(o => (o.Column, o.Row) == (column, row)).Id;
        int paths = store.GetView().Paths.Count;
        Assert.True(paths > 1);

        Assert.True(await store.RemoveDecorationAsync(wellId, CancellationToken.None));

        ForestView view = store.GetView();
        Assert.DoesNotContain(view.Objects, o => o.Id == wellId);
        Assert.Equal(1, view.Inventory["well"]);
        Assert.Equal(coins, view.Coins);
        Assert.Single(view.Paths);   // only the plaza is left
    }

    [Fact]
    public async Task Removing_a_plant_or_an_unknown_id_as_a_decoration_does_nothing()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 30);
        int plantId = store.GetView().Objects.Single().Id;

        Assert.False(await store.RemoveDecorationAsync(plantId, CancellationToken.None));
        Assert.False(await store.RemoveDecorationAsync(9999, CancellationToken.None));

        Assert.Single(store.GetView().Objects);
        Assert.Empty(store.GetView().Inventory);
    }

    private static async Task<ForestStore> VillageAsync()
    {
        ForestStore store = await NewStoreAsync();
        await GiveCoinsAsync(store, 2000);
        foreach (string decoration in new[] { "bench", "lantern", "flowers", "well", "pond", "cabin" })
        {
            await store.BuyDecorationAsync(decoration, CancellationToken.None);
        }

        for (int i = 0; i < 8; i++)
        {
            await GiveCoinsAsync(store, 30);
        }

        return store;
    }

    // The free tile (not the plaza) nearest to the wanted one.
    private static (int Column, int Row) FreeTileNear(ForestView view, int column, int row)
    {
        HashSet<(int, int)> taken = [.. view.Objects.Select(o => (o.Column, o.Row)), (view.Size / 2, view.Size / 2)];
        return Enumerable.Range(0, view.Size * view.Size)
            .Select(i => (Column: i % view.Size, Row: i / view.Size))
            .Where(t => !taken.Contains(t))
            .OrderBy(t => Math.Abs(t.Column - column) + Math.Abs(t.Row - row))
            .First();
    }

    private static async Task<(int Column, int Row)> PlaceAsync(ForestStore store, string decoration, int column, int row)
    {
        (int Column, int Row) tile = FreeTileNear(store.GetView(), column, row);
        store.TogglePlacing(decoration);
        Assert.True(await store.PickAsync(tile.Column, tile.Row, CancellationToken.None));
        return tile;
    }

    private static HashSet<(int, int)> PathTilesOf(ForestView view) => [.. view.Paths.Select(p => (p.Column, p.Row))];

    [Fact]
    public async Task Decorations_get_paths_that_lead_to_the_plaza_and_plants_keep_off_them()
    {
        ForestStore store = await VillageAsync();
        await PlaceAsync(store, "cabin", 0, 0);
        await PlaceAsync(store, "well", 1, 5);

        ForestView view = store.GetView();

        HashSet<(int, int)> paths = PathTilesOf(view);
        Assert.Contains((view.Size / 2, view.Size / 2), paths);
        Assert.DoesNotContain(view.Objects, o => paths.Contains((o.Column, o.Row)));
        foreach (ForestObject decoration in view.Objects.Where(o => ForestCatalog.Find(o.Species)!.Kind == ForestKind.Decoration))
        {
            Assert.True(new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(d => paths.Contains((decoration.Column + d.Item1, decoration.Row + d.Item2))), decoration.Species);
        }
    }

    [Fact]
    public async Task A_manual_move_pins_the_object_and_paths_route_around_it()
    {
        ForestStore store = await VillageAsync();
        ForestObject tree = store.GetView().Objects.First();
        (int column, int row) = FreeTileNear(store.GetView(), 0, 5);
        store.ToggleSelect(tree.Id);

        Assert.True(await store.PickAsync(column, row, CancellationToken.None));

        ForestView view = store.GetView();
        ForestObject moved = view.Objects.Single(o => o.Id == tree.Id);
        Assert.Equal((column, row, true), (moved.Column, moved.Row, moved.Pinned));
        Assert.DoesNotContain((column, row), PathTilesOf(view));
        Assert.Single(view.Objects, o => o.Pinned);
    }

    [Fact]
    public async Task Arranging_lays_out_the_unpinned_objects_again_and_keeps_the_pinned_ones()
    {
        ForestStore store = await VillageAsync();
        int pinnedId = store.GetView().Objects.First().Id;
        (int column, int row) = FreeTileNear(store.GetView(), 0, 5);
        store.ToggleSelect(pinnedId);
        await store.PickAsync(column, row, CancellationToken.None);
        ForestView before = store.GetView();

        Assert.True(await store.ArrangeAsync(unpinAll: false, CancellationToken.None));

        ForestView after = store.GetView();
        ForestObject pinned = after.Objects.Single(o => o.Id == pinnedId);
        Assert.Equal((column, row, true), (pinned.Column, pinned.Row, pinned.Pinned));
        Assert.Equal(before.Objects.Count, after.Objects.Count);
        Assert.Equal(after.Objects.Count, after.Objects.Select(o => (o.Column, o.Row)).Distinct().Count());
        Assert.DoesNotContain(after.Objects, o => PathTilesOf(after).Contains((o.Column, o.Row)));
    }

    [Fact]
    public async Task Arranging_with_unpin_all_releases_every_pin_and_equals_a_fresh_layout_of_the_same_objects()
    {
        ForestStore store = await VillageAsync();
        await PlaceAsync(store, "cabin", 0, 0);
        await store.SetPinnedAsync(store.GetView().Objects.First().Id.ToString(), true, CancellationToken.None);

        Assert.True(await store.ArrangeAsync(unpinAll: true, CancellationToken.None));

        ForestView view = store.GetView();
        Assert.All(view.Objects, o => Assert.False(o.Pinned));
        WorldObject[] world = [.. view.Objects.Select(o => new WorldObject(o.Id, o.Species, ForestCatalog.Find(o.Species)!.Kind, o.StageValue, null, false))];
        WorldLayout fresh = ForestWorld.Arrange(ForestLimits.StartSize, world);
        Assert.Equal(fresh.Size, view.Size);
        Assert.Equal(view.Objects.Select(o => fresh.Positions[o.Id]), view.Objects.Select(o => new GridTile(o.Column, o.Row)));
        Assert.Equal(fresh.Paths, view.Paths);
    }

    // ---- growing sapling -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_session_plants_a_growing_sapling_of_the_chosen_species_that_grows_up_on_completion()
    {
        FakePluginContext ctx = await StartAsync();
        ForestStore store = await LoadAsync(ctx);
        await GiveCoinsAsync(store, 400);
        await store.UnlockAsync("birch", CancellationToken.None);
        await store.SelectSpeciesAsync("birch", CancellationToken.None);
        int before = store.GetView().Objects.Count;
        Guid id = Guid.NewGuid();

        await store.OnSessionStartedAsync(new SessionStarted(id, TimeSpan.FromMinutes(60), SessionStart), CancellationToken.None);

        ForestView running = store.GetView();
        ForestObject sapling = running.Objects.Single(o => o.Growing);
        Assert.Equal(before + 1, running.Objects.Count);
        Assert.Equal(("birch", ForestStage.Sapling, false), (sapling.Species, sapling.StageValue, sapling.Pinned));
        Assert.DoesNotContain(PathTilesOf(running), t => t == (sapling.Column, sapling.Row));

        await store.OnSessionEndedAsync(new SessionEnded(id, SessionEndReason.Expired, SessionStart.AddMinutes(60)), CancellationToken.None);

        ForestView done = store.GetView();
        ForestObject grown = done.Objects.Single(o => o.Id == sapling.Id);
        Assert.Equal(before + 1, done.Objects.Count);
        Assert.Equal((false, ForestStage.Mature, 60, sapling.Column, sapling.Row), (grown.Growing, grown.StageValue, grown.Minutes, grown.Column, grown.Row));
    }

    [Fact]
    public async Task Break_glass_withers_the_growing_sapling_and_any_other_end_takes_it_away()
    {
        ForestStore store = await NewStoreAsync();
        Guid first = Guid.NewGuid();
        await store.OnSessionStartedAsync(new SessionStarted(first, TimeSpan.FromMinutes(60), SessionStart), CancellationToken.None);
        int saplingId = store.GetView().Objects.Single().Id;

        await store.OnSessionEndedAsync(new SessionEnded(first, SessionEndReason.BreakGlass, SessionStart.AddMinutes(20)), CancellationToken.None);

        ForestObject withered = Assert.Single(store.GetView().Objects);
        Assert.Equal((saplingId, ForestStage.Withered, 20, false), (withered.Id, withered.StageValue, withered.Minutes, withered.Growing));

        Guid second = Guid.NewGuid();
        await store.OnSessionStartedAsync(new SessionStarted(second, TimeSpan.FromMinutes(60), SessionStart), CancellationToken.None);
        Assert.Equal(2, store.GetView().Objects.Count);
        await store.OnSessionEndedAsync(new SessionEnded(second, SessionEndReason.DeveloperAbort, SessionStart.AddMinutes(5)), CancellationToken.None);

        Assert.Equal([saplingId], store.GetView().Objects.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task The_growing_sapling_survives_a_restart_and_a_new_start_replaces_a_stale_one()
    {
        FakePluginContext ctx = await StartAsync();
        Guid id = Guid.NewGuid();
        await ctx.PublishAsync(new SessionStarted(id, TimeSpan.FromMinutes(45), ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        ForestStore reloaded = await LoadAsync(ctx);
        ForestObject sapling = reloaded.GetView().Objects.Single();
        Assert.True(sapling.Growing);

        await reloaded.OnSessionStartedAsync(new SessionStarted(Guid.NewGuid(), TimeSpan.FromMinutes(10), SessionStart), CancellationToken.None);

        ForestObject replacement = reloaded.GetView().Objects.Single();
        Assert.True(replacement.Growing);
        Assert.NotEqual(sapling.Id, replacement.Id);
    }

    [Fact]
    public async Task Resetting_during_a_session_plants_a_fresh_growing_sapling()
    {
        ForestStore store = await NewStoreAsync();
        Guid id = Guid.NewGuid();
        await store.OnSessionStartedAsync(new SessionStarted(id, TimeSpan.FromMinutes(30), SessionStart), CancellationToken.None);
        await store.ResetAsync(CancellationToken.None);

        Assert.True(Assert.Single(store.GetView().Objects).Growing);

        await store.OnSessionEndedAsync(new SessionEnded(id, SessionEndReason.Expired, SessionStart.AddMinutes(30)), CancellationToken.None);

        ForestObject grown = Assert.Single(store.GetView().Objects);
        Assert.Equal((false, ForestStage.Young), (grown.Growing, grown.StageValue));
    }

    // ---- streak and persistence of the new fields ----------------------------------------------------------------

    [Fact]
    public async Task Consecutive_days_add_ten_percent_each_up_to_fifty_and_a_gap_starts_over()
    {
        FakePluginContext ctx = await StartAsync();
        int[] earned = new int[8];
        for (int day = 0; day < 8; day++)
        {
            int before = (await LoadAsync(ctx)).GetView().Coins;
            await RunSessionAsync(ctx, 100);
            earned[day] = (await LoadAsync(ctx)).GetView().Coins - before;
            ctx.FakeTime.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(100));
        }

        Assert.Equal([100, 110, 120, 130, 140, 150, 150, 150], earned);

        ctx.FakeTime.Advance(TimeSpan.FromDays(2));
        int coins = (await LoadAsync(ctx)).GetView().Coins;
        await RunSessionAsync(ctx, 100);

        ForestView view = (await LoadAsync(ctx)).GetView();
        Assert.Equal(coins + 100, view.Coins);
        Assert.Equal(1, view.Streak);
    }

    [Fact]
    public async Task A_second_session_on_the_same_day_does_not_raise_the_streak()
    {
        ForestStore store = await NewStoreAsync();

        await GiveCoinsAsync(store, 50);
        await GiveCoinsAsync(store, 50);

        Assert.Equal((100, 1), (store.GetView().Coins, store.GetView().Streak));
    }

    [Fact]
    public async Task Pins_growing_flag_streak_and_plant_id_survive_a_restart()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 60);
        ForestStore first = await LoadAsync(ctx);
        await first.OnSessionStartedAsync(new SessionStarted(Guid.NewGuid(), TimeSpan.FromMinutes(30), ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        int pinnedId = first.GetView().Objects.First(o => !o.Growing).Id;
        await first.SetPinnedAsync(pinnedId.ToString(), true, CancellationToken.None);

        ForestView expected = first.GetView();
        ForestView reloaded = (await LoadAsync(ctx)).GetView();

        Assert.Equal(
            expected.Objects.Select(o => (o.Id, o.Column, o.Row, o.Pinned, o.Growing)),
            reloaded.Objects.Select(o => (o.Id, o.Column, o.Row, o.Pinned, o.Growing)));
        Assert.Equal(expected.Paths, reloaded.Paths);
        Assert.Equal(expected.Streak, reloaded.Streak);
        Assert.Equal(expected.Size, reloaded.Size);
        using JsonDocument doc = JsonDocument.Parse((await ctx.Storage.ReadAsync(ForestStore.DataKey, CancellationToken.None))!);
        Assert.Contains(doc.RootElement.GetProperty("o").EnumerateArray(), o => o.TryGetProperty("pn", out JsonElement pn) && pn.GetBoolean());
        Assert.Contains(doc.RootElement.GetProperty("o").EnumerateArray(), o => o.TryGetProperty("gw", out JsonElement gw) && gw.GetBoolean());
        Assert.True(doc.RootElement.GetProperty("p").GetProperty("o").GetInt32() > 0);
    }

    [Fact]
    public async Task Documents_written_before_paths_and_pins_load_and_are_laid_out_on_load()
    {
        const string json = """{"v":1,"c":40,"z":6,"n":4,"o":[{"i":1,"k":"oak","g":2,"c":3,"r":3,"m":50},{"i":2,"k":"pine","g":1,"c":1,"r":1,"m":30},{"i":3,"k":"bench","g":2,"c":2,"r":3}]}""";
        FakePluginContext ctx = await StartAsync(c => c.Storage.WriteAsync(ForestStore.DataKey, json, CancellationToken.None));

        ForestView view = (await LoadAsync(ctx)).GetView();

        Assert.Equal(40, view.Coins);
        Assert.Equal(3, view.Objects.Count);
        Assert.All(view.Objects, o => Assert.False(o.Pinned || o.Growing));
        Assert.Equal((1, 1), (view.Objects.Single(o => o.Id == 2).Column, view.Objects.Single(o => o.Id == 2).Row));   // kept: not on a path
        Assert.DoesNotContain(view.Objects, o => PathTilesOf(view).Contains((o.Column, o.Row)));
        Assert.Contains((view.Size / 2, view.Size / 2), PathTilesOf(view));
    }

    [Fact]
    public async Task A_growing_flag_without_a_running_session_is_dropped_on_load()
    {
        const string json = """{"v":1,"o":[{"i":1,"k":"oak","g":0,"c":1,"r":1,"gw":true},{"i":2,"k":"pine","g":2,"c":2,"r":2}]}""";
        FakePluginContext ctx = await StartAsync(c => c.Storage.WriteAsync(ForestStore.DataKey, json, CancellationToken.None));

        ForestView view = (await LoadAsync(ctx)).GetView();

        Assert.Equal([2], view.Objects.Select(o => o.Id).ToArray());
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

        await RunAsync(store, 30);

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

            Assert.Equal(view.Objects.Count, garden.Items.Count(i => i.Id.StartsWith(ForestIds.ObjectPrefix, StringComparison.Ordinal)));
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

    // ---- widget and settings -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_widget_shows_plants_and_coins_and_an_empty_hint()
    {
        FakePluginContext ctx = await StartAsync();
        IDashboardWidgetPlugin widget = ctx.GetExtension<IDashboardWidgetPlugin>();

        UiNode empty = await ctx.RenderAsync(widget, CancellationToken.None);
        Assert.Contains(Descendants(empty).OfType<UiText>(), t => t.Text == "Finish a session to plant your first tree.");

        await RunSessionAsync(ctx, 30);
        UiNode filled = await ctx.RenderAsync(widget, CancellationToken.None);
        UiStatTile tile = Descendants(filled).OfType<UiStatTile>().Single();
        Assert.Equal(("1 plant", "30 coins"), (tile.Value, tile.Caption));
        Assert.DoesNotContain(Descendants(filled).OfType<UiText>(), t => t.Text == "Finish a session to plant your first tree.");
    }

    [Fact]
    public async Task Resetting_needs_a_second_click_and_starts_a_fresh_forest()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        await RunSessionAsync(ctx, 30);
        await ctx.RenderAsync(settings, CancellationToken.None);   // gives the section its surface
        int before = ctx.RenderRequests;

        await ctx.SendActionAsync(settings, UiAction.Click(ForestSettings.ResetButtonId), CancellationToken.None);

        Assert.Equal(30, (await LoadAsync(ctx)).GetView().Coins);   // armed only
        Assert.Equal(before + 1, ctx.RenderRequests);
        Assert.Equal("Reset forest? Click again", Descendants(await ctx.RenderAsync(settings, CancellationToken.None)).OfType<UiButton>().Single().Label);

        await ctx.SendActionAsync(settings, UiAction.Click(ForestSettings.ResetButtonId), CancellationToken.None);

        ForestView view = (await LoadAsync(ctx)).GetView();
        Assert.Equal(0, view.Coins);
        Assert.Empty(view.Objects);
        Assert.Equal(ForestLimits.StartSize, view.Size);
        UiNode tree = await ctx.RenderAsync(settings, CancellationToken.None);
        Assert.Equal("Reset forest", Descendants(tree).OfType<UiButton>().Single().Label);
        Assert.Contains(Descendants(tree).OfType<UiText>(), t => t.Text == "Your forest is empty.");
    }

    [Fact]
    public async Task The_reset_confirmation_expires()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        await RunSessionAsync(ctx, 30);

        await ctx.SendActionAsync(settings, UiAction.Click(ForestSettings.ResetButtonId), CancellationToken.None);
        ctx.FakeTime.Advance(ForestSettings.ConfirmWindow + TimeSpan.FromSeconds(1));
        await ctx.SendActionAsync(settings, UiAction.Click(ForestSettings.ResetButtonId), CancellationToken.None);

        Assert.Equal(30, (await LoadAsync(ctx)).GetView().Coins);
    }

    [Fact]
    public async Task Resetting_keeps_a_running_session_tracked()
    {
        FakePluginContext ctx = await StartAsync();
        ISettingsSectionPlugin settings = ctx.GetExtension<ISettingsSectionPlugin>();
        Guid id = Guid.NewGuid();
        await ctx.PublishAsync(new SessionStarted(id, TimeSpan.FromMinutes(30), ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        await ctx.SendActionAsync(settings, UiAction.Click(ForestSettings.ResetButtonId), CancellationToken.None);
        await ctx.SendActionAsync(settings, UiAction.Click(ForestSettings.ResetButtonId), CancellationToken.None);
        ctx.FakeTime.Advance(TimeSpan.FromMinutes(30));
        await ctx.PublishAsync(new SessionEnded(id, SessionEndReason.Expired, ctx.FakeTime.GetUtcNow()), CancellationToken.None);

        Assert.Equal(30, (await LoadAsync(ctx)).GetView().Coins);
    }
}
