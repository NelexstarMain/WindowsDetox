// Tests of the Focus Forest model library: every model builds, validates inside a scene, stays on its tile, and every helper winds outward.

using Blocker.UI.Sdk;
using Xunit;

namespace FocusInsights.Tests;

/// <summary>Tests of <see cref="ForestModels"/> and <see cref="ForestMeshes"/>.</summary>
public sealed class ForestModelsTests
{
    private static readonly ForestStage[] AllStages = [ForestStage.Sapling, ForestStage.Young, ForestStage.Mature, ForestStage.Majestic, ForestStage.Withered];

    private static IEnumerable<(ForestSpecies Species, ForestStage Stage, UiSceneModel Model)> AllModels()
    {
        foreach (ForestSpecies species in ForestCatalog.All)
        {
            foreach (ForestStage stage in species.Kind == ForestKind.Decoration ? [ForestStage.Mature] : AllStages)
            {
                yield return (species, stage, ForestModels.For(species, stage));
            }
        }
    }

    private static Vec[] Points(UiSceneFace face) =>
        [.. Enumerable.Range(0, face.Points.Count / 3).Select(i => new Vec(face.Points[3 * i], face.Points[(3 * i) + 1], face.Points[(3 * i) + 2]))];

    private static double MaxY(UiSceneModel model) => model.Faces.Max(f => Points(f).Max(p => p.Y));

    private static void AssertOutward(Mesh mesh, Vec centre)
    {
        Assert.NotEmpty(mesh.Faces);
        foreach (Mesh.Face face in mesh.Faces)
        {
            Vec middle = new Vec(face.Points.Average(p => p.X), face.Points.Average(p => p.Y), face.Points.Average(p => p.Z)) - centre;
            Assert.True(Mesh.Normal(face.Points).Dot(middle) > 0, "A face points into the solid.");
        }
    }

    [Fact]
    public void Every_catalogue_entry_and_stage_builds_with_its_catalogue_key_and_a_sane_face_count()
    {
        int models = 0;
        foreach ((ForestSpecies species, ForestStage stage, UiSceneModel model) in AllModels())
        {
            models++;
            Assert.Equal(ForestCatalog.ModelKey(species, stage), model.Key);
            Assert.InRange(model.Faces.Count, 20, 200);
            Assert.Same(model, ForestModels.For(species, stage));
        }

        Assert.Equal((14 * 5) + 8, models);
    }

    [Fact]
    public void Models_pass_the_scene_validator_in_scenes_within_the_limits()
    {
        List<UiNode> scenes = [];
        foreach (IGrouping<string, (ForestSpecies Species, ForestStage Stage, UiSceneModel Model)> group in AllModels().GroupBy(m => m.Species.Id))
        {
            UiSceneModel[] models = [.. group.Select(g => g.Model)];
            UiSceneItem[] items = [.. models.Select((m, i) => new UiSceneItem("item-" + m.Key, m.Key, i, 0))];
            scenes.Add(new UiScene("scene-" + group.Key, models.Length, 1, models, items));
        }

        UiValidationResult result = UiTreeValidator.Validate(Ui.Page("Forest", [.. scenes]));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Models_stay_on_their_tile_above_the_ground_and_have_no_degenerate_faces()
    {
        foreach ((ForestSpecies species, ForestStage stage, UiSceneModel model) in AllModels())
        {
            foreach (UiSceneFace face in model.Faces)
            {
                Vec[] points = Points(face);
                Assert.InRange(points.Length, 3, 16);
                Assert.True(points.All(p => Math.Abs(p.X) <= 0.55 && Math.Abs(p.Z) <= 0.55 && p.Y >= -0.001 && p.Y <= 2.3), $"{model.Key} leaves its tile or the height budget.");
                Assert.True(Mesh.Normal(points).Length > 1e-7, $"{model.Key} has a degenerate face.");
            }
        }
    }

    [Fact]
    public void Trees_and_bushes_grow_taller_with_every_stage_and_majestic_is_the_most_detailed()
    {
        foreach (ForestSpecies species in ForestCatalog.All.Where(s => s.Kind != ForestKind.Decoration))
        {
            UiSceneModel sapling = ForestModels.For(species, ForestStage.Sapling), young = ForestModels.For(species, ForestStage.Young);
            UiSceneModel mature = ForestModels.For(species, ForestStage.Mature), majestic = ForestModels.For(species, ForestStage.Majestic);

            Assert.True(MaxY(sapling) < MaxY(young) && MaxY(young) < MaxY(mature) && MaxY(mature) < MaxY(majestic), $"{species.Id} does not grow monotonically.");
            Assert.True(majestic.Faces.Count > mature.Faces.Count && mature.Faces.Count > sapling.Faces.Count, $"{species.Id} is not more detailed when grown.");
            Assert.True(MaxY(sapling) < 0.45, $"{species.Id} sapling is not tiny.");
        }
    }

    [Fact]
    public void Serialised_models_are_small_enough_for_a_document_of_many_models()
    {
        List<int> sizes = [];
        foreach ((ForestSpecies species, ForestStage stage, UiSceneModel model) in AllModels())
        {
            UiScene scene = new("one", 1, 1, [model], [new UiSceneItem("item", model.Key, 0, 0)], ShowGround: false, AllowRotate: false);
            sizes.Add(UiJson.Serialize(Ui.Page("P", scene)).Length);
        }

        Assert.True(sizes.Average() <= 8 * 1024, $"Average serialised model is {sizes.Average():0} bytes.");
        Assert.True(sizes.Max() <= 14 * 1024, $"Largest serialised model is {sizes.Max()} bytes.");
    }

    [Fact]
    public void Withered_models_differ_from_the_living_ones_in_colour_and_species_are_distinct()
    {
        foreach (ForestSpecies species in ForestCatalog.All.Where(s => s.Kind != ForestKind.Decoration))
        {
            string[] living = [.. ForestModels.For(species, ForestStage.Mature).Faces.Select(f => f.Color).Distinct()];
            string[] dead = [.. ForestModels.For(species, ForestStage.Withered).Faces.Select(f => f.Color).Distinct()];
            Assert.NotEqual(living.Order(), dead.Order());
        }

        string[] signatures = [.. ForestCatalog.All.Select(s => string.Join(',', ForestModels.For(s, ForestStage.Mature).Faces.Select(f => f.Color).Distinct().Order().Take(6)))];
        Assert.Equal(signatures.Length, signatures.Distinct().Count());
    }

    [Fact]
    public void Box_winds_outward_on_all_five_visible_faces_and_its_top_points_up()
    {
        Mesh box = ForestMeshes.Box(0.1, 0.2, -0.1, 0.4, 0.3, 0.2, "#808080");

        Assert.Equal(5, box.Count);
        AssertOutward(box, new Vec(0.1, 0.35, -0.1));
        Mesh.Face top = box.Faces.Single(f => f.Points.All(p => Math.Abs(p.Y - 0.5) < 1e-9));
        Assert.True(Mesh.Normal(top.Points).Y > 0);
    }

    [Fact]
    public void Turned_box_keeps_its_faces_outward()
    {
        AssertOutward(ForestMeshes.Box(0.2, 0, 0.1, 0.3, 0.2, 0.1, "#808080", null, 37), new Vec(0.2, 0.1, 0.1));
    }

    [Theory]
    [InlineData(3, 0.2, 0.2)]
    [InlineData(6, 0.2, 0.1)]
    [InlineData(8, 0.2, 0)]
    [InlineData(5, 0.05, 0.2)]
    public void Prism_and_cone_side_faces_point_away_from_the_axis_and_the_top_points_up(int sides, double rBottom, double rTop)
    {
        Mesh prism = ForestMeshes.Prism(sides, 0.1, 0, 0.2, rBottom, rTop, 0.5, "#808080");

        foreach (Mesh.Face face in prism.Faces)
        {
            Vec normal = Mesh.Normal(face.Points);
            Vec middle = new(face.Points.Average(p => p.X) - 0.1, 0, face.Points.Average(p => p.Z) - 0.2);
            if (face.Points.All(p => Math.Abs(p.Y - 0.5) < 1e-9))
            {
                Assert.True(normal.Y > 0);
            }
            else
            {
                Assert.True(normal.Dot(middle) > 0);
            }
        }
    }

    [Fact]
    public void Tilted_limb_faces_point_away_from_the_axis()
    {
        var a = new Vec(0, 0, 0);
        var b = new Vec(0.3, 0.4, -0.1);
        Mesh limb = ForestMeshes.Limb(a, b, 0.05, 0.02, 5, "#808080");
        Vec axis = (b - a).Normalized();

        foreach (Mesh.Face face in limb.Faces)
        {
            var middle = new Vec(face.Points.Average(p => p.X), face.Points.Average(p => p.Y), face.Points.Average(p => p.Z));
            Vec radial = middle - (axis * middle.Dot(axis));
            Vec normal = Mesh.Normal(face.Points);
            Assert.True(face.Points.Length == 5 ? normal.Dot(axis) > 0 : normal.Dot(radial) > 0);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Blob_is_closed_outward_and_deterministic(int detail)
    {
        Mesh blob = ForestMeshes.Blob(0.1, 0.5, 0, 0.3, 0.2, 0.25, "#40A040", detail, 0.1, 5, 0.07, keepUnderside: true);

        AssertOutward(blob, new Vec(0.1, 0.5, 0));
        Assert.Equal(new[] { 8, 20, 80 }[detail + 1], blob.Count);
        Assert.True(ForestMeshes.Blob(0.1, 0.5, 0, 0.3, 0.2, 0.25, "#40A040", 0, 0.1, 5).Count < 20, "The never-visible underside is not dropped.");
        Mesh again = ForestMeshes.Blob(0.1, 0.5, 0, 0.3, 0.2, 0.25, "#40A040", detail, 0.1, 5, 0.07, keepUnderside: true);
        Assert.Equal(blob.ToModel("a").Faces.Select(f => (f.Points, f.Color)), again.ToModel("a").Faces.Select(f => (f.Points, f.Color)));
    }

    [Fact]
    public void Lathe_and_gable_wind_outward()
    {
        Mesh lathe = ForestMeshes.Lathe(7, [(0.2, 0), (0.3, 0.2), (0.1, 0.4), (0, 0.5)], _ => "#808080", 0, 0, 0.1, 3);
        foreach (Mesh.Face face in lathe.Faces)
        {
            Vec middle = new(face.Points.Average(p => p.X), 0, face.Points.Average(p => p.Z));
            Assert.True(Mesh.Normal(face.Points).Dot(middle) > 0);
        }

        Mesh gable = ForestMeshes.Gable(0, 0, 0, 0.6, 0.4, 0.2, "#B5543C", "#A9714B");
        Assert.Equal(4, gable.Count);
        AssertOutward(gable, new Vec(0, 0.05, 0));
    }

    [Fact]
    public void Transforms_keep_winding_and_colours_are_valid_hex()
    {
        Mesh moved = ForestMeshes.Box(0, -0.1, 0, 0.2, 0.2, 0.2, "#336699").Turned(33).RotatedX(10).RotatedZ(-20).Scaled(1.5, 0.8, 1.2).Moved(0.1, 0.2, 0.3);

        AssertOutward(moved, new Vec(0.1, 0.2, 0.3));
        Assert.Equal("#336699", ((Rgb)"#336699").Hex);
        Assert.Equal("#FFFFFF", ((Rgb)"#808080").Mix("#FFFFFF", 1).Hex);
        Assert.Equal(((Rgb)"#808080").Vary(4, 0.1), ((Rgb)"#808080").Vary(4, 0.1));
    }
}
