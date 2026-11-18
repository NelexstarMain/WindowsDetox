namespace Blocker.UI.Controls.Metaballs;

/// <summary>Reusable, preallocated per-frame data handed to a renderer. Everything is in mask-layer pixels.</summary>
internal sealed class MetaballFrame
{
    /// <summary>Gets the packed <c>float4</c> rectangles.</summary>
    public float[] Rects { get; } = new float[MetaballScene.MaxBodies * 4];

    /// <summary>Gets the per-body corner radii.</summary>
    public float[] Radii { get; } = new float[MetaballScene.MaxBodies];

    /// <summary>Gets the premultiplied fill colour (RGBA).</summary>
    public float[] FillColor { get; } = new float[4];

    /// <summary>Gets the premultiplied glow colour (RGBA).</summary>
    public float[] GlowColor { get; } = new float[4];

    /// <summary>Gets the premultiplied haze colour scaled by cursor presence (RGBA).</summary>
    public float[] HazeColor { get; } = new float[4];

    /// <summary>Gets the cursor position (x, y).</summary>
    public float[] Cursor { get; } = new float[2];

    /// <summary>Gets the number of packed bodies.</summary>
    public int Count { get; private set; }

    /// <summary>Gets the cursor blob radius (zero when hidden).</summary>
    public float CursorRadius { get; private set; }

    /// <summary>Gets the cursor presence from 0 to 1.</summary>
    public float CursorStrength { get; private set; }

    /// <summary>Gets the body-to-body blend distance.</summary>
    public float BodyBlend { get; private set; }

    /// <summary>Gets the cursor blend distance.</summary>
    public float CursorBlend { get; private set; }

    /// <summary>Gets the anti-aliasing half width.</summary>
    public float AntiAlias { get; private set; }

    /// <summary>Gets the glow ring width.</summary>
    public float RingWidth { get; private set; }

    /// <summary>Gets the haze radius.</summary>
    public float HazeRadius { get; private set; }

    /// <summary>Fills the frame from <paramref name="scene"/>.</summary>
    /// <param name="scene">Animation state.</param>
    /// <param name="layerScale">DIPs to mask-layer pixels.</param>
    /// <param name="colors">Palette.</param>
    public void Fill(MetaballScene scene, float layerScale, ThemeColors colors)
    {
        Count = UniformPacker.Pack(scene.Bodies, scene.Count, layerScale, Rects, Radii);

        float strength = scene.CursorStrength;
        CursorStrength = strength;
        bool shown = strength > 0.001f;
        Cursor[0] = shown ? scene.CursorX * layerScale : -1e6f;
        Cursor[1] = shown ? scene.CursorY * layerScale : -1e6f;
        float eased = strength * strength * (3f - (2f * strength));
        CursorRadius = shown ? MetaballTuning.CursorRadius * layerScale * eased : 0f;

        BodyBlend = MetaballTuning.BodyBlend * layerScale;
        CursorBlend = MetaballTuning.CursorBlend * layerScale;
        AntiAlias = MathF.Max(0.75f, layerScale * 0.75f);
        RingWidth = MathF.Max(1f, MetaballTuning.RingWidth * layerScale);
        HazeRadius = MetaballTuning.SpotRadius * layerScale;

        colors.Fill.Premultiplied().WriteTo(FillColor);
        colors.Glow.Premultiplied().WriteTo(GlowColor);
        colors.Haze.Premultiplied(strength).WriteTo(HazeColor);
    }
}
