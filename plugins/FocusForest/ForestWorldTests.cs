// Tests of the world layout (ForestWorld), the path models (ForestPaths) and the scene builder.

using Blocker.UI.Sdk;
using Xunit;

namespace FocusForest.Tests;

/// <summary>Tests of the village layout algorithm, the path pieces and the scene made of them.</summary>
public sealed class ForestWorldTests
{
    private static readonly string[] Trees = ["oak", "pine", "birch", "maple"];
    private static readonly string[] Decorations = ["cabin", "well", "pond", "bench", "lantern", "flowers", "bench", "rock", "mushrooms", "lantern"];

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static WorldObject Plant(int id, string species, ForestStage stage = ForestStage.Mature, GridTile? at = null, bool pinned = false) =>
        new(id, species, ForestCatalog.Find(species)!.Kind, stage, at, pinned);

    private static List<WorldObject> Mixed(int plants, int decorations)
    {
        List<WorldObject> objects = [];
        for (int i = 0; i < plants; i++)
        {
            objects.Add(Plant(objects.Count + 1, Trees[i % Trees.Length], (ForestStage)((i / Trees.Length) % 4)));
        }

        for (int i = 0; i < decorations; i++)
        {
            objects.Add(Plant(objects.Count + 1, Decorations[i % Decorations.Length]));
        }

        return objects;
    }

    // The same objects, each standing where the layout put it (what the store passes when something is added).
    private static List<WorldObject> Settled(IEnumerable<WorldObject> objects, WorldLayout layout) =>
        [.. objects.Select(o => layout.Positions.TryGetValue(o.Id, out GridTile at) ? o with { Position = at } : o)];

    private static HashSet<GridTile> PathTiles(WorldLayout layout) => [.. layout.Paths.Select(p => new GridTile(p.Column, p.Row))];

    private static IEnumerable<GridTile> Around(GridTile t) =>
        [new(t.Column + 1, t.Row), new(t.Column, t.Row + 1), new(t.Column - 1, t.Row), new(t.Column, t.Row - 1)];

    private static bool IsDecoration(WorldObject o) => o.Kind == ForestKind.Decoration;

    private static double Distance(GridTile a, GridTile b) => Math.Sqrt(Math.Pow(a.Column - b.Column, 2) + Math.Pow(a.Row - b.Row, 2));

    // ---- determinism and stability -------------------------------------------------------------------------------

    [Fact]
    public void The_same_objects_always_make_the_same_village()
    {
        List<WorldObject> objects = Mixed(30, 8);

        WorldLayout a = ForestWorld.Arrange(6, objects);
        WorldLayout b = ForestWorld.Arrange(6, [.. objects.AsEnumerable().Reverse()]);

        Assert.Equal(a.Size, b.Size);
        Assert.Equal(a.Positions.OrderBy(p => p.Key), b.Positions.OrderBy(p => p.Key));
        Assert.Equal(a.Paths, b.Paths);
    }

    [Fact]
    public void A_different_seed_makes_a_different_village()
    {
        List<WorldObject> objects = Mixed(20, 6);

        WorldLayout a = ForestWorld.Arrange(8, objects, seed: 1);
        WorldLayout b = ForestWorld.Arrange(8, objects, seed: 2);

        Assert.NotEqual(a.Positions.OrderBy(p => p.Key), b.Positions.OrderBy(p => p.Key));
    }

    [Fact]
    public void Adding_a_plant_moves_nothing_else()
    {
        List<WorldObject> objects = Mixed(20, 6);
        WorldLayout before = ForestWorld.Arrange(10, objects);

        List<WorldObject> more = [.. Settled(objects, before), Plant(100, "pine", ForestStage.Young)];
        WorldLayout after = ForestWorld.Arrange(before.Size, more);

        Assert.Equal(before.Size, after.Size);
        Assert.Equal(before.Paths, after.Paths);
        Assert.All(objects, o => Assert.Equal(before.Positions[o.Id], after.Positions[o.Id]));
        Assert.True(after.Positions.ContainsKey(100));
    }

    [Fact]
    public void Adding_a_decoration_moves_only_plants_that_stand_where_its_path_goes()
    {
        List<WorldObject> objects = Mixed(24, 5);
        WorldLayout before = ForestWorld.Arrange(10, objects);

        List<WorldObject> more = [.. Settled(objects, before), Plant(100, "cabin")];
        WorldLayout after = ForestWorld.Arrange(before.Size, more);

        Assert.Equal(before.Size, after.Size);
        Assert.Subset(PathTiles(after), PathTiles(before));
        foreach (WorldObject o in objects)
        {
            if (before.Positions[o.Id] != after.Positions[o.Id])
            {
                Assert.False(IsDecoration(o));
                Assert.Contains(before.Positions[o.Id], PathTiles(after));
            }
        }
    }

    // ---- the village ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_decoration_is_beside_a_path_and_every_path_reaches_the_plaza()
    {
        List<WorldObject> objects = Mixed(30, 10);

        WorldLayout layout = ForestWorld.Arrange(8, objects);

        HashSet<GridTile> paths = PathTiles(layout);
        Assert.Contains(ForestWorld.Plaza(layout.Size), paths);
        HashSet<GridTile> reached = [ForestWorld.Plaza(layout.Size)];
        Queue<GridTile> queue = new([ForestWorld.Plaza(layout.Size)]);
        while (queue.TryDequeue(out GridTile t))
        {
            foreach (GridTile next in Around(t).Where(paths.Contains))
            {
                if (reached.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        Assert.Equal(paths.Count, reached.Count);
        foreach (WorldObject d in objects.Where(IsDecoration))
        {
            Assert.Contains(Around(layout.Positions[d.Id]), paths.Contains);
        }
    }

    [Fact]
    public void Decorations_never_take_the_tiles_next_to_the_plaza_so_every_one_stays_reachable()
    {
        List<WorldObject> objects = Mixed(0, 20);

        WorldLayout layout = ForestWorld.Arrange(10, objects);

        Assert.Equal(objects.Count, layout.Positions.Count);
        Assert.All(layout.Positions.Values, t => Assert.False(ForestWorld.IsPlazaApproach(layout.Size, t)));
    }

    [Fact]
    public void Paths_merge_into_streets_instead_of_one_lane_per_decoration()
    {
        List<WorldObject> objects = Mixed(0, 8);

        WorldLayout layout = ForestWorld.Arrange(10, objects);

        GridTile plaza = ForestWorld.Plaza(layout.Size);
        int separateLanes = objects.Sum(o => Math.Abs(layout.Positions[o.Id].Column - plaza.Column) + Math.Abs(layout.Positions[o.Id].Row - plaza.Row) - 1);
        Assert.True(layout.Paths.Count < separateLanes * 0.8, $"{layout.Paths.Count} path tiles; separate lanes would need about {separateLanes}.");
    }

    [Fact]
    public void Nothing_overlaps_and_plants_keep_off_the_paths_and_the_plaza_margin()
    {
        List<WorldObject> objects = Mixed(40, 8);

        WorldLayout layout = ForestWorld.Arrange(8, objects);

        Assert.Equal(objects.Count, layout.Positions.Count);
        Assert.Equal(objects.Count, layout.Positions.Values.Distinct().Count());
        Assert.All(layout.Positions.Values, t => Assert.True(t.Column >= 0 && t.Row >= 0 && t.Column < layout.Size && t.Row < layout.Size));
        HashSet<GridTile> paths = PathTiles(layout);
        GridTile plaza = ForestWorld.Plaza(layout.Size);
        Assert.All(layout.Positions.Values, t => Assert.DoesNotContain(t, paths));
        foreach (WorldObject plant in objects.Where(o => !IsDecoration(o)))
        {
            GridTile t = layout.Positions[plant.Id];
            Assert.False(Math.Abs(t.Column - plaza.Column) <= 1 && Math.Abs(t.Row - plaza.Row) <= 1);
        }
    }

    [Fact]
    public void Plants_grow_in_groves_of_their_own_species()
    {
        List<WorldObject> objects = Mixed(48, 0);

        WorldLayout layout = ForestWorld.Arrange(12, objects);

        List<double> same = [];
        List<double> different = [];
        for (int i = 0; i < objects.Count; i++)
        {
            for (int j = i + 1; j < objects.Count; j++)
            {
                (objects[i].Species == objects[j].Species ? same : different).Add(Distance(layout.Positions[objects[i].Id], layout.Positions[objects[j].Id]));
            }
        }

        Assert.True(same.Average() < different.Average() * 0.85, $"Same species {same.Average():0.00} tiles apart, different species {different.Average():0.00}.");
    }

    [Fact]
    public void Bushes_line_the_paths_more_than_trees_do()
    {
        List<WorldObject> objects = [.. Mixed(0, 8), .. Enumerable.Range(0, 12).Select(i => Plant(50 + i, i % 2 == 0 ? "bush" : "oak", ForestStage.Young))];

        WorldLayout layout = ForestWorld.Arrange(10, objects);

        HashSet<GridTile> paths = PathTiles(layout);
        double Beside(string species) => objects.Where(o => o.Species == species).Average(o => Around(layout.Positions[o.Id]).Any(paths.Contains) ? 1 : 0);
        Assert.True(Beside("bush") > Beside("oak"), $"Bushes {Beside("bush"):0.00}, oaks {Beside("oak"):0.00}.");
    }

    [Fact]
    public void Bigger_stages_stand_further_out_than_saplings()
    {
        List<WorldObject> objects = [.. Enumerable.Range(0, 24).Select(i => Plant(i + 1, "oak", i % 2 == 0 ? ForestStage.Sapling : ForestStage.Majestic))];

        WorldLayout layout = ForestWorld.Arrange(10, objects);

        GridTile plaza = ForestWorld.Plaza(layout.Size);
        double Reach(ForestStage stage) => objects.Where(o => o.Stage == stage).Average(o => Distance(layout.Positions[o.Id], plaza));
        Assert.True(Reach(ForestStage.Majestic) > Reach(ForestStage.Sapling), $"Majestic {Reach(ForestStage.Majestic):0.00}, saplings {Reach(ForestStage.Sapling):0.00}.");
    }

    [Fact]
    public void Pinned_objects_stay_and_paths_route_around_them()
    {
        List<WorldObject> objects = Mixed(16, 6);
        GridTile pinnedTree = new(3, 3);
        GridTile pinnedBench = new(2, 6);
        objects.Add(Plant(90, "oak", at: pinnedTree, pinned: true));
        objects.Add(Plant(91, "bench", at: pinnedBench, pinned: true));

        WorldLayout layout = ForestWorld.Arrange(10, objects);

        Assert.Equal(pinnedTree, layout.Positions[90]);
        Assert.Equal(pinnedBench, layout.Positions[91]);
        Assert.DoesNotContain(pinnedTree, PathTiles(layout));
        Assert.DoesNotContain(pinnedBench, PathTiles(layout));
        Assert.Contains(Around(pinnedBench), PathTiles(layout).Contains);
    }

    [Fact]
    public void Unpinned_objects_with_a_position_stay_unless_a_path_needs_their_tile()
    {
        List<WorldObject> objects = [Plant(1, "oak", at: new GridTile(0, 0)), Plant(2, "oak", at: new GridTile(5, 5)), Plant(3, "bench")];

        WorldLayout layout = ForestWorld.Arrange(10, objects);

        Assert.Equal(new GridTile(0, 0), layout.Positions[1]);
        Assert.NotEqual(new GridTile(5, 5), layout.Positions[2]);   // the plaza belongs to the village
    }

    [Fact]
    public void A_full_field_grows_by_two_and_everything_shifts_by_one()
    {
        List<WorldObject> objects = [.. Enumerable.Range(0, 24).Select(i => Plant(i + 1, "oak", at: new GridTile(i % 6, i / 6)))];

        WorldLayout layout = ForestWorld.Arrange(6, objects);

        Assert.Equal(8, layout.Size);
        Assert.Equal(24, layout.Positions.Count);
        // Only the oak that now stands on the plaza is moved.
        Assert.Equal(23, objects.Count(o => layout.Positions[o.Id] == new GridTile(o.Position!.Value.Column + 1, o.Position.Value.Row + 1)));
    }

    [Fact]
    public void The_field_keeps_a_free_share_while_it_can_grow()
    {
        WorldLayout layout = ForestWorld.Arrange(6, Mixed(40, 8));

        int tiles = layout.Size * layout.Size;
        Assert.True(layout.Positions.Count + layout.Paths.Count <= tiles * 0.65, $"{layout.Positions.Count} objects and {layout.Paths.Count} path tiles on {tiles} tiles.");
        Assert.Equal(0, layout.Size % 2);
    }

    [Fact]
    public void The_largest_field_does_not_grow_and_takes_what_fits()
    {
        List<WorldObject> objects = Mixed(300, 0);

        WorldLayout layout = ForestWorld.Arrange(ForestLimits.MaxSize, objects);

        Assert.Equal(ForestLimits.MaxSize, layout.Size);
        Assert.Equal((ForestLimits.MaxSize * ForestLimits.MaxSize) - 1, layout.Positions.Count);   // every tile but the plaza
        Assert.Single(layout.Paths);
    }

    [Fact]
    public void A_full_arrange_of_two_hundred_objects_on_the_largest_field_is_fast()
    {
        List<WorldObject> objects = Mixed(165, 35);
        ForestWorld.Arrange(ForestLimits.MaxSize, objects);   // JIT warm-up

        // The host gives a plugin test 5 seconds: 40 layouts must stay far below that (about 8 ms each on a developer machine).
        WorldLayout layout = ForestWorld.Arrange(ForestLimits.MaxSize, objects);
        for (int i = 0; i < 40; i++)
        {
            layout = ForestWorld.Arrange(ForestLimits.MaxSize, objects);
        }

        Assert.Equal(objects.Count, layout.Positions.Count);
    }

    // ---- autotile and path models --------------------------------------------------------------------------------

    private static (ForestPathShape Shape, int Rotation) AutotileOf(params GridTile[] neighbours)
    {
        GridTile centre = new(1, 1);
        return ForestWorld.Autotile([centre, .. neighbours]).Where(t => (t.Column, t.Row) == (1, 1)).Select(t => (t.Shape, t.Rotation)).Single();
    }

    [Theory]
    [InlineData(0, ForestPathShape.Single, 0)]
    [InlineData(1, ForestPathShape.End, 0)]
    [InlineData(2, ForestPathShape.End, 1)]
    [InlineData(4, ForestPathShape.End, 2)]
    [InlineData(8, ForestPathShape.End, 3)]
    [InlineData(5, ForestPathShape.Straight, 0)]
    [InlineData(10, ForestPathShape.Straight, 1)]
    [InlineData(3, ForestPathShape.Corner, 0)]
    [InlineData(6, ForestPathShape.Corner, 1)]
    [InlineData(12, ForestPathShape.Corner, 2)]
    [InlineData(9, ForestPathShape.Corner, 3)]
    [InlineData(7, ForestPathShape.Tee, 0)]
    [InlineData(14, ForestPathShape.Tee, 1)]
    [InlineData(13, ForestPathShape.Tee, 2)]
    [InlineData(11, ForestPathShape.Tee, 3)]
    [InlineData(15, ForestPathShape.Cross, 0)]
    public void A_path_tile_takes_its_shape_and_rotation_from_its_neighbours(int mask, ForestPathShape shape, int rotation)
    {
        // Bits: east 1, south 2, west 4, north 8.
        GridTile[] neighbours = [.. Around(new GridTile(1, 1)).Where((_, d) => (mask & (1 << d)) != 0)];

        Assert.Equal((shape, rotation), AutotileOf(neighbours));
    }

    private static (double X, double Z) Turn(double x, double z, int turns) => (turns & 3) switch
    {
        1 => (-z, x),
        2 => (-x, -z),
        3 => (z, -x),
        _ => (x, z),
    };

    private static bool Inside(UiSceneFace face, double x, double z)
    {
        // Faces are convex: the point is inside when it lies on the same side of every edge.
        int count = face.Points.Count / 3;
        bool positive = false, negative = false;
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            double cross = ((face.Points[3 * next] - face.Points[3 * i]) * (z - face.Points[(3 * i) + 2])) - ((face.Points[(3 * next) + 2] - face.Points[(3 * i) + 2]) * (x - face.Points[3 * i]));
            positive |= cross > 0;
            negative |= cross < 0;
        }

        return !(positive && negative);
    }

    // Whether any face of the model covers the point, with the model turned by the given quarter turns.
    private static bool PathAt(UiSceneModel model, double x, double z, int turns)
    {
        (double lx, double lz) = Turn(x, z, (4 - turns) & 3);
        return model.Faces.Any(f => Inside(f, lx, lz));
    }

    [Fact]
    public void Each_model_opens_exactly_the_sides_the_autotile_asks_for_at_the_edge_midpoints()
    {
        for (int mask = 0; mask < 16; mask++)
        {
            GridTile[] neighbours = [.. Around(new GridTile(1, 1)).Where((_, d) => (mask & (1 << d)) != 0)];
            (ForestPathShape shape, int rotation) = AutotileOf(neighbours);
            UiSceneModel model = ForestPaths.Model(shape);
            (double X, double Z)[] sides = [(0.49, 0), (0, 0.49), (-0.49, 0), (0, -0.49)];
            for (int d = 0; d < 4; d++)
            {
                Assert.True(((mask & (1 << d)) != 0) == PathAt(model, sides[d].X, sides[d].Z, rotation), $"Mask {mask} ({shape}, turn {rotation}), side {d}.");
            }

            Assert.True(PathAt(model, 0, 0, rotation));
        }
    }

    [Fact]
    public void Neighbouring_pieces_meet_with_the_same_width_on_every_edge()
    {
        foreach (ForestPathShape shape in Enum.GetValues<ForestPathShape>().Where(s => s != ForestPathShape.Single))
        {
            UiSceneModel model = ForestPaths.Model(shape);
            foreach (int arm in ForestPaths.BaseArms(shape))
            {
                (double X, double Z) along = Turn(1, 0, arm);
                (double X, double Z) across = Turn(0, 1, arm);
                // Across the edge of the arm: trodden strip and fringe within half a width plus the fringe, nothing beyond.
                double edge = ForestPaths.HalfWidth + ForestPaths.FringeWidth;
                foreach (double offset in new[] { -edge + 0.005, -ForestPaths.HalfWidth, -0.05, 0, 0.05, ForestPaths.HalfWidth, edge - 0.005 })
                {
                    Assert.True(PathAt(model, (along.X * 0.499) + (across.X * offset), (along.Z * 0.499) + (across.Z * offset), 0), $"{shape}, arm {arm}, offset {offset}.");
                }

                foreach (double offset in new[] { -edge - 0.01, edge + 0.01 })
                {
                    Assert.False(PathAt(model, (along.X * 0.499) + (across.X * offset), (along.Z * 0.499) + (across.Z * offset), 0), $"{shape}, arm {arm}, offset {offset}.");
                }
            }
        }
    }

    [Fact]
    public void Path_models_are_flat_wound_upwards_and_small()
    {
        foreach (ForestPathShape shape in Enum.GetValues<ForestPathShape>())
        {
            UiSceneModel model = ForestPaths.Model(shape);

            Assert.Equal("path-" + shape.ToString().ToLowerInvariant(), model.Key);
            Assert.Same(model, ForestPaths.Model(shape));
            Assert.All(model.Faces, f =>
            {
                Vec[] points = [.. Enumerable.Range(0, f.Points.Count / 3).Select(i => new Vec(f.Points[3 * i], f.Points[(3 * i) + 1], f.Points[(3 * i) + 2]))];
                Assert.All(points, p => Assert.InRange(p.Y, 0.005, 0.015));
                Assert.All(points, p => Assert.InRange(Math.Max(Math.Abs(p.X), Math.Abs(p.Z)), 0, 0.5));
                Vec normal = Mesh.Normal(points).Normalized();
                Assert.True(normal.Y > 0.999, "A path face does not point up.");
            });
            Assert.True(UiJson.Serialize(new UiScene("m", 1, 1, [model], [])).Length < 6 * 1024, $"{shape} is {UiJson.Serialize(new UiScene("m", 1, 1, [model], [])).Length} bytes.");
        }
    }

    [Fact]
    public void Path_pieces_do_not_stack_faces_and_stay_narrow()
    {
        foreach (ForestPathShape shape in Enum.GetValues<ForestPathShape>())
        {
            // The renderer sorts faces by their centre, so faces that overlap in plan would flicker: no point is covered twice.
            UiSceneModel model = ForestPaths.Model(shape);
            for (int i = 0; i < 100; i++)
            {
                for (int j = 0; j < 100; j++)
                {
                    double x = -0.5 + ((i + 0.37) / 100), z = -0.5 + ((j + 0.61) / 100);
                    Assert.True(model.Faces.Count(f => Inside(f, x, z)) <= 1, $"{shape} stacks faces at ({x:0.00}, {z:0.00}).");
                }
            }

            if (shape == ForestPathShape.Straight)
            {
                // The strip is about 0.3 wide: the middle of the tile is covered, a quarter of a tile to the side is not.
                Assert.True(PathAt(model, 0, 0.1, 0));
                Assert.False(PathAt(model, 0, 0.25, 0));
            }
        }
    }

    // ---- scene ---------------------------------------------------------------------------------------------------

    private static ForestView ViewOf(IReadOnlyList<ForestObject> objects, IReadOnlyList<ForestPathTile> paths, int size = 8) =>
        new(0, "oak", new HashSet<string>(), new Dictionary<string, int>(), size, objects, null, null) { Paths = paths };

    [Fact]
    public void The_scene_draws_path_tiles_first_unlabelled_and_full_size_and_keeps_them_out_of_the_species_budget()
    {
        ForestObject tree = new() { Id = 1, Species = "oak", Stage = (int)ForestStage.Mature, Column = 0, Row = 0 };
        ForestPathTile[] paths = [new(3, 3, ForestPathShape.Corner, 2), new(4, 3, ForestPathShape.Tee, 1), new(3, 4, ForestPathShape.End, 0)];

        UiScene scene = ForestSceneBuilder.Build(ViewOf([tree], paths), "scene", 500, allowPick: true, selectedObjectId: "forest-obj-1");

        Assert.Equal(["path-3-3", "path-4-3", "path-3-4", "forest-obj-1"], scene.Items.Select(i => i.Id).ToArray());
        Assert.All(scene.Items.Take(3), i => Assert.Null(i.Label));
        Assert.All(scene.Items.Take(3), i => Assert.Equal(ForestPaths.ItemScale, i.Scale));
        Assert.Equal(["path-corner", "path-end", "path-tee"], scene.Models.Take(3).Select(m => m.Key).Order().ToArray());
        Assert.Equal(2, scene.Items[0].Rotation);
        Assert.Equal("oak-mature", scene.Items[3].Model);
        Assert.True(scene.Items[3].IsSelected);
        Assert.True(scene.AllowPick);
        Assert.Equal((8, 8, 500d), (scene.Columns, scene.Rows, scene.Height));
        Assert.True(UiTreeValidator.Validate(Ui.Page("Forest", [scene])).IsValid);
    }

    [Fact]
    public void A_whole_village_builds_a_valid_scene_within_the_document_limit()
    {
        List<WorldObject> world = Mixed(60, 10);
        WorldLayout layout = ForestWorld.Arrange(8, world);
        ForestObject[] objects =
        [
            .. world.Select(o => new ForestObject { Id = o.Id, Species = o.Species, Stage = (int)o.Stage, Column = layout.Positions[o.Id].Column, Row = layout.Positions[o.Id].Row }),
        ];

        UiPage page = Ui.Page("Forest", [ForestSceneBuilder.Build(ViewOf(objects, layout.Paths, layout.Size), "scene", 600, false, null)]);

        Assert.True(UiTreeValidator.Validate(page).IsValid);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(UiJson.Serialize(page)) <= UiJson.MaxDocumentBytes);
    }
}
