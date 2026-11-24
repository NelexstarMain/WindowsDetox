// Builds the isometric scene of the forest: path tiles, plants and decorations, and the models they use. Pure: no state, no I/O.

using System.Globalization;
using Blocker.UI.Sdk;

namespace FocusForest;

/// <summary>Turns a <see cref="ForestView"/> into a <see cref="UiScene"/>.</summary>
public static class ForestSceneBuilder
{
    // The host refuses documents over 512 KiB (UiJson.MaxDocumentBytes) and one model is 2-20 KiB of JSON, so the scene gets a fixed share:
    // the shop thumbnails of one category need up to about 100 KiB and the rest of the page about 50 KiB. Models beyond the budget are
    // drawn with the closest model that did fit. The path models (about 3 KiB each) are sent in any case and are not part of the budget.
    private const int SceneModelBudgetChars = 300_000;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ModelSizes = new(StringComparer.Ordinal);

    /// <summary>
    /// The scene of the view. Path tiles come first (<c>path-column-row</c>, unlabelled, drawn at <see cref="ForestPaths.ItemScale"/>);
    /// <paramref name="selectedObjectId"/> is the scene item id of the selected object (<see cref="ForestIds.ObjectPrefix"/> and its
    /// number). Labels are the tooltips of <see cref="ForestLayout.LabelOf"/> when a time zone is given, else just the name.
    /// </summary>
    public static UiScene Build(ForestView view, string sceneId, double height, bool allowPick, string? selectedObjectId, TimeZoneInfo? zone = null)
    {
        List<UiSceneModel> models = [.. view.Paths.Select(p => p.Shape).Distinct().Order().Select(ForestPaths.Model), .. ModelsFor(view.Objects)];
        List<UiSceneItem> items =
        [
            .. view.Paths.Select(p => new UiSceneItem(
                string.Create(CultureInfo.InvariantCulture, $"{ForestIds.PathPrefix}{p.Column}-{p.Row}"),
                ForestPaths.Key(p.Shape),
                p.Column,
                p.Row,
                p.Rotation,
                ForestPaths.ItemScale)),
            .. view.Objects.Select(o =>
            {
                string id = ForestIds.ObjectPrefix + o.Id.ToString(CultureInfo.InvariantCulture);
                return new UiSceneItem(
                    id,
                    ResolveModel(o, models),
                    o.Column,
                    o.Row,
                    ForestStore.RotationOf(o.Id),
                    IsSelected: selectedObjectId == id,
                    Label: zone is null ? ForestCatalog.Find(o.Species)?.Name ?? o.Species : ForestLayout.LabelOf(o, zone));
            }),
        ];
        return new UiScene(sceneId, view.Size, view.Size, models, items, height, AllowPick: allowPick, Label: "Your focus forest");
    }

    private static string ModelKeyOf(ForestObject o) =>
        ForestCatalog.ModelKey(ForestCatalog.Find(o.Species)!, o.StageValue);

    // Only models that are used are sent. Every used species gets one model first (its most used stage), so that no species is missing from
    // the scene; the other stages follow, most used first, as many as fit the budget. The first model is always sent.
    private static List<UiSceneModel> ModelsFor(IReadOnlyList<ForestObject> objects)
    {
        List<IGrouping<string, ForestObject>> byUse = [.. objects.GroupBy(ModelKeyOf).OrderByDescending(g => g.Count())];
        IEnumerable<IGrouping<string, ForestObject>> mostUsedPerSpecies = byUse.GroupBy(g => g.First().Species).Select(species => species.First());
        List<UiSceneModel> models = [];
        HashSet<string> seen = [];
        int total = 0;
        foreach (IGrouping<string, ForestObject> group in mostUsedPerSpecies.Concat(byUse))
        {
            if (!seen.Add(group.Key))
            {
                continue;
            }

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

    // The model of the object itself, or the closest one that fit the budget: the same species at the nearest stage, else the same kind
    // (nearest stage first), else any plant or decoration model.
    private static string ResolveModel(ForestObject o, List<UiSceneModel> models)
    {
        string key = ModelKeyOf(o);
        if (models.Any(m => m.Key == key))
        {
            return key;
        }

        ForestSpecies species = ForestCatalog.Find(o.Species)!;
        (string Key, ForestSpecies Species, int Distance)[] ranked =
        [
            .. models.Where(m => !m.Key.StartsWith(ForestIds.PathPrefix, StringComparison.Ordinal)).Select(m =>
            {
                ForestSpecies owner = ForestCatalog.All.First(s => m.Key == s.Id || m.Key.StartsWith(s.Id + "-", StringComparison.Ordinal));
                ForestStage stage = m.Key == owner.Id ? ForestStage.Mature : Enum.Parse<ForestStage>(m.Key[(owner.Id.Length + 1)..], ignoreCase: true);
                return (m.Key, owner, (stage == ForestStage.Withered) != (o.StageValue == ForestStage.Withered) ? 10 : Math.Abs(stage - o.StageValue));
            }),
        ];
        return ranked.Where(r => r.Species == species).OrderBy(r => r.Distance).Select(r => r.Key).FirstOrDefault()
            ?? ranked.Where(r => r.Species.Kind == species.Kind).OrderBy(r => r.Distance).Select(r => r.Key).FirstOrDefault()
            ?? ranked[0].Key;
    }
}
