using FocusInsights;
using Blocker.UI.Sdk;

namespace Blocker.UI.Render;

/// <summary>Review sheet of every Focus Forest model: one scene per tree or bush across its five stages, plus one for the decorations.</summary>
internal static class ForestGallery
{
    private const string Prefix = "gallery:forest";

    /// <summary>
    /// Parses <c>gallery:forest[:&lt;quarter turns&gt;[:&lt;scene height&gt;[:&lt;grid columns&gt;[:&lt;comma separated ids&gt;]]]]</c>;
    /// the optional ids limit the sheet to some species, for close-ups.
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
        ForestStage[] stages = [ForestStage.Sapling, ForestStage.Young, ForestStage.Mature, ForestStage.Majestic, ForestStage.Withered];
        List<UiNode> cells = [];
        foreach (ForestSpecies species in ForestCatalog.All.Where(s => s.Kind != ForestKind.Decoration && (ids.Length == 0 || ids.Contains(s.Id))))
        {
            cells.Add(Ui.Column(Ui.Text(species.Name, UiTextStyle.Subtitle), Ui.Row([.. stages.Select(stage => Thumbnail(species, stage, turns, height))])));
        }

        UiNode[] decorations = [.. ForestCatalog.All.Where(s => s.Kind == ForestKind.Decoration && (ids.Length == 0 || ids.Contains(s.Id))).Select(d => Thumbnail(d, ForestStage.Mature, turns, height))];
        return decorations.Length == 0 ? Ui.Column(Ui.Grid(columns, [.. cells])) : Ui.Column(Ui.Grid(columns, [.. cells]), Ui.Text("Decorations", UiTextStyle.Subtitle), Ui.Row(decorations));
    }

    // One model alone on one tile, like a shop thumbnail.
    private static UiScene Thumbnail(ForestSpecies species, ForestStage stage, int turns, double height)
    {
        // A speck of white far above the tile gives every thumbnail the same scale, so heights can be compared across stages.
        UiSceneModel model = ForestModels.For(species, stage);
        UiSceneFace ruler = new([0, 2.3, 0, 0, 2.3, 0.004, 0.004, 2.3, 0.004], "#FFFFFF");
        model = model with { Faces = [.. model.Faces, ruler] };
        return new($"scene-{model.Key}", 1, 1, [model], [new UiSceneItem($"item-{model.Key}", model.Key, 0, 0, turns)], height, "#9CCC65", true, false);
    }
}
