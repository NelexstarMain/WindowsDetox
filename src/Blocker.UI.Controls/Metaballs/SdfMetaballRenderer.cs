using System.Diagnostics;
using SkiaSharp;

namespace Blocker.UI.Controls.Metaballs;

/// <summary>Draws the metaball mask into a transparent layer. Never renders text.</summary>
internal interface IMetaballRenderer : IDisposable
{
    /// <summary>Renders <paramref name="frame"/> into <paramref name="canvas"/>.</summary>
    /// <param name="canvas">Layer canvas, already cleared to transparent.</param>
    /// <param name="frame">Frame data in layer pixels.</param>
    /// <param name="width">Layer width.</param>
    /// <param name="height">Layer height.</param>
    /// <returns><see langword="false"/> when the backend cannot draw and the caller should switch to the fallback.</returns>
    bool Render(SKCanvas canvas, MetaballFrame frame, int width, int height);
}

/// <summary>Picks the metaball backend.</summary>
internal static class MetaballRenderers
{
    /// <summary>Creates the SDF backend, or the blur-threshold fallback when the SkSL effect cannot compile.</summary>
    /// <returns>A renderer.</returns>
    public static IMetaballRenderer Create() => SdfMetaballRenderer.TryCreate() ?? (IMetaballRenderer)new BlurThresholdMetaballRenderer();
}

/// <summary>
/// Signed-distance-field backend: an SkSL runtime effect evaluates rounded-rectangle SDFs for every body plus a
/// circle for the cursor, joins them with a polynomial smooth minimum and shades fill, glow ring and haze.
/// </summary>
internal sealed class SdfMetaballRenderer : IMetaballRenderer
{
    /// <summary>The SkSL source. Uniforms are in layer pixels; the output is premultiplied.</summary>
    internal const string Source = """
        uniform float4 rects[32];
        uniform float radii[32];
        uniform float bodyCount;
        uniform float2 cursorPos;
        uniform float cursorRadius;
        uniform float blendBody;
        uniform float blendCursor;
        uniform float aaWidth;
        uniform float ringWidth;
        uniform float hazeRadius;
        uniform float4 fillColor;
        uniform float4 glowColor;
        uniform float4 hazeColor;

        float sdRoundRect(float2 p, float4 r, float rad) {
            float2 hs = r.zw * 0.5;
            float2 q = abs(p - (r.xy + hs)) - hs + float2(rad);
            return length(max(q, float2(0.0))) + min(max(q.x, q.y), 0.0) - rad;
        }

        float smoothMin(float a, float b, float kk) {
            float h = max(kk - abs(a - b), 0.0) / kk;
            return min(a, b) - h * h * kk * 0.25;
        }

        half4 main(float2 p) {
            float dBody = 100000.0;
            float dHard = 100000.0;
            for (int i = 0; i < 32; i++) {
                if (float(i) >= bodyCount) { break; }
                float d = sdRoundRect(p, rects[i], radii[i]);
                dHard = min(dHard, d);
                dBody = smoothMin(dBody, d, blendBody);
            }

            float dCursor = length(p - cursorPos) - cursorRadius;
            float dField = smoothMin(dBody, dCursor, blendCursor);
            float bridge = max(min(dHard, dCursor) - dField, 0.0);

            float coverage = 1.0 - smoothstep(-aaWidth, aaWidth, dField);
            float nearBody = 1.0 - smoothstep(0.0, blendCursor * 1.5, dBody);
            float fillAlpha = coverage * mix(0.30, 1.0, nearBody);

            float2 toCursor = p - cursorPos;
            float haze = exp(-dot(toCursor, toCursor) / (hazeRadius * hazeRadius));
            float4 color = hazeColor * haze;

            float4 fill = fillColor * fillAlpha;
            color = fill + color * (1.0 - fill.a);

            float bridgeAmount = clamp(bridge / (blendCursor * 0.25), 0.0, 1.0);
            float ring = (1.0 - smoothstep(0.0, ringWidth + aaWidth, abs(dField))) * (0.25 + 0.75 * bridgeAmount);
            float4 glow = glowColor * ring;
            color = glow + color * (1.0 - glow.a);

            return half4(color);
        }
        """;

    private readonly SKRuntimeEffect _effect;
    private readonly SKRuntimeEffectUniforms _uniforms;
    private readonly float[] _count = new float[1];
    private readonly float[] _cursorRadius = new float[1];
    private readonly float[] _blendBody = new float[1];
    private readonly float[] _blendCursor = new float[1];
    private readonly float[] _aa = new float[1];
    private readonly float[] _ring = new float[1];
    private readonly float[] _haze = new float[1];
    private readonly SKPaint _paint = new();

    private SdfMetaballRenderer(SKRuntimeEffect effect)
    {
        _effect = effect;
        _uniforms = new SKRuntimeEffectUniforms(effect);
    }

    /// <summary>Compiles the effect and dry-runs it once.</summary>
    /// <returns>The renderer, or <see langword="null"/> when the effect or the dry run fails.</returns>
    public static SdfMetaballRenderer? TryCreate()
    {
        SdfMetaballRenderer? renderer = null;
        try
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(Source, out string errors);
            if (effect is null)
            {
                Trace.TraceWarning("Metaball SkSL failed to compile, using blur-threshold fallback: " + errors);
                return null;
            }

            renderer = new SdfMetaballRenderer(effect);
            using SKSurface probe = SKSurface.Create(new SKImageInfo(4, 4));
            var frame = new MetaballFrame();
            frame.Fill(new MetaballScene(), 1f, ThemeColors.Default);
            if (probe is null || !renderer.Render(probe.Canvas, frame, 4, 4))
            {
                renderer.Dispose();
                return null;
            }

            return renderer;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.TraceWarning("Metaball SDF backend unavailable, using blur-threshold fallback: " + ex.Message);
            renderer?.Dispose();
            return null;
        }
    }

    /// <inheritdoc />
    public bool Render(SKCanvas canvas, MetaballFrame frame, int width, int height)
    {
        _count[0] = frame.Count;
        _cursorRadius[0] = frame.CursorRadius;
        _blendBody[0] = frame.BodyBlend;
        _blendCursor[0] = frame.CursorBlend;
        _aa[0] = frame.AntiAlias;
        _ring[0] = frame.RingWidth;
        _haze[0] = frame.HazeRadius;

        _uniforms["rects"] = frame.Rects;
        _uniforms["radii"] = frame.Radii;
        _uniforms["bodyCount"] = _count;
        _uniforms["cursorPos"] = frame.Cursor;
        _uniforms["cursorRadius"] = _cursorRadius;
        _uniforms["blendBody"] = _blendBody;
        _uniforms["blendCursor"] = _blendCursor;
        _uniforms["aaWidth"] = _aa;
        _uniforms["ringWidth"] = _ring;
        _uniforms["hazeRadius"] = _haze;
        _uniforms["fillColor"] = frame.FillColor;
        _uniforms["glowColor"] = frame.GlowColor;
        _uniforms["hazeColor"] = frame.HazeColor;

        using SKShader? shader = _effect.ToShader(_uniforms);
        if (shader is null)
        {
            return false;
        }

        _paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, _paint);
        _paint.Shader = null;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _paint.Dispose();
        _uniforms.Dispose();
        _effect.Dispose();
    }
}
