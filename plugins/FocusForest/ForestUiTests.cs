// UI tests of the Focus Forest page, widget and settings: layout, validity and every interaction flow.
// FakePluginContext starts at 2026-01-01T09:00:00Z with UTC as the local time zone.

using System.Globalization;
using System.Text;
using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Blocker.UI.Sdk;
using Xunit;

namespace FocusForest.Tests;

/// <summary>Behaviour tests of what the Focus Forest page shows and what its controls do.</summary>
public sealed class ForestUiTests
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

    private static UiButton? FindButton(UiPage page, string id) => Descendants(page).OfType<UiButton>().SingleOrDefault(b => b.Id == id);

    private static UiButton ButtonWithId(UiPage page, string id) => FindButton(page, id) ?? throw new Xunit.Sdk.XunitException($"No button {id}.");

    private static bool HasText(UiPage page, string text) => Descendants(page).OfType<UiText>().Any(t => t.Text == text);

    private static UiCard? CardTitled(UiPage page, string title) => Descendants(page).OfType<UiCard>().FirstOrDefault(c => c.Title == title);

    private static UiAction Pick(int column, int row) =>
        new(ForestIds.Scene, UiActionKind.Pick, null, new Dictionary<string, string> { ["column"] = column.ToString(CultureInfo.InvariantCulture), ["row"] = row.ToString(CultureInfo.InvariantCulture) });

    private static (int Column, int Row) FreeTile(ForestView view)
    {
        var taken = view.Objects.Select(o => (o.Column, o.Row)).Concat(view.Paths.Select(p => (p.Column, p.Row))).ToHashSet();
        for (int row = view.Size - 1; row >= 0; row--)
        {
            for (int column = view.Size - 1; column >= 0; column--)
            {
                if (!taken.Contains((column, row)))
                {
                    return (column, row);
                }
            }
        }

        throw new Xunit.Sdk.XunitException("No free tile.");
    }

    private static async Task<(FakePluginContext Ctx, IUiPagePlugin Page)> PlantedAsync(int minutes = 52)
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, minutes);
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);   // gives the page its surface
        return (ctx, page);
    }

    private static string FirstObjectId(ForestView view) => ForestIds.ObjectPrefix + view.Objects[0].Id.ToString(CultureInfo.InvariantCulture);

    // ---- layout --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_leads_with_the_stat_strip_the_big_scene_and_the_planting_choice()
    {
        (FakePluginContext ctx, _) = await PlantedAsync();

        UiPage page = await RenderPageAsync(ctx);

        Assert.True(UiTreeValidator.Validate(page).IsValid);
        Assert.Empty(Descendants(page).OfType<UiGlyph>());
        Assert.Equal(["Coins", "Plants", "Streak", "Field"], Descendants(page).OfType<UiStatTile>().Select(t => t.Label));
        UiScene garden = Garden(page);
        Assert.InRange(garden.Height, 560, 640);
        Assert.False(garden.AllowPick);
        Assert.Equal("Your forest", Assert.IsType<UiCard>(page.Children[1]).Title);
        Assert.NotNull(CardTitled(page, "Planting next: Oak"));
        Assert.Equal(UiIcon.Checkmark, ButtonWithId(page, ForestIds.ChipPrefix + "oak").Icon);
        Assert.True(ButtonWithId(page, ForestIds.ChipPrefix + "pine").IsEnabled);
        Assert.DoesNotContain(Descendants(page).OfType<UiButton>(), b => b.Variant == UiButtonVariant.Primary);   // nothing is selected yet
    }

    [Fact]
    public async Task The_page_never_has_more_than_one_primary_button()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        await ctx.SendActionAsync(page, UiAction.Click(FirstObjectId((await LoadAsync(ctx)).GetView())), CancellationToken.None);

        UiPage selected = await RenderPageAsync(ctx);

        Assert.Single(Descendants(selected).OfType<UiButton>(), b => b.Variant == UiButtonVariant.Primary);
    }

    [Fact]
    public async Task Choosing_a_chip_changes_the_species_planted_next()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ChipPrefix + "pine"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ChipPrefix + "birch"), CancellationToken.None);   // locked: ignored

        Assert.Equal("pine", (await LoadAsync(ctx)).GetView().Chosen);
        UiPage after = await RenderPageAsync(ctx);
        Assert.NotNull(CardTitled(after, "Planting next: Pine"));
        Assert.Null(FindButton(after, ForestIds.ChipPrefix + "birch"));
    }

    // ---- empty state ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_forest_explains_how_it_grows_and_shows_a_small_preview()
    {
        FakePluginContext ctx = await StartAsync();

        UiPage page = await RenderPageAsync(ctx);

        Assert.True(UiTreeValidator.Validate(page).IsValid);
        UiCard intro = Assert.IsType<UiCard>(CardTitled(page, "Grow your forest"));
        Assert.Contains(Descendants(intro).OfType<UiText>(), t => t.Text.StartsWith("Your forest is empty", StringComparison.Ordinal));
        UiScene preview = Assert.Single(Descendants(intro).OfType<UiScene>());
        Assert.Equal(ForestIds.Preview, preview.Id);
        Assert.False(preview.AllowPick);
        Assert.False(preview.AllowRotate);
        Assert.NotEmpty(preview.Items);
        Assert.DoesNotContain(Descendants(page).OfType<UiScene>(), s => s.Id == ForestIds.Scene);
        Assert.Null(CardTitled(page, "Your forest"));
        Assert.Null(FindButton(page, ForestIds.Arrange));
        Assert.NotNull(CardTitled(page, "Shop"));
    }

    // ---- selection, move, pin ------------------------------------------------------------------------------------

    [Fact]
    public async Task Clicking_an_object_opens_the_selection_panel_with_its_facts_and_actions()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        string objectId = FirstObjectId((await LoadAsync(ctx)).GetView());

        await ctx.SendActionAsync(page, UiAction.Click(objectId), CancellationToken.None);
        UiPage selected = await RenderPageAsync(ctx);

        UiCard panel = Assert.IsType<UiCard>(CardTitled(selected, "Oak"));
        Assert.True(Assert.Single(Garden(selected).Items, i => i.Id == objectId).IsSelected);
        string[] texts = [.. Descendants(panel).OfType<UiText>().Select(t => t.Text)];
        Assert.Contains("Mature", texts);
        Assert.Contains("Jan 1", texts);
        Assert.Contains("52 min", texts);
        Assert.Contains("Automatic", texts);
        Assert.Equal(["Move", "Pin", "Close"], Descendants(panel).OfType<UiButton>().Select(b => b.Label));
        Assert.False(Garden(selected).AllowPick);   // selecting alone does not start a move

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Close), CancellationToken.None);

        UiPage closed = await RenderPageAsync(ctx);
        Assert.Null(CardTitled(closed, "Oak"));
        Assert.DoesNotContain(Garden(closed).Items, i => i.IsSelected);
    }

    [Fact]
    public async Task Move_then_picking_an_empty_tile_moves_and_pins_the_object()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        ForestView before = (await LoadAsync(ctx)).GetView();
        await ctx.SendActionAsync(page, UiAction.Click(FirstObjectId(before)), CancellationToken.None);
        (int column, int row) = FreeTile(before);

        await ctx.SendActionAsync(page, Pick(column, row), CancellationToken.None);   // not moving yet: ignored
        Assert.Equal((before.Objects[0].Column, before.Objects[0].Row), ((await LoadAsync(ctx)).GetView().Objects[0].Column, (await LoadAsync(ctx)).GetView().Objects[0].Row));

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Move), CancellationToken.None);
        UiPage moving = await RenderPageAsync(ctx);
        Assert.True(Garden(moving).AllowPick);
        Assert.True(HasText(moving, "Click an empty tile to move the oak. It will stay pinned where you put it."));
        Assert.False(ButtonWithId(moving, ForestIds.Move).IsEnabled);
        Assert.Equal("Cancel", ButtonWithId(moving, ForestIds.Cancel).Label);

        await ctx.SendActionAsync(page, Pick(column, row), CancellationToken.None);

        ForestObject moved = Assert.Single((await LoadAsync(ctx)).GetView().Objects);
        Assert.Equal((column, row, true), (moved.Column, moved.Row, moved.Pinned));
        UiPage done = await RenderPageAsync(ctx);
        Assert.False(Garden(done).AllowPick);
        Assert.Null(CardTitled(done, "Oak"));
    }

    [Fact]
    public async Task Cancel_while_moving_keeps_the_selection_and_stops_the_move()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        ForestView before = (await LoadAsync(ctx)).GetView();
        await ctx.SendActionAsync(page, UiAction.Click(FirstObjectId(before)), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Move), CancellationToken.None);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Cancel), CancellationToken.None);

        UiPage after = await RenderPageAsync(ctx);
        Assert.False(Garden(after).AllowPick);
        Assert.NotNull(CardTitled(after, "Oak"));
        Assert.True(ButtonWithId(after, ForestIds.Move).IsEnabled);
    }

    [Fact]
    public async Task Selecting_another_object_while_moving_ends_the_move()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        await RunSessionAsync(ctx, 30);
        ForestView view = (await LoadAsync(ctx)).GetView();
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ObjectPrefix + view.Objects[0].Id), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Move), CancellationToken.None);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ObjectPrefix + view.Objects[1].Id), CancellationToken.None);

        UiPage after = await RenderPageAsync(ctx);
        Assert.False(Garden(after).AllowPick);
        Assert.True(ButtonWithId(after, ForestIds.Move).IsEnabled);
    }

    [Fact]
    public async Task A_click_on_a_path_tile_counts_as_a_click_on_an_empty_tile_while_moving()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        await RunSessionAsync(ctx, 30);
        ForestView before = (await LoadAsync(ctx)).GetView();
        await ctx.SendActionAsync(page, UiAction.Click(FirstObjectId(before)), CancellationToken.None);

        string pathId = Garden(await RenderPageAsync(ctx)).Items.FirstOrDefault(i => i.Id.StartsWith(ForestIds.PathPrefix, StringComparison.Ordinal))?.Id ?? "path-0-0";
        await ctx.SendActionAsync(page, UiAction.Click(pathId), CancellationToken.None);   // not moving: nothing happens
        await ctx.SendActionAsync(page, UiAction.Click("path-x-y"), CancellationToken.None);

        ForestView after = (await LoadAsync(ctx)).GetView();
        Assert.Equal(before.Objects.Select(o => (o.Id, o.Column, o.Row)), after.Objects.Select(o => (o.Id, o.Column, o.Row)));
        Assert.NotNull(CardTitled(await RenderPageAsync(ctx), "Oak"));
    }

    [Fact]
    public async Task Pin_and_unpin_toggle_the_state_shown_in_the_panel()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        await ctx.SendActionAsync(page, UiAction.Click(FirstObjectId((await LoadAsync(ctx)).GetView())), CancellationToken.None);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Pin), CancellationToken.None);

        Assert.True(Assert.Single((await LoadAsync(ctx)).GetView().Objects).Pinned);
        UiPage pinned = await RenderPageAsync(ctx);
        Assert.Equal("Unpin", ButtonWithId(pinned, ForestIds.Pin).Label);
        Assert.True(HasText(pinned, "Pinned"));

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Pin), CancellationToken.None);

        Assert.False(Assert.Single((await LoadAsync(ctx)).GetView().Objects).Pinned);
        Assert.Equal("Pin", ButtonWithId(await RenderPageAsync(ctx), ForestIds.Pin).Label);
    }

    [Fact]
    public async Task A_growing_sapling_says_it_finishes_when_the_session_ends()
    {
        FakePluginContext ctx = await StartAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);
        await ctx.PublishAsync(new SessionStarted(Guid.NewGuid(), TimeSpan.FromMinutes(25), ctx.FakeTime.GetUtcNow()), CancellationToken.None);
        ForestObject sapling = Assert.Single((await LoadAsync(ctx)).GetView().Objects, o => o.Growing);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ObjectPrefix + sapling.Id), CancellationToken.None);
        UiPage selected = await RenderPageAsync(ctx);

        Assert.True(HasText(selected, "Growing — finishes when your session ends."));
        Assert.True(HasText(selected, "Growing"));
        Assert.Null(FindButton(selected, ForestIds.Remove));
        Assert.True(UiTreeValidator.Validate(selected).IsValid);
    }

    [Fact]
    public async Task A_withered_tree_offers_removal_with_the_fee_and_a_missing_coins_hint()
    {
        FakePluginContext ctx = await StartAsync();
        await RunSessionAsync(ctx, 30, SessionEndReason.BreakGlass);
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await ctx.RenderAsync(page, CancellationToken.None);
        ForestView view = (await LoadAsync(ctx)).GetView();
        await ctx.SendActionAsync(page, UiAction.Click(FirstObjectId(view)), CancellationToken.None);

        UiPage broke = await RenderPageAsync(ctx);

        Assert.NotNull(CardTitled(broke, "Withered Oak"));
        UiButton remove = ButtonWithId(broke, ForestIds.Remove);
        Assert.Equal($"Remove ({ForestLimits.WitheredRemovalFee} coins)", remove.Label);
        Assert.False(remove.IsEnabled);
        Assert.True(HasText(broke, $"Need {ForestLimits.WitheredRemovalFee - view.Coins} more coins to remove it."));

        await RunSessionAsync(ctx, 30);
        UiPage funded = await RenderPageAsync(ctx);
        Assert.True(ButtonWithId(funded, ForestIds.Remove).IsEnabled);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Remove), CancellationToken.None);

        Assert.DoesNotContain((await LoadAsync(ctx)).GetView().Objects, o => o.StageValue == ForestStage.Withered);
    }

    [Fact]
    public async Task A_placed_decoration_can_go_back_to_the_inventory()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync(60);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ShopTab(ShopCategory.Decorations)), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.BuyPrefix + "mushrooms"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.PlacePrefix + "mushrooms"), CancellationToken.None);
        (int column, int row) = FreeTile((await LoadAsync(ctx)).GetView());
        await ctx.SendActionAsync(page, Pick(column, row), CancellationToken.None);
        ForestObject mushrooms = (await LoadAsync(ctx)).GetView().Objects.Single(o => o.Species == "mushrooms");
        int coins = (await LoadAsync(ctx)).GetView().Coins;

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ObjectPrefix + mushrooms.Id.ToString(CultureInfo.InvariantCulture)), CancellationToken.None);
        UiButton back = ButtonWithId(await RenderPageAsync(ctx), ForestIds.Remove);
        Assert.Equal("Back to inventory", back.Label);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Remove), CancellationToken.None);

        ForestView after = (await LoadAsync(ctx)).GetView();
        Assert.DoesNotContain(after.Objects, o => o.Species == "mushrooms");
        Assert.Equal(coins, after.Coins);
        Assert.Equal("Place Mushrooms ×1", ButtonWithId(await RenderPageAsync(ctx), ForestIds.PlacePrefix + "mushrooms").Label);
    }

    // ---- arrange -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Arrange_keeps_pins_and_release_needs_a_second_click_within_the_window()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        await RunSessionAsync(ctx, 30);
        ForestView view = (await LoadAsync(ctx)).GetView();
        await ctx.SendActionAsync(page, UiAction.Click(FirstObjectId(view)), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Pin), CancellationToken.None);
        UiPage idle = await RenderPageAsync(ctx);
        Assert.Equal("Arrange world", ButtonWithId(idle, ForestIds.Arrange).Label);
        Assert.Equal("Release all pins and arrange", ButtonWithId(idle, ForestIds.Release).Label);
        ForestObject pinned = (await LoadAsync(ctx)).GetView().Objects.Single(o => o.Pinned);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Arrange), CancellationToken.None);

        ForestObject afterArrange = (await LoadAsync(ctx)).GetView().Objects.Single(o => o.Pinned);
        Assert.Equal((pinned.Id, pinned.Column, pinned.Row), (afterArrange.Id, afterArrange.Column, afterArrange.Row));

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Release), CancellationToken.None);   // arms
        Assert.Contains((await LoadAsync(ctx)).GetView().Objects, o => o.Pinned);
        UiButton armed = ButtonWithId(await RenderPageAsync(ctx), ForestIds.Release);
        Assert.Equal(("Release all pins? Click again", UiButtonVariant.Warning), (armed.Label, armed.Variant));

        ctx.FakeTime.Advance(ForestPage.ConfirmWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("Release all pins and arrange", ButtonWithId(await RenderPageAsync(ctx), ForestIds.Release).Label);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Release), CancellationToken.None);   // window over: arms again
        Assert.Contains((await LoadAsync(ctx)).GetView().Objects, o => o.Pinned);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Release), CancellationToken.None);   // confirms

        Assert.DoesNotContain((await LoadAsync(ctx)).GetView().Objects, o => o.Pinned);
        Assert.Equal("Release all pins and arrange", ButtonWithId(await RenderPageAsync(ctx), ForestIds.Release).Label);
    }

    [Fact]
    public async Task Release_is_disabled_when_nothing_is_pinned()
    {
        (FakePluginContext ctx, _) = await PlantedAsync();

        Assert.False(ButtonWithId(await RenderPageAsync(ctx), ForestIds.Release).IsEnabled);
    }

    // ---- inventory and placing -----------------------------------------------------------------------------------

    [Fact]
    public async Task Placing_a_decoration_shows_a_hint_with_cancel_and_places_it_on_the_picked_tile()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync(60);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ShopTab(ShopCategory.Decorations)), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.BuyPrefix + "mushrooms"), CancellationToken.None);
        UiPage inventory = await RenderPageAsync(ctx);
        Assert.Equal("Place Mushrooms ×1", ButtonWithId(inventory, ForestIds.PlacePrefix + "mushrooms").Label);
        Assert.NotNull(CardTitled(inventory, "Your decorations"));

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.PlacePrefix + "mushrooms"), CancellationToken.None);
        UiPage placing = await RenderPageAsync(ctx);
        Assert.True(Garden(placing).AllowPick);
        Assert.True(HasText(placing, "Click an empty tile to place the mushrooms."));
        Assert.NotNull(FindButton(placing, ForestIds.Cancel));

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.Cancel), CancellationToken.None);
        Assert.False(Garden(await RenderPageAsync(ctx)).AllowPick);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.PlacePrefix + "mushrooms"), CancellationToken.None);
        (int column, int row) = FreeTile((await LoadAsync(ctx)).GetView());
        await ctx.SendActionAsync(page, Pick(column, row), CancellationToken.None);

        UiPage done = await RenderPageAsync(ctx);
        Assert.Contains(Garden(done).Items, i => i.Model == "mushrooms" && i.Column == column && i.Row == row);
        Assert.Null(FindButton(done, ForestIds.PlacePrefix + "mushrooms"));
        Assert.True(UiTreeValidator.Validate(done).IsValid);
    }

    // ---- shop ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_shop_shows_a_state_a_price_and_a_thumbnail_for_every_entry()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync(160);

        UiPage trees = await RenderPageAsync(ctx);

        UiScene[] thumbnails = [.. Descendants(trees).OfType<UiScene>().Where(s => s.Id.StartsWith("forest-thumb-", StringComparison.Ordinal))];
        Assert.Equal(ForestCatalog.All.Count(s => s.Kind == ForestKind.Tree), thumbnails.Length);
        Assert.All(thumbnails, t => Assert.Equal((1, 1, false, false, 150d), (t.Columns, t.Rows, t.ShowGround, t.AllowRotate, t.Height)));
        UiCard shop = Assert.IsType<UiCard>(CardTitled(trees, "Shop"));
        string[] texts = [.. Descendants(shop).OfType<UiText>().Select(t => t.Text)];
        Assert.Contains("Free", texts);
        Assert.Contains("150 coins", texts);
        Assert.Contains("Planting next", texts);       // oak
        Assert.Contains("Unlocked", texts);            // pine
        Assert.Contains("Ready to unlock", texts);     // birch, 150 coins
        Assert.Contains("Need 90 more coins", texts);  // maple, 250 coins
        Assert.Equal("Planting", ButtonWithId(trees, ForestIds.SelectPrefix + "oak").Label);
        Assert.False(ButtonWithId(trees, ForestIds.SelectPrefix + "oak").IsEnabled);
        Assert.True(ButtonWithId(trees, ForestIds.SelectPrefix + "pine").IsEnabled);
        Assert.True(ButtonWithId(trees, ForestIds.UnlockPrefix + "birch").IsEnabled);
        Assert.False(ButtonWithId(trees, ForestIds.UnlockPrefix + "maple").IsEnabled);

        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.UnlockPrefix + "birch"), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.SelectPrefix + "birch"), CancellationToken.None);

        UiPage after = await RenderPageAsync(ctx);
        Assert.Equal("Planting", ButtonWithId(after, ForestIds.SelectPrefix + "birch").Label);
        Assert.NotNull(CardTitled(after, "Planting next: Birch"));
    }

    [Fact]
    public async Task Decorations_show_owned_counts_and_disable_buy_when_unaffordable()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync(200);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ShopTab(ShopCategory.Decorations)), CancellationToken.None);
        await ctx.SendActionAsync(page, UiAction.Click(ForestIds.BuyPrefix + "rock"), CancellationToken.None);

        UiPage page2 = await RenderPageAsync(ctx);

        Assert.Equal(ForestCatalog.All.Count(s => s.Kind == ForestKind.Decoration), Descendants(page2).OfType<UiScene>().Count(s => s.Id.StartsWith("forest-thumb-", StringComparison.Ordinal)));
        Assert.True(HasText(page2, "Owned ×1"));
        Assert.True(HasText(page2, "Not owned"));
        Assert.True(HasText(page2, "Need 740 more coins"));
        Assert.False(ButtonWithId(page2, ForestIds.BuyPrefix + "cabin").IsEnabled);
        Assert.True(ButtonWithId(page2, ForestIds.BuyPrefix + "bench").IsEnabled);
    }

    [Fact]
    public async Task Shop_tabs_switch_between_trees_bushes_and_decorations()
    {
        (FakePluginContext ctx, IUiPagePlugin page) = await PlantedAsync();
        foreach ((ShopCategory category, string id) in new[] { (ShopCategory.Bushes, "bush"), (ShopCategory.Decorations, "rock"), (ShopCategory.Trees, "oak") })
        {
            await ctx.SendActionAsync(page, UiAction.Click(ForestIds.ShopTab(category)), CancellationToken.None);

            UiPage shown = await RenderPageAsync(ctx);

            Assert.Contains(Descendants(shown).OfType<UiScene>(), s => s.Id == "forest-thumb-" + id);
            Assert.Equal(UiButtonVariant.Secondary, ButtonWithId(shown, ForestIds.ShopTab(category)).Variant);
        }
    }

    // ---- limits --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_forest_with_every_species_and_stage_stays_under_the_document_limit_on_every_tab()
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
        IUiPagePlugin plugin = ctx.GetExtension<IUiPagePlugin>();

        foreach (ShopCategory category in Enum.GetValues<ShopCategory>())
        {
            await ctx.SendActionAsync(plugin, UiAction.Click(ForestIds.ShopTab(category)), CancellationToken.None);
            await ctx.SendActionAsync(plugin, UiAction.Click(ForestIds.ObjectPrefix + "1"), CancellationToken.None);
            UiPage page = await RenderPageAsync(ctx);

            Assert.True(UiTreeValidator.Validate(page).IsValid);
            Assert.True(Encoding.UTF8.GetByteCount(UiJson.Serialize(page)) <= UiJson.MaxDocumentBytes, $"The {category} page is over the document limit.");
        }
    }

    [Fact]
    public void The_example_forest_renders_without_interactive_controls_and_fits_the_document_limit()
    {
        ForestView view = ForestSampleData.Create(1000, TimeZoneInfo.Utc);

        foreach (ShopCategory category in Enum.GetValues<ShopCategory>())
        {
            UiPage page = Ui.Page("Forest", [.. ForestLayout.Body(view, TimeZoneInfo.Utc, false, category)]);

            Assert.True(UiTreeValidator.Validate(page).IsValid);
            Assert.Null(FindButton(page, ForestIds.Arrange));
            Assert.False(Garden(page).AllowPick);
            Assert.True(Encoding.UTF8.GetByteCount(UiJson.Serialize(page)) <= UiJson.MaxDocumentBytes);
        }
    }

    // ---- widget --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_widget_leads_with_one_stat_then_a_mini_forest_without_camera_or_clicks()
    {
        (FakePluginContext ctx, _) = await PlantedAsync(30);
        IDashboardWidgetPlugin widget = ctx.GetExtension<IDashboardWidgetPlugin>();

        UiCard card = Assert.IsType<UiCard>(await ctx.RenderAsync(widget, CancellationToken.None));

        Assert.Null(card.Title);
        Assert.Null(card.Subtitle);
        Assert.Null(card.Icon);
        Assert.IsType<UiStatTile>(card.Children[0]);
        UiStatTile tile = Assert.Single(Descendants(card).OfType<UiStatTile>());
        Assert.Equal(("1 plant", "30 coins"), (tile.Value, tile.Caption));
        UiScene mini = Assert.Single(Descendants(card).OfType<UiScene>());
        Assert.False(mini.AllowRotate);
        Assert.False(mini.AllowPick);
        Assert.InRange(mini.Height, 64, 160);
        Assert.Empty(Descendants(card).OfType<UiButton>());
        Assert.Empty(Descendants(card).OfType<UiCard>().Where(c => c != card));
        Assert.Empty(Descendants(card).OfType<UiGlyph>());
    }

    [Fact]
    public async Task The_empty_widget_shows_one_calm_line_and_no_scene()
    {
        FakePluginContext ctx = await StartAsync();

        UiCard card = Assert.IsType<UiCard>(await ctx.RenderAsync(ctx.GetExtension<IDashboardWidgetPlugin>(), CancellationToken.None));

        Assert.Contains(Descendants(card).OfType<UiText>(), t => t.Text == "Finish a session to plant your first tree.");
        Assert.Empty(Descendants(card).OfType<UiScene>());
    }
}
