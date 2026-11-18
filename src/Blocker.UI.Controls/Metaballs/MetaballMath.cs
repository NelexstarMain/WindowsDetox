namespace Blocker.UI.Controls.Metaballs;

/// <summary>Quality of the metaball layer.</summary>
public enum MetaballQuality
{
    /// <summary>SDF shader at half resolution.</summary>
    High,

    /// <summary>Quarter-resolution mask; cheaper on slow CPUs.</summary>
    Low,

    /// <summary>No metaball mask; only the static blueprint grid is drawn.</summary>
    Off,
}

/// <summary>Resolves the quality actually used.</summary>
internal static class MetaballQualityResolver
{
    /// <summary>Returns <see cref="MetaballQuality.Off"/> when the system disables client-area animation (reduced motion).</summary>
    /// <param name="requested">The requested quality.</param>
    /// <param name="clientAreaAnimation">Value of <c>SystemParameters.ClientAreaAnimation</c>.</param>
    /// <returns>The effective quality.</returns>
    public static MetaballQuality Resolve(MetaballQuality requested, bool clientAreaAnimation) =>
        clientAreaAnimation ? requested : MetaballQuality.Off;

    /// <summary>Gets the mask resolution relative to device pixels.</summary>
    /// <param name="quality">Effective quality.</param>
    /// <returns>Scale factor in (0, 1].</returns>
    public static float ResolutionScale(MetaballQuality quality) => quality == MetaballQuality.Low ? 0.25f : 0.5f;
}

/// <summary>Visual constants of the metaball field, in DIPs.</summary>
internal static class MetaballTuning
{
    /// <summary>Smooth-min blend distance between bodies.</summary>
    public const float BodyBlend = 20f;

    /// <summary>Smooth-min blend distance between bodies and the cursor blob.</summary>
    public const float CursorBlend = 28f;

    /// <summary>Radius of the cursor blob.</summary>
    public const float CursorRadius = 56f;

    /// <summary>Radius of the cursor spotlight over the grid.</summary>
    public const float SpotRadius = 180f;

    /// <summary>Width of the glow ring at the field edge.</summary>
    public const float RingWidth = 1.5f;

    /// <summary>Blueprint grid cell size.</summary>
    public const float GridCell = 24f;
}

/// <summary>Packs scene state into shader uniform arrays.</summary>
internal static class UniformPacker
{
    /// <summary>Writes bodies as <c>float4(x, y, width, height)</c> rectangles and per-body radii, multiplied by <paramref name="scale"/>.</summary>
    /// <param name="bodies">Source groups of <see cref="MetaballScene.Stride"/> floats.</param>
    /// <param name="count">Number of bodies.</param>
    /// <param name="scale">DIPs to layer pixels (device scale times mask resolution, for example 0.5).</param>
    /// <param name="rects">Destination of length <c>4 * MaxBodies</c>; unused entries are zeroed.</param>
    /// <param name="radii">Destination of length <c>MaxBodies</c>; unused entries are zeroed. Radii are clamped to half the shorter side.</param>
    /// <returns>The number of packed bodies.</returns>
    public static int Pack(ReadOnlySpan<float> bodies, int count, float scale, Span<float> rects, Span<float> radii)
    {
        int n = Math.Min(count, MetaballScene.MaxBodies);
        for (int i = 0; i < n; i++)
        {
            int s = i * MetaballScene.Stride;
            int r = i * 4;
            float w = MathF.Max(bodies[s + 2], 0f) * scale;
            float h = MathF.Max(bodies[s + 3], 0f) * scale;
            rects[r] = bodies[s] * scale;
            rects[r + 1] = bodies[s + 1] * scale;
            rects[r + 2] = w;
            rects[r + 3] = h;
            radii[i] = Math.Clamp(bodies[s + 4] * scale, 0f, MathF.Min(w, h) * 0.5f);
        }

        rects[(n * 4)..].Clear();
        radii[n..].Clear();
        return n;
    }
}

/// <summary>Alpha-threshold colour matrix for the blur-threshold fallback ("gooey" effect).</summary>
internal static class AlphaThreshold
{
    /// <summary>Default alpha gain.</summary>
    public const float Slope = 20f;

    /// <summary>Default alpha bias (in normalised alpha units).</summary>
    public const float Offset = -9f;

    /// <summary>Builds a row-major 4x5 colour matrix that keeps RGB and maps alpha to <c>alpha * slope + offset</c>.</summary>
    /// <param name="slope">Alpha gain.</param>
    /// <param name="offset">Alpha bias.</param>
    /// <returns>Twenty floats.</returns>
    public static float[] CreateMatrix(float slope = Slope, float offset = Offset) =>
    [
        1f, 0f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f, 0f,
        0f, 0f, 0f, slope, offset,
    ];
}
