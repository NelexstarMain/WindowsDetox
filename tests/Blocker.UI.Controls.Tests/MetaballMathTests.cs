using Blocker.UI.Controls.Metaballs;

namespace Blocker.UI.Controls.Tests;

public sealed class MetaballMathTests
{
    [Fact]
    public void Spring_halves_the_error_after_one_half_life_when_starting_at_rest()
    {
        float x = 100f;
        float v = 0f;
        const int steps = 70;
        for (int i = 0; i < steps; i++)
        {
            Spring.Step(ref x, ref v, 0f, 0.07f, 0.07f / steps);
        }

        Assert.InRange(x, 49f, 51f);
    }

    [Fact]
    public void Spring_is_independent_of_the_step_size()
    {
        float coarseX = 40f, coarseV = 0f, fineX = 40f, fineV = 0f;
        Spring.Step(ref coarseX, ref coarseV, 10f, 0.07f, 0.1f);
        for (int i = 0; i < 100; i++)
        {
            Spring.Step(ref fineX, ref fineV, 10f, 0.07f, 0.001f);
        }

        Assert.Equal(fineX, coarseX, 0.01f);
        Assert.Equal(fineV, coarseV, 0.1f);
    }

    [Fact]
    public void Spring_never_overshoots_the_goal()
    {
        float x = 0f;
        float v = 0f;
        for (int i = 0; i < 600; i++)
        {
            Spring.Step(ref x, ref v, 50f, 0.07f, 1f / 60f);
            Assert.True(x <= 50.0001f);
        }

        Assert.Equal(50f, x, 0.01f);
    }

    [Fact]
    public void Scene_settles_and_reports_idle()
    {
        var scene = new MetaballScene();
        scene.SetBodies([new BodyTarget(1, 0, 0, 100, 50, 12)]);
        Assert.True(scene.SetBodies([new BodyTarget(1, 200, 0, 100, 50, 12)]));

        bool moving = true;
        for (int i = 0; i < 600 && moving; i++)
        {
            moving = scene.Step(1f / 60f);
        }

        Assert.False(moving);
        Assert.Equal(200f, scene.Bodies[0]);
    }

    [Fact]
    public void Scene_keeps_spring_state_by_id_when_order_changes()
    {
        var scene = new MetaballScene();
        scene.SetBodies([new BodyTarget(1, 0, 0, 10, 10, 2), new BodyTarget(2, 100, 0, 10, 10, 2)]);
        scene.SetBodies([new BodyTarget(2, 100, 0, 10, 10, 2)]);

        Assert.Equal(1, scene.Count);
        Assert.Equal(100f, scene.Bodies[0]);
    }

    [Fact]
    public void Scene_caps_bodies_at_the_shader_limit()
    {
        var scene = new MetaballScene();
        var many = new BodyTarget[MetaballScene.MaxBodies + 8];
        for (int i = 0; i < many.Length; i++)
        {
            many[i] = new BodyTarget(i, i, 0, 10, 10, 2);
        }

        scene.SetBodies(many);
        Assert.Equal(MetaballScene.MaxBodies, scene.Count);
    }

    [Fact]
    public void Scene_identical_targets_report_no_change()
    {
        var scene = new MetaballScene();
        BodyTarget[] bodies = [new BodyTarget(1, 5, 5, 20, 20, 4)];
        scene.SetBodies(bodies);
        Assert.False(scene.SetBodies(bodies));
    }

    [Fact]
    public void Cursor_fades_in_and_out()
    {
        var scene = new MetaballScene();
        scene.SetCursor(10, 10);
        for (int i = 0; i < 120; i++)
        {
            scene.Step(1f / 60f);
        }

        Assert.Equal(1f, scene.CursorStrength, 0.01f);
        scene.ClearCursor();
        bool moving = true;
        for (int i = 0; i < 300 && moving; i++)
        {
            moving = scene.Step(1f / 60f);
        }

        Assert.False(moving);
        Assert.Equal(0f, scene.CursorStrength);
    }

    [Fact]
    public void Packer_scales_rects_to_half_resolution_and_zero_fills_the_rest()
    {
        float[] bodies = [10, 20, 100, 60, 12, 40, 50, 30, 30, 40];
        var rects = new float[MetaballScene.MaxBodies * 4];
        var radii = new float[MetaballScene.MaxBodies];
        Array.Fill(rects, 9f);
        Array.Fill(radii, 9f);

        int packed = UniformPacker.Pack(bodies, 2, 0.5f, rects, radii);

        Assert.Equal(2, packed);
        Assert.Equal([5f, 10f, 50f, 30f], rects[..4]);
        Assert.Equal(6f, radii[0]);
        Assert.Equal([20f, 25f, 15f, 15f], rects[4..8]);
        Assert.Equal(15f * 0.5f, radii[1]);
        Assert.All(rects[8..], v => Assert.Equal(0f, v));
        Assert.All(radii[2..], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Packer_clamps_radius_to_half_the_short_side()
    {
        float[] bodies = [0, 0, 20, 10, 50];
        var rects = new float[MetaballScene.MaxBodies * 4];
        var radii = new float[MetaballScene.MaxBodies];

        UniformPacker.Pack(bodies, 1, 1f, rects, radii);

        Assert.Equal(5f, radii[0]);
    }

    [Fact]
    public void Alpha_threshold_matrix_keeps_rgb_and_maps_alpha()
    {
        float[] matrix = AlphaThreshold.CreateMatrix();

        Assert.Equal(20, matrix.Length);
        Assert.Equal([1f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f], matrix[..15]);
        Assert.Equal([0f, 0f, 0f, 20f, -9f], matrix[15..]);
    }

    [Theory]
    [InlineData(MetaballQuality.High, true, MetaballQuality.High)]
    [InlineData(MetaballQuality.Low, true, MetaballQuality.Low)]
    [InlineData(MetaballQuality.Off, true, MetaballQuality.Off)]
    [InlineData(MetaballQuality.High, false, MetaballQuality.Off)]
    public void Reduced_motion_forces_quality_off(MetaballQuality requested, bool animations, MetaballQuality expected) =>
        Assert.Equal(expected, MetaballQualityResolver.Resolve(requested, animations));

    [Fact]
    public void Resolution_scale_is_half_for_high_and_quarter_for_low()
    {
        Assert.Equal(0.5f, MetaballQualityResolver.ResolutionScale(MetaballQuality.High));
        Assert.Equal(0.25f, MetaballQualityResolver.ResolutionScale(MetaballQuality.Low));
    }

    [Fact]
    public void Rgba_premultiplies_and_converts_from_packed_argb()
    {
        Rgba c = Rgba.FromArgb(0x80FF0000);

        Assert.Equal(1f, c.R);
        Assert.Equal(128 / 255f, c.A, 1e-6f);
        Rgba p = c.Premultiplied();
        Assert.Equal(c.A, p.R, 1e-6f);
        Assert.Equal(c.A, p.A, 1e-6f);
    }

    [Fact]
    public void Theme_colors_derive_translucent_accents()
    {
        ThemeColors colors = ThemeColors.Default;

        Assert.Equal(1f, colors.Fill.A);
        Assert.Equal(0.55f, colors.Glow.A);
        Assert.Equal(colors.Glow.G, colors.Haze.G);
        Assert.True(colors.GridStrong.A > colors.GridSubtle.A);
    }

    [Fact]
    public void Frame_hides_the_cursor_when_absent_and_scales_blend_distances()
    {
        var scene = new MetaballScene();
        var frame = new MetaballFrame();

        frame.Fill(scene, 0.5f, ThemeColors.Default);

        Assert.Equal(0f, frame.CursorRadius);
        Assert.True(frame.Cursor[0] < -1000f);
        Assert.Equal(MetaballTuning.BodyBlend * 0.5f, frame.BodyBlend);
    }
}
