using System.Windows;
using System.Windows.Media;
using Blocker.UI.Controls.Rendering;
using Blocker.UI.Sdk;
using Vec3 = Blocker.UI.Controls.Rendering.SceneMath.Vec3;

namespace Blocker.UI.Controls.Tests;

public sealed class SceneMathTests
{
    private static readonly Color Grey = Color.FromRgb(200, 200, 200);

    // A unit tile top wound as the SDK documents: (-x,-z), (-x,+z), (+x,+z), (+x,-z).
    private static readonly Vec3[] Top = [new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0)];

    [Fact]
    public void A_top_face_is_visible_and_the_brightest_side()
    {
        Assert.True(SceneMath.TryShade(Top, Grey, out Color top));
        Assert.True(SceneMath.TryShade([new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)], Grey, out Color front));   // normal +z
        Assert.True(SceneMath.TryShade([new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1)], Grey, out Color right));   // normal +x

        Assert.True(top.R > front.R && front.R > right.R);
    }

    [Fact]
    public void A_face_wound_the_other_way_is_culled() =>
        Assert.False(SceneMath.TryShade([.. Top.Reverse()], Grey, out _));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TileAt_inverts_the_tile_outline_for_every_camera_turn(int turns)
    {
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 5; column++)
            {
                Point[] outline = SceneMath.TileOutline(5, 3, turns, column, row);
                var centre = new Point(outline.Average(p => p.X), outline.Average(p => p.Y));

                Assert.Equal((column, row), SceneMath.TileAt(5, 3, turns, centre));
            }
        }
    }

    [Fact]
    public void TileAt_outside_the_grid_is_null() =>
        Assert.Null(SceneMath.TileAt(4, 4, 0, new Point(0, -50)));

    [Fact]
    public void Items_are_drawn_back_to_front()
    {
        var cube = new UiSceneModel("m", [new UiSceneFace([-0.3, 0.5, -0.3, -0.3, 0.5, 0.3, 0.3, 0.5, 0.3, 0.3, 0.5, -0.3], "#336633")]);
        var scene = new UiScene("s", 3, 3, [cube], [new UiSceneItem("front", "m", 2, 2), new UiSceneItem("back", "m", 0, 0)], ShowGround: false);

        string?[] order = [.. SceneMath.Project(scene, 0).Polygons.Select(p => p.ItemId)];

        Assert.Equal(["back", "front"], order);
        Assert.Equal(["front", "back"], SceneMath.Project(scene, 2).Polygons.Select(p => p.ItemId));
    }

    [Fact]
    public void Ground_draws_the_tiles_and_two_visible_sides()
    {
        var scene = new UiScene("s", 2, 3, [], []);

        SceneProjection projection = SceneMath.Project(scene, 0);

        Assert.Equal((2 * 3) + 2, projection.Polygons.Count);
        Assert.All(projection.Polygons, p => Assert.Null(p.ItemId));
        Assert.False(projection.Bounds.IsEmpty);
    }
}
