// Focus Forest visualization: stat tiles, the isometric scene, the inventory and the shop. Pure: no state, no I/O.

using System.Globalization;
using Blocker.UI.Sdk;

namespace FocusInsights;

/// <summary>Ids of the forest controls; dynamic ones are a prefix followed by an object id or a catalogue id.</summary>
public static class ForestIds
{
    /// <summary>The big scene (empty-tile clicks arrive as Pick with this id).</summary>
    public const string Scene = "forest-scene";

    /// <summary>Prefix of a placed object, followed by its numeric id.</summary>
    public const string ObjectPrefix = "forest-obj-";

    /// <summary>Prefix of "Unlock", followed by a catalogue id.</summary>
    public const string UnlockPrefix = "forest-unlock-";

    /// <summary>Prefix of "Select", followed by a catalogue id.</summary>
    public const string SelectPrefix = "forest-select-";

    /// <summary>Prefix of "Buy", followed by a catalogue id.</summary>
    public const string BuyPrefix = "forest-buy-";

    /// <summary>Prefix of an inventory entry, followed by a catalogue id.</summary>
    public const string PlacePrefix = "forest-place-";

    /// <summary>Drops the pending move or placement.</summary>
    public const string Cancel = "forest-cancel";

    /// <summary>Removes the selected withered plant for a fee.</summary>
    public const string Remove = "forest-remove";

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

/// <summary>Builds the forest tab of the Insights page.</summary>
public static class ForestLayout
{
    private const double ThumbnailHeight = 96;
    private const double SceneHeight = 420;

    // The host refuses documents over 512 KiB and one model is 6-30 KiB of JSON, so the scene gets a fixed share (the shop
    // thumbnails of one category need about 150 KiB). Models beyond it are drawn with a similar model that did fit.
    private const int SceneModelBudgetChars = 230_000;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ModelSizes = new(StringComparer.Ordinal);

    /// <summary>Tiles, forest card, inventory and shop. With <paramref name="interactive"/> off (the example) nothing can be clicked.</summary>
    public static List<UiNode> Body(ForestView view, TimeZoneInfo zone, bool interactive, ShopCategory shop = ShopCategory.Trees)
    {
        List<UiNode> body = [Tiles(view), Garden(view, zone, interactive)];
        if (view.Inventory.Count > 0)
        {
            body.Add(Inventory(view, interactive));
        }

        body.Add(Shop(view, interactive, shop));
        return body;
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

        int day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(o.Planted), zone).DateTime).DayNumber;
        string prefix = o.StageValue == ForestStage.Withered ? "Withered " : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{name} · {o.Minutes} min · {InsightsFormat.DayLabel(day)}");
    }

    private static UiNode Tiles(ForestView v) => Ui.Grid(
        3,
        Ui.Stat("Coins", Short(v.Coins), "1 per focused minute", UiTone.Accent, UiIcon.Star),
        Ui.Stat("Plants", Short(v.Objects.Count(o => ForestCatalog.Find(o.Species)?.Kind != ForestKind.Decoration)), "Trees and bushes", UiTone.Success, UiIcon.Leaf),
        Ui.Stat("Field", string.Create(CultureInfo.InvariantCulture, $"{v.Size}x{v.Size}"), "Grows as you plant", UiTone.Neutral, UiIcon.Target));

    private static string Short(int n) => n < 10_000
        ? n.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{n / 1000.0:0.#}k");

    private static UiNode Garden(ForestView v, TimeZoneInfo zone, bool interactive)
    {
        string chosen = ForestCatalog.Find(v.Chosen)?.Name ?? v.Chosen;
        List<UiSceneModel> models = ModelsFor(v.Objects);
        var items = v.Objects
            .Select(o => new UiSceneItem(
                ForestIds.ObjectPrefix + o.Id.ToString(CultureInfo.InvariantCulture),
                ResolveModel(o, models),
                o.Column,
                o.Row,
                ForestStore.RotationOf(o.Id),
                IsSelected: v.SelectedObject == o.Id,
                Label: LabelOf(o, zone)))
            .ToList();
        var scene = new UiScene(
            ForestIds.Scene,
            v.Size,
            v.Size,
            models,
            items,
            SceneHeight,
            AllowPick: interactive && v.IsPicking,
            Label: "Your focus forest");

        List<UiNode> children = [scene];
        if (interactive)
        {
            children.AddRange(Hint(v));
        }

        return new UiCard(children, "Your forest", $"Planting next: {chosen}");
    }

    private static IEnumerable<UiNode> Hint(ForestView v)
    {
        if (v.SelectedObject is { } id && v.Objects.FirstOrDefault(o => o.Id == id) is { } selected)
        {
            string name = ForestCatalog.Find(selected.Species)?.Name ?? selected.Species;
            List<UiNode> row =
            [
                Ui.Text($"Click an empty tile to move the {name.ToLowerInvariant()}, or click it again to deselect.", UiTextStyle.Caption),
                Ui.Button(ForestIds.Cancel, "Cancel", UiButtonVariant.Secondary, UiIcon.Dismiss),
            ];
            if (selected.StageValue == ForestStage.Withered)
            {
                row.Add(new UiButton(
                    ForestIds.Remove,
                    $"Remove ({ForestLimits.WitheredRemovalFee} coins)",
                    UiButtonVariant.Warning,
                    UiIcon.Dismiss,
                    v.Coins >= ForestLimits.WitheredRemovalFee));
            }

            yield return new UiStack(row, UiOrientation.Horizontal);
        }
        else if (v.PlacingDecoration is { } decoration)
        {
            yield return Ui.Row(
                Ui.Text($"Click an empty tile to place the {(ForestCatalog.Find(decoration)?.Name ?? decoration).ToLowerInvariant()}.", UiTextStyle.Caption),
                Ui.Button(ForestIds.Cancel, "Cancel", UiButtonVariant.Secondary, UiIcon.Dismiss));
        }
        else
        {
            yield return Ui.Text(
                v.Objects.Count == 0
                    ? "Finish a focus session to plant your first tree. Ending one early plants a withered tree."
                    : "Click a plant or decoration to move it. Withered trees can be removed for a small fee.",
                UiTextStyle.Caption);
        }
    }

    private static UiNode Inventory(ForestView v, bool interactive)
    {
        UiNode[] buttons =
        [
            .. ForestCatalog.All
                .Where(s => v.Inventory.GetValueOrDefault(s.Id) > 0)
                .Select(s => (UiNode)new UiButton(
                    ForestIds.PlacePrefix + s.Id,
                    string.Create(CultureInfo.InvariantCulture, $"{s.Name} ×{v.Inventory[s.Id]}"),
                    v.PlacingDecoration == s.Id ? UiButtonVariant.Primary : UiButtonVariant.Secondary,
                    IsEnabled: interactive)),
        ];
        return new UiCard([Ui.Grid(4, buttons)], "Inventory", "Choose a piece, then click an empty tile in your forest");
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
        return new UiCard([new UiStack(tabs, UiOrientation.Horizontal), Ui.Grid(4, entries)], "Shop", "Unlock trees and bushes to plant, or buy decorations for your forest");
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
        string caption;
        if (s.Kind == ForestKind.Decoration)
        {
            int owned = v.Inventory.GetValueOrDefault(s.Id);
            caption = string.Create(CultureInfo.InvariantCulture, $"{s.Price} coins{(owned > 0 ? $" · {owned} owned" : string.Empty)}");
            button = new UiButton(ForestIds.BuyPrefix + s.Id, "Buy", UiButtonVariant.Secondary, null, interactive && v.Coins >= s.Price && owned < ForestLimits.MaxInventoryPerItem);
        }
        else if (!v.Unlocked.Contains(s.Id))
        {
            caption = string.Create(CultureInfo.InvariantCulture, $"{s.Price} coins");
            button = new UiButton(ForestIds.UnlockPrefix + s.Id, "Unlock", UiButtonVariant.Secondary, UiIcon.Unlock, interactive && v.Coins >= s.Price);
        }
        else
        {
            bool isChosen = v.Chosen == s.Id;
            caption = s.Kind == ForestKind.Tree ? "Tree" : "Bush";
            button = new UiButton(
                ForestIds.SelectPrefix + s.Id,
                isChosen ? "Planting" : "Select",
                isChosen ? UiButtonVariant.Primary : UiButtonVariant.Secondary,
                isChosen ? UiIcon.Checkmark : null,
                interactive && !isChosen);
        }

        UiSceneItem item = new(ForestIds.Thumbnail(s.Id) + "-item", ForestCatalog.ModelKey(s, ForestStage.Mature), 0, 0);
        var thumbnail = new UiScene(
            ForestIds.Thumbnail(s.Id),
            1,
            1,
            [ForestModels.For(s, ForestStage.Mature)],
            [item],
            ThumbnailHeight,
            ShowGround: false,
            AllowRotate: false,
            Label: s.Name);
        return Ui.Column(thumbnail, Ui.Text(s.Name), Ui.Text(caption, UiTextStyle.Caption), button);
    }

    private static string ModelKeyOf(ForestObject o) =>
        ForestCatalog.ModelKey(ForestCatalog.Find(o.Species)!, o.StageValue);

    // Only models that are used are sent, most used first, as many as fit the budget (always at least one).
    private static List<UiSceneModel> ModelsFor(IReadOnlyList<ForestObject> objects)
    {
        List<UiSceneModel> models = [];
        int total = 0;
        foreach (IGrouping<string, ForestObject> group in objects.GroupBy(ModelKeyOf).OrderByDescending(g => g.Count()))
        {
            ForestObject sample = group.First();
            UiSceneModel model = ForestModels.For(ForestCatalog.Find(sample.Species)!, sample.StageValue);
            int size = ModelSizes.GetOrAdd(model.Key, _ => UiJson.Serialize(new UiScene("m", 1, 1, [model], [])).Length);
            if (models.Count > 0 && total + size > SceneModelBudgetChars)
            {
                continue;
            }

            total += size;
            models.Add(model);
        }

        return models;
    }

    // The model of the object itself, or the closest one that fit the budget: same species, else same kind, else any.
    private static string ResolveModel(ForestObject o, List<UiSceneModel> models)
    {
        string key = ModelKeyOf(o);
        if (models.Any(m => m.Key == key))
        {
            return key;
        }

        ForestSpecies species = ForestCatalog.Find(o.Species)!;
        string[] sameSpecies = [.. models.Select(m => m.Key).Where(k => k.StartsWith(species.Id + "-", StringComparison.Ordinal))];
        if (sameSpecies.Length > 0)
        {
            return sameSpecies[0];
        }

        string? sameKind = models.Select(m => m.Key).FirstOrDefault(k => ForestCatalog.All.Any(s => s.Kind == species.Kind && (k == s.Id || k.StartsWith(s.Id + "-", StringComparison.Ordinal))));
        return sameKind ?? models[0].Key;
    }
}

/// <summary>A deterministic example forest for the "Show example" preview; nothing of it is stored.</summary>
public static class ForestSampleData
{
    private static readonly string[] Plants = ["oak", "pine", "birch", "maple", "bush", "cherry", "spruce", "rose", "oak", "willow", "berry", "apple"];
    private static readonly string[] Decorations = ["pond", "bench", "lantern", "flowers", "rock", "mushrooms", "well", "cabin"];

    /// <summary>Builds a 10x10 forest with plants of every stage, one withered tree and a few decorations.</summary>
    public static ForestView Create(int today, TimeZoneInfo zone)
    {
        const int size = 10;
        long baseTime = new DateTimeOffset(DateOnly.FromDayNumber(today).ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(DateOnly.FromDayNumber(today).ToDateTime(TimeOnly.MinValue))).ToUnixTimeSeconds();
        int[] minutes = [25, 45, 60, 90, 30, 120, 50, 25, 60, 45, 90, 30];
        List<ForestObject> objects = [];
        var tiles = ForestStore.TilesFromCentre(size).ToList();
        for (int i = 0; i < 42; i++)
        {
            bool withered = i == 17;
            string species = Plants[i % Plants.Length];
            int m = minutes[(i * 5) % minutes.Length];
            (int column, int row) = tiles[i];
            objects.Add(new ForestObject
            {
                Id = i + 1,
                Species = species,
                Stage = (int)(withered ? ForestStage.Withered : ForestCatalog.StageFor(m)),
                Column = column,
                Row = row,
                Planted = baseTime - ((41 - i) * 86_400L / 2),
                Minutes = withered ? m / 2 : m,
            });
        }

        for (int i = 0; i < Decorations.Length; i++)
        {
            (int column, int row) = tiles[^(1 + (i * 3))];
            objects.Add(new ForestObject { Id = 100 + i, Species = Decorations[i], Stage = (int)ForestStage.Mature, Column = column, Row = row, Planted = baseTime });
        }

        HashSet<string> unlocked = [.. ForestCatalog.All.Where(s => s.Kind != ForestKind.Decoration && (s.Price <= 400 || Plants.Contains(s.Id))).Select(s => s.Id)];
        return new ForestView(1260, "cherry", unlocked, new Dictionary<string, int> { ["bench"] = 1, ["flowers"] = 2 }, size, objects, null, null);
    }
}
