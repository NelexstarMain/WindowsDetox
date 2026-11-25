using FocusForest;
using Blocker.UI.Sdk;

namespace Blocker.UI.Render;

/// <summary>Review sheet of every Focus Forest model: one scene per tree or bush across its five stages, plus one for the decorations.</summary>
internal static class ForestGallery
{
    private const string Prefix = "gallery:forest";

    /// <summary>
    /// Parses <c>gallery:forest[:&lt;quarter turns&gt;[:&lt;scene height&gt;[:&lt;grid columns&gt;[:&lt;comma separated ids&gt;]]]]</c>;
    /// the optional ids limit the sheet to some species, for close-ups; the single id <c>example</c> draws the sample forest instead <c>empty</c> the first-run page, <c>paths</c> the path pieces with a street demo and <c>world</c> a generated village.
    /// </summary>
    public static bool TryParse(string target, out int turns, out double height, out int columns, out string[] ids)
    {
        turns = 0;
        height = 165;
        columns = 2;
        ids = [];
        if (!target.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = target.Split(':');
        System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
        turns = parts.Length > 2 ? int.Parse(parts[2], c) & 3 : 0;
        height = parts.Length > 3 ? double.Parse(parts[3], c) : height;
        columns = parts.Length > 4 ? int.Parse(parts[4], c) : columns;
        ids = parts.Length > 5 ? parts[5].Split(',') : [];
        return true;
    }

    public static UiNode Build(int turns, double height, int columns, string[] ids)
    {
        if (ids is ["example"])
        {
            return Ui.Column([.. ForestLayout.Body(ForestSampleData.Create(DateOnly.FromDateTime(DateTime.Today).DayNumber, TimeZoneInfo.Utc), TimeZoneInfo.Utc, false)]);
        }

        if (ids is ["empty"])
        {
            ForestView fresh = ForestSampleData.Create(DateOnly.FromDateTime(DateTime.Today).DayNumber, TimeZoneInfo.Utc) with { Coins = 0, Objects = [], Inventory = new Dictionary<string, int>() };
            return Ui.Column([.. ForestLayout.Body(fresh, TimeZoneInfo.Utc, true)]);
        }

        if (ids is ["paths"])
        {
            return Ui.Column(Ui.Text("Path pieces"), PathPieces(turns, height), Ui.Text("Streets"), Streets(height * 2.6));
        }

        if (ids is ["world"])
        {
            return Ui.Column(Village(700));
        }

        ForestStage[] stages = [ForestStage.Sapling, ForestStage.Young, ForestStage.Mature, ForestStage.Majestic, ForestStage.Withered];
        List<UiNode> cells = [];
        foreach (ForestSpecies species in ForestCatalog.All.Where(s => s.Kind != ForestKind.Decoration && (ids.Length == 0 || ids.Contains(s.Id))))
        {
            cells.Add(Ui.Column(Ui.Text(species.Name), Ui.Row([.. stages.Select(stage => Thumbnail(species, stage, turns, height, ids.Length == 0))])));
        }

        UiNode[] decorations = [.. ForestCatalog.All.Where(s => s.Kind == ForestKind.Decoration && (ids.Length == 0 || ids.Contains(s.Id))).Select(d => Thumbnail(d, ForestStage.Mature, turns, height, ids.Length == 0))];
        return decorations.Length == 0 ? Ui.Column(Ui.Grid(columns, [.. cells])) : Ui.Column(Ui.Grid(columns, [.. cells]), Ui.Text("Decorations"), Ui.Row(decorations), Ui.Text("Paths"), PathPieces(turns, height));
    }

    // Every path shape alone on one tile, at the scale the village draws them.
    private static UiNode PathPieces(int turns, double height) => Ui.Row([.. Enum.GetValues<ForestPathShape>().Select(shape =>
    {
        UiSceneModel model = ForestPaths.Model(shape);
        return new UiScene($"scene-{model.Key}", 1, 1, [model], [new UiSceneItem($"item-{model.Key}", model.Key, 0, 0, turns, ForestPaths.ItemScale)], height, "#9CCC65", true, false);
    })]);

    // A hand-drawn street network, to see that neighbouring pieces join.
    private static UiNode Streets(double height)
    {
        string[] rows = ["......", ".####.", ".#..#.", ".#.###", ".#..#.", "......"];
        IEnumerable<GridTile> tiles = rows.SelectMany((line, row) => line.Select((ch, column) => (ch, column)).Where(c => c.ch == '#').Select(c => new GridTile(c.column, row)));
        return ForestSceneBuilder.Build(EmptyView(rows.Length) with { Paths = ForestWorld.Autotile(tiles) }, "streets", height, false, null);
    }

    // A village laid out by the world algorithm: 40 plants and the decorations of the shop.
    private static UiNode Village(double height)
    {
        string[] plants = ["oak", "pine", "birch", "maple", "bush", "cherry", "rose", "spruce"];
        List<WorldObject> world = [];
        foreach (ForestSpecies species in ForestCatalog.All.Where(s => s.Kind == ForestKind.Decoration))
        {
            world.Add(new WorldObject(world.Count + 1, species.Id, species.Kind, ForestStage.Mature, null, false));
        }

        for (int i = 0; i < 40; i++)
        {
            ForestSpecies species = ForestCatalog.Find(plants[i % plants.Length])!;
            world.Add(new WorldObject(world.Count + 1, species.Id, species.Kind, (ForestStage)((i / plants.Length) % 4), null, false));
        }

        WorldLayout layout = ForestWorld.Arrange(ForestLimits.StartSize, world);
        ForestObject[] objects = [.. world.Select(o => new ForestObject { Id = o.Id, Species = o.Species, Stage = (int)o.Stage, Column = layout.Positions[o.Id].Column, Row = layout.Positions[o.Id].Row })];
        return ForestSceneBuilder.Build(EmptyView(layout.Size) with { Objects = objects, Paths = layout.Paths }, "village", height, false, null);
    }

    private static ForestView EmptyView(int size) => new(0, "oak", new HashSet<string>(), new Dictionary<string, int>(), size, [], null, null);

    // One model alone on one tile, like a shop thumbnail.
    private static UiScene Thumbnail(ForestSpecies species, ForestStage stage, int turns, double height, bool ruled)
    {
        // A speck of white far above the tile gives every thumbnail the same scale, so heights can be compared across stages;
        // close-ups of chosen species leave it out so that each model fills its cell.
        UiSceneModel model = ForestModels.For(species, stage);
        if (ruled)
        {
            UiSceneFace ruler = new([0, 2.3, 0, 0, 2.3, 0.004, 0.004, 2.3, 0.004], "#FFFFFF");
            model = model with { Faces = [.. model.Faces, ruler] };
        }

        return new($"scene-{model.Key}", 1, 1, [model], [new UiSceneItem($"item-{model.Key}", model.Key, 0, 0, turns)], height, "#9CCC65", true, false);
    }
}
