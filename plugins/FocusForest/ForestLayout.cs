// Focus Forest visualization: stat tiles, the isometric scene, the selection panel, the inventory and the shop. Pure: no state, no I/O.
// The scene itself is built by ForestSceneBuilder; this file only decides what surrounds it.

using System.Globalization;
using Blocker.UI.Sdk;

namespace FocusForest;

/// <summary>Ids of the forest controls; dynamic ones are a prefix followed by an object id or a catalogue id.</summary>
public static class ForestIds
{
    /// <summary>The big scene (empty-tile clicks arrive as Pick with this id).</summary>
    public const string Scene = "forest-scene";

    /// <summary>The small scene of the first-run preview.</summary>
    public const string Preview = "forest-preview";

    /// <summary>Prefix of a placed object, followed by its numeric id.</summary>
    public const string ObjectPrefix = "forest-obj-";

    /// <summary>Prefix of a path tile of the scene, followed by column and row; clicking one counts as clicking that tile.</summary>
    public const string PathPrefix = "path-";

    /// <summary>Prefix of "Unlock", followed by a catalogue id.</summary>
    public const string UnlockPrefix = "forest-unlock-";

    /// <summary>Prefix of the shop's "Plant next", followed by a catalogue id.</summary>
    public const string SelectPrefix = "forest-select-";

    /// <summary>Prefix of a "Planting next" chip, followed by a catalogue id.</summary>
    public const string ChipPrefix = "forest-chip-";

    /// <summary>Prefix of "Buy", followed by a catalogue id.</summary>
    public const string BuyPrefix = "forest-buy-";

    /// <summary>Prefix of an inventory entry, followed by a catalogue id.</summary>
    public const string PlacePrefix = "forest-place-";

    /// <summary>Drops the pending move or placement.</summary>
    public const string Cancel = "forest-cancel";

    /// <summary>Closes the selection panel.</summary>
    public const string Close = "forest-close";

    /// <summary>Starts moving the selected object: the next click on an empty tile puts it there.</summary>
    public const string Move = "forest-move";

    /// <summary>Pins or unpins the selected object.</summary>
    public const string Pin = "forest-pin";

    /// <summary>Removes the selected withered plant for a fee.</summary>
    public const string Remove = "forest-remove";

    /// <summary>Re-lays out the world, keeping pinned objects where they are.</summary>
    public const string Arrange = "forest-arrange";

    /// <summary>Releases every pin and re-lays out the world (asks for a second click).</summary>
    public const string Release = "forest-release";

    /// <summary>Id of the shop category button for <paramref name="category"/>.</summary>
    public static string ShopTab(ShopCategory category) => ShopTabPrefix + category.ToString().ToLowerInvariant();

    /// <summary>Prefix of the shop category buttons.</summary>
    public const string ShopTabPrefix = "forest-shop-";

    private const string ThumbPrefix = "forest-thumb-";

    internal static string Thumbnail(string speciesId) => ThumbPrefix + speciesId;
}

/// <summary>Which part of the catalogue the shop lists; one at a time keeps the thumbnails inside the host's document size limit.</summary>
public enum ShopCategory
{
    /// <summary>Trees.</summary>
    Trees,

    /// <summary>Bushes.</summary>
    Bushes,

    /// <summary>Decorations.</summary>
    Decorations,
}

/// <summary>What the page remembers between renders and the store does not know.</summary>
/// <param name="Moving">The selected object waits for a click on an empty tile.</param>
/// <param name="ReleaseArmed">"Release all pins and arrange" was clicked once.</param>
public sealed record ForestUiState(bool Moving = false, bool ReleaseArmed = false);

/// <summary>Builds the Forest page body.</summary>
public static class ForestLayout
{
    private const double ThumbnailHeight = 150;
    private const double SceneHeight = 600;
    private const double PlantingThumbnailHeight = 88;
    private const double PreviewHeight = 340;
    private const double MiniSceneHeight = 120;
    private const int MiniSceneObjects = 40;

    private static readonly IReadOnlyDictionary<string, string> Flavour = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["oak"] = "Sturdy and patient, like a good habit.",
        ["pine"] = "Evergreen focus, all year round.",
        ["birch"] = "Pale bark, light leaves.",
        ["maple"] = "Turns the forest copper and gold.",
        ["cherry"] = "A cloud of blossom for long sessions.",
        ["spruce"] = "Snow stays on it all winter.",
        ["willow"] = "Branches that sway when you slow down.",
        ["palm"] = "A little holiday between sessions.",
        ["apple"] = "Fruit for the focused.",
        ["cypress"] = "Tall, quiet and straight.",
        ["bush"] = "A tidy green companion.",
        ["rose"] = "Thorny, but worth it.",
        ["berry"] = "Small, round and blue.",
        ["fern"] = "Loves the shade under the trees.",
        ["rock"] = "Moss grows on whatever stays put.",
        ["mushrooms"] = "They appear when you are not looking.",
        ["flowers"] = "A splash of colour for the lawn.",
        ["bench"] = "Somewhere to rest after a session.",
        ["lantern"] = "Keeps the forest glowing at dusk.",
        ["pond"] = "Still water, still mind.",
        ["well"] = "Old stones, cold water.",
        ["cabin"] = "Your own hideout in the woods.",
    };

    /// <summary>Tiles, forest card, selection panel, planting choice, inventory and shop. With <paramref name="interactive"/> off (the example) nothing can be clicked.</summary>
    public static List<UiNode> Body(ForestView view, TimeZoneInfo zone, bool interactive, ShopCategory shop = ShopCategory.Trees, ForestUiState? state = null)
    {
        state ??= new ForestUiState();
        bool empty = view.Objects.Count == 0;
        bool moving = interactive && view.SelectedObject is not null && state.Moving;
        List<UiNode> body = [Tiles(view)];
        // The panel sits above the scene so that it is on screen when an object far down the page is clicked.
        if (interactive && !empty && view.SelectedObject is { } id && view.Objects.FirstOrDefault(o => o.Id == id) is { } selected)
        {
            body.Add(SelectionPanel(view, selected, zone, moving));
        }

        body.Add(empty ? FirstRun(view) : Garden(view, zone, interactive, moving, state));

        body.Add(Planting(view, interactive));
        if (view.Inventory.Count > 0)
        {
            body.Add(Inventory(view, interactive));
        }

        body.Add(Shop(view, interactive, shop));
        return body;
    }

    /// <summary>The small scene of the dashboard widget: the most recent plants, nothing clickable, no camera.</summary>
    public static UiScene MiniScene(ForestView view)
    {
        ForestView recent = view with { Objects = [.. view.Objects.OrderByDescending(o => o.Planted).ThenByDescending(o => o.Id).Take(MiniSceneObjects)], SelectedObject = null, PlacingDecoration = null };
        return ForestSceneBuilder.Build(recent, "forest-mini", MiniSceneHeight, allowPick: false, selectedObjectId: null) with { AllowRotate = false };
    }

    /// <summary>Tooltip of a placed object: "Oak · 52 min · Nov 22", "Withered Oak · 20 min · Nov 22" or the decoration name.</summary>
    public static string LabelOf(ForestObject o, TimeZoneInfo zone)
    {
        ForestSpecies? species = ForestCatalog.Find(o.Species);
        string name = species?.Name ?? o.Species;
        if (species is not { Kind: not ForestKind.Decoration })
        {
            return name;
        }

        string prefix = o.StageValue == ForestStage.Withered ? "Withered " : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{name} · {o.Minutes} min · {ForestFormat.DayLabel(DayOf(o, zone))}");
    }

    private static int DayOf(ForestObject o, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(o.Planted), zone).DateTime).DayNumber;

    private static bool IsPlant(ForestObject o) => ForestCatalog.Find(o.Species)?.Kind != ForestKind.Decoration;

    private static UiNode Tiles(ForestView v) => Ui.Grid(
        4,
        Ui.Stat("Coins", Short(v.Coins), "Earned by focusing", UiTone.Accent, UiIcon.Star),
        Ui.Stat("Plants", Short(v.Objects.Count(IsPlant)), v.Objects.Any(o => o.Growing) ? "One is growing" : "Trees and bushes", UiTone.Success, UiIcon.Leaf),
        Ui.Stat("Streak", Short(v.Streak), v.Streak == 1 ? "Day in a row" : "Days in a row", UiTone.Success, UiIcon.Flame),
        Ui.Stat("Field", string.Create(CultureInfo.InvariantCulture, $"{v.Size}x{v.Size}"), "Grows as you plant", UiTone.Neutral, UiIcon.Target));

    private static string Short(int n) => n < 10_000
        ? n.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{n / 1000.0:0.#}k");

    // ---- forest card -----------------------------------------------------------------------------------------------

    private static UiNode Garden(ForestView v, TimeZoneInfo zone, bool interactive, bool moving, ForestUiState state)
    {
        bool allowPick = interactive && (moving || v.PlacingDecoration is not null);
        UiScene scene = ForestSceneBuilder.Build(v, ForestIds.Scene, SceneHeight, allowPick, SelectedItemId(v), zone);
        int pinned = v.Objects.Count(o => o.Pinned);
        List<UiNode> children = [scene];
        if (interactive)
        {
            children.Add(Hint(v, moving));
            children.Add(ArrangeRow(pinned, state.ReleaseArmed));
        }

        string subtitle = string.Create(
            CultureInfo.InvariantCulture,
            $"{ForestFormat.Count(v.Objects.Count(IsPlant), "plant")} · {ForestFormat.Count(v.Objects.Count(o => !IsPlant(o)), "decoration")} · {pinned} pinned");
        return new UiCard(children, "Your forest", subtitle, UiIcon.Leaf);
    }

    private static string? SelectedItemId(ForestView v) =>
        v.SelectedObject is { } id ? ForestIds.ObjectPrefix + id.ToString(CultureInfo.InvariantCulture) : null;

    private static string NameOf(string speciesId) => ForestCatalog.Find(speciesId)?.Name ?? speciesId;

    private static UiNode Hint(ForestView v, bool moving)
    {
        if (moving && v.Objects.FirstOrDefault(o => o.Id == v.SelectedObject) is { } mover)
        {
            return Ui.Row(
                Ui.Text($"Click an empty tile to move the {NameOf(mover.Species).ToLowerInvariant()}. It will stay pinned where you put it.", UiTextStyle.Body, UiTone.Accent),
                Ui.Button(ForestIds.Cancel, "Cancel", UiButtonVariant.Secondary, UiIcon.Dismiss));
        }

        if (v.PlacingDecoration is { } decoration)
        {
            return Ui.Row(
                Ui.Text($"Click an empty tile to place the {NameOf(decoration).ToLowerInvariant()}.", UiTextStyle.Body, UiTone.Accent),
                Ui.Button(ForestIds.Cancel, "Cancel", UiButtonVariant.Secondary, UiIcon.Dismiss));
        }

        return Ui.Text(
            v.SelectedObject is null
                ? "Click a tree, bush or decoration to select it. Turn the view with the buttons at the top right."
                : "Choose Move, then click an empty tile. Pinned objects stay put when the world is arranged.",
            UiTextStyle.Caption);
    }

    private static UiNode ArrangeRow(int pinned, bool releaseArmed)
    {
        UiButton release = releaseArmed
            ? Ui.Button(ForestIds.Release, "Release all pins? Click again", UiButtonVariant.Warning, UiIcon.Warning)
            : new UiButton(ForestIds.Release, "Release all pins and arrange", UiButtonVariant.Subtle, UiIcon.Unlock, pinned > 0);
        return Ui.Row(
            Ui.Text("Arrange tidies the world: paths and gaps are redrawn, pinned objects keep their tile.", UiTextStyle.Caption),
            Ui.Button(ForestIds.Arrange, "Arrange world", UiButtonVariant.Secondary, UiIcon.Apps),
            release);
    }

    // ---- selection panel -------------------------------------------------------------------------------------------

    private static UiNode SelectionPanel(ForestView v, ForestObject o, TimeZoneInfo zone, bool moving)
    {
        ForestSpecies? species = ForestCatalog.Find(o.Species);
        bool decoration = species?.Kind == ForestKind.Decoration;
        bool withered = o.StageValue == ForestStage.Withered;
        string name = (withered ? "Withered " : string.Empty) + NameOf(o.Species);
        string day = ForestFormat.DayLabel(DayOf(o, zone));

        List<UiNode> facts =
        [
            Fact("Stage", decoration ? "Decoration" : o.Growing ? "Growing" : o.StageValue.ToString()),
            Fact(decoration ? "Placed" : "Planted", day),
        ];
        if (!decoration)
        {
            facts.Add(Fact("Session", o.Growing ? "In progress" : string.Create(CultureInfo.InvariantCulture, $"{o.Minutes} min")));
        }

        facts.Add(Fact("Position", o.Pinned ? "Pinned" : "Automatic"));

        List<UiNode> children = [Ui.Grid(facts.Count, [.. facts])];
        if (o.Growing)
        {
            children.Add(Ui.Text("Growing — finishes when your session ends.", UiTextStyle.Body, UiTone.Accent));
        }
        else if (withered)
        {
            children.Add(Ui.Text("This session ended early. Removing the tree frees its tile.", UiTextStyle.Caption));
        }

        List<UiNode> actions =
        [
            moving
                ? new UiButton(ForestIds.Move, "Click a tile…", UiButtonVariant.Primary, UiIcon.Target, false)
                : Ui.Button(ForestIds.Move, "Move", UiButtonVariant.Primary, UiIcon.Target),
            o.Pinned
                ? Ui.Button(ForestIds.Pin, "Unpin", UiButtonVariant.Secondary, UiIcon.Unlock)
                : Ui.Button(ForestIds.Pin, "Pin", UiButtonVariant.Secondary, UiIcon.Lock),
        ];
        if (withered)
        {
            int fee = ForestLimits.WitheredRemovalFee;
            actions.Add(new UiButton(ForestIds.Remove, string.Create(CultureInfo.InvariantCulture, $"Remove ({fee} coins)"), UiButtonVariant.Warning, UiIcon.Delete, v.Coins >= fee));
        }
        else if (ForestCatalog.Find(o.Species)?.Kind == ForestKind.Decoration)
        {
            actions.Add(Ui.Button(ForestIds.Remove, "Back to inventory", UiButtonVariant.Secondary, UiIcon.Delete));
        }

        actions.Add(Ui.Button(ForestIds.Close, "Close", UiButtonVariant.Subtle, UiIcon.Dismiss));
        children.Add(new UiStack(actions, UiOrientation.Horizontal));
        if (withered && v.Coins < ForestLimits.WitheredRemovalFee)
        {
            children.Add(Ui.Text(string.Create(CultureInfo.InvariantCulture, $"Need {ForestLimits.WitheredRemovalFee - v.Coins} more coins to remove it."), UiTextStyle.Caption));
        }

        return new UiCard(children, name, SelectionSubtitle(o, species), UiIcon.Leaf);
    }

    private static string SelectionSubtitle(ForestObject o, ForestSpecies? species) => species?.Kind switch
    {
        ForestKind.Decoration => "Decoration · " + Flavour.GetValueOrDefault(o.Species, "A piece of your forest"),
        ForestKind.Bush => "Bush · " + (o.Pinned ? "pinned in place" : "placed automatically"),
        _ => "Tree · " + (o.Pinned ? "pinned in place" : "placed automatically"),
    };

    private static UiNode Fact(string label, string value) => Ui.Column(Ui.Text(label, UiTextStyle.Caption), Ui.Text(value, UiTextStyle.Subtitle));

    // ---- first run -------------------------------------------------------------------------------------------------

    private static UiNode FirstRun(ForestView v)
    {
        UiScene preview = ForestSceneBuilder.Build(ForestSampleData.Preview(v), ForestIds.Preview, PreviewHeight, allowPick: false, selectedObjectId: null);
        return new UiCard(
            [
                Ui.Text("Your forest is empty. Every focus session you finish plants a tree here."),
                Ui.Text("Stay for the whole session and the tree grows: 25 minutes makes a young tree, 50 a mature one and 90 a majestic one. End it early and it withers.", UiTextStyle.Caption),
                Ui.Text("You earn one coin per focused minute. Spend coins below on new trees, bushes and decorations, then click anything in the forest to move or pin it.", UiTextStyle.Caption),
                preview with { AllowRotate = false },
                Ui.Text("Preview: this is what a few sessions grow.", UiTextStyle.Caption),
            ],
            "Grow your forest",
            "Start a focus session to plant your first tree",
            UiIcon.Leaf);
    }

    // ---- planting next ---------------------------------------------------------------------------------------------

    private static UiNode Planting(ForestView v, bool interactive)
    {
        ForestSpecies chosen = ForestCatalog.Find(v.Chosen) ?? ForestCatalog.All[0];
        UiNode[] chips =
        [
            .. ForestCatalog.All
                .Where(s => s.Kind != ForestKind.Decoration && v.Unlocked.Contains(s.Id))
                .Select(s => (UiNode)new UiButton(
                    ForestIds.ChipPrefix + s.Id,
                    s.Name,
                    s.Id == chosen.Id ? UiButtonVariant.Secondary : UiButtonVariant.Subtle,
                    s.Id == chosen.Id ? UiIcon.Checkmark : null,
                    interactive)),
        ];
        UiNode headline = Ui.Row(
            Thumbnail(chosen, "forest-planting-thumb", PlantingThumbnailHeight),
            Ui.Column(
                Ui.Text(Flavour.GetValueOrDefault(chosen.Id, string.Empty)),
                Ui.Text("The next finished session grows this " + (chosen.Kind == ForestKind.Bush ? "bush." : "tree."), UiTextStyle.Caption)));
        return new UiCard([headline, Ui.Grid(5, chips)], $"Planting next: {chosen.Name}", "Pick one of your unlocked species", UiIcon.Leaf);
    }

    private static UiScene Thumbnail(ForestSpecies s, string id, double height)
    {
        UiSceneItem item = new(id + "-item", ForestCatalog.ModelKey(s, ForestStage.Mature), 0, 0);
        return new UiScene(id, 1, 1, [ForestModels.For(s, ForestStage.Mature)], [item], height, ShowGround: false, AllowRotate: false, Label: s.Name);
    }

    // ---- inventory and shop ----------------------------------------------------------------------------------------

    private static UiNode Inventory(ForestView v, bool interactive)
    {
        UiNode[] buttons =
        [
            .. ForestCatalog.All
                .Where(s => v.Inventory.GetValueOrDefault(s.Id) > 0)
                .Select(s => (UiNode)new UiButton(
                    ForestIds.PlacePrefix + s.Id,
                    string.Create(CultureInfo.InvariantCulture, $"Place {s.Name} ×{v.Inventory[s.Id]}"),
                    UiButtonVariant.Secondary,
                    v.PlacingDecoration == s.Id ? UiIcon.Checkmark : null,
                    interactive)),
        ];
        return new UiCard([Ui.Grid(4, buttons)], "Your decorations", "Choose a piece, then click an empty tile in your forest", UiIcon.Heart);
    }

    private static UiNode Shop(ForestView v, bool interactive, ShopCategory category)
    {
        UiNode[] tabs =
        [
            .. Enum.GetValues<ShopCategory>().Select(c => (UiNode)Ui.Button(
                ForestIds.ShopTab(c),
                c.ToString(),
                c == category ? UiButtonVariant.Secondary : UiButtonVariant.Subtle)),
        ];
        UiNode[] entries = [.. ForestCatalog.All.Where(s => CategoryOf(s) == category).Select(s => ShopEntry(s, v, interactive))];
        return new UiCard([new UiStack(tabs, UiOrientation.Horizontal), Ui.Grid(4, entries)], "Shop", "Unlock trees and bushes to plant, or buy decorations for your forest", UiIcon.Star);
    }

    private static ShopCategory CategoryOf(ForestSpecies s) => s.Kind switch
    {
        ForestKind.Tree => ShopCategory.Trees,
        ForestKind.Bush => ShopCategory.Bushes,
        _ => ShopCategory.Decorations,
    };

    private static UiNode ShopEntry(ForestSpecies s, ForestView v, bool interactive)
    {
        UiButton button;
        string state;
        UiTone stateTone = UiTone.Neutral;
        if (s.Kind == ForestKind.Decoration)
        {
            int owned = v.Inventory.GetValueOrDefault(s.Id);
            bool full = owned >= ForestLimits.MaxInventoryPerItem;
            bool affordable = v.Coins >= s.Price;
            state = owned > 0 ? string.Create(CultureInfo.InvariantCulture, $"Owned ×{owned}") : "Not owned";
            if (full)
            {
                state = "Inventory full";
            }
            else if (!affordable)
            {
                state = Need(s.Price - v.Coins);
            }

            button = new UiButton(ForestIds.BuyPrefix + s.Id, "Buy", UiButtonVariant.Secondary, UiIcon.Star, interactive && affordable && !full);
        }
        else if (!v.Unlocked.Contains(s.Id))
        {
            bool affordable = v.Coins >= s.Price;
            state = affordable ? "Ready to unlock" : Need(s.Price - v.Coins);
            button = new UiButton(ForestIds.UnlockPrefix + s.Id, "Unlock", UiButtonVariant.Secondary, UiIcon.Unlock, interactive && affordable);
        }
        else
        {
            bool isChosen = v.Chosen == s.Id;
            state = isChosen ? "Planting next" : "Unlocked";
            stateTone = isChosen ? UiTone.Success : UiTone.Neutral;
            button = new UiButton(
                ForestIds.SelectPrefix + s.Id,
                isChosen ? "Planting" : "Plant next",
                UiButtonVariant.Secondary,
                isChosen ? UiIcon.Checkmark : null,
                interactive && !isChosen);
        }

        UiText price = s.Price == 0
            ? Ui.Text("Free", UiTextStyle.Mono, UiTone.Success)
            : Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{s.Price} coins"), UiTextStyle.Mono, UiTone.Accent);
        return Ui.Column(
            Thumbnail(s, ForestIds.Thumbnail(s.Id), ThumbnailHeight),
            Ui.Row(Ui.Text(s.Name, UiTextStyle.Subtitle), price),
            Ui.Text(Flavour.GetValueOrDefault(s.Id, string.Empty), UiTextStyle.Caption),
            Ui.Text(state, UiTextStyle.Caption, stateTone),
            button);
    }

    private static string Need(int missing) => string.Create(CultureInfo.InvariantCulture, $"Need {missing} more coin{(missing == 1 ? string.Empty : "s")}");
}

/// <summary>A deterministic example forest for the "Show example" preview; nothing of it is stored.</summary>
public static class ForestSampleData
{
    private static readonly string[] Plants = ["oak", "pine", "birch", "maple", "bush", "cherry", "spruce", "rose", "palm", "willow", "berry", "apple", "fern", "cypress"];
    private static readonly string[] Decorations = ["pond", "bench", "lantern", "flowers", "rock", "mushrooms", "well", "cabin"];

    /// <summary>A tiny, tidy forest for the first-run preview: <paramref name="template"/> supplies the coins, unlocks and everything else the view carries.</summary>
    public static ForestView Preview(ForestView template)
    {
        (string Species, ForestStage Stage, int Column, int Row, int Minutes)[] layout =
        [
            ("oak", ForestStage.Mature, 1, 1, 60),
            ("pine", ForestStage.Majestic, 2, 1, 95),
            ("cherry", ForestStage.Young, 1, 2, 30),
            ("bush", ForestStage.Mature, 2, 2, 55),
            ("birch", ForestStage.Sapling, 0, 1, 15),
            ("pond", ForestStage.Mature, 3, 3, 0),
            ("bench", ForestStage.Mature, 0, 3, 0),
        ];
        List<ForestObject> objects =
        [
            .. layout.Select((p, i) => new ForestObject { Id = i + 1, Species = p.Species, Stage = (int)p.Stage, Column = p.Column, Row = p.Row, Planted = 0, Minutes = p.Minutes }),
        ];
        return template with { Size = 4, Objects = objects, Paths = [], SelectedObject = null, PlacingDecoration = null };
    }

    /// <summary>Builds a forest with every plant species at several stages, a few withered plants and a few decorations, laid out by <see cref="ForestWorld"/>.</summary>
    public static ForestView Create(int today, TimeZoneInfo zone)
    {
        const int size = 8;
        long baseTime = new DateTimeOffset(DateOnly.FromDayNumber(today).ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(DateOnly.FromDayNumber(today).ToDateTime(TimeOnly.MinValue))).ToUnixTimeSeconds();
        int[] minutes = [25, 45, 60, 90, 30, 120, 50, 25, 60, 45, 90, 30];
        List<ForestObject> objects = [];
        for (int i = 0; i < 42; i++)
        {
            bool withered = i is 17 or 26 or 38;
            string species = Plants[i % Plants.Length];
            int m = minutes[(i * 5) % minutes.Length];
            objects.Add(new ForestObject
            {
                Id = i + 1,
                Species = species,
                Stage = (int)(withered ? ForestStage.Withered : ForestCatalog.StageFor(m)),
                Planted = baseTime - ((41 - i) * 86_400L / 2),
                Minutes = withered ? m / 2 : m,
            });
        }

        for (int i = 0; i < Decorations.Length; i++)
        {
            objects.Add(new ForestObject { Id = 100 + i, Species = Decorations[i], Stage = (int)ForestStage.Mature, Planted = baseTime });
        }

        WorldLayout world = ForestWorld.Arrange(
            size,
            [.. objects.Select(o => new WorldObject(o.Id, o.Species, ForestCatalog.Find(o.Species)!.Kind, (ForestStage)o.Stage, null, false))]);
        objects = [.. objects.Where(o => world.Positions.ContainsKey(o.Id))];
        foreach (ForestObject o in objects)
        {
            (o.Column, o.Row) = (world.Positions[o.Id].Column, world.Positions[o.Id].Row);
        }

        HashSet<string> unlocked = [.. ForestCatalog.All.Where(s => s.Kind != ForestKind.Decoration && (s.Price <= 400 || Plants.Contains(s.Id))).Select(s => s.Id)];
        return new ForestView(1260, "cherry", unlocked, new Dictionary<string, int> { ["bench"] = 1, ["flowers"] = 2 }, world.Size, objects, null, null) { Paths = world.Paths };
    }
}
