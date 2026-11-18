using SkiaSharp;

namespace Blocker.UI.Controls.Metaballs;

/// <summary>
/// Fallback backend for machines where the SkSL effect is unavailable: shapes are blurred inside a layer and the
/// layer is passed through an alpha-threshold colour matrix, which fuses nearby shapes into one outline.
/// </summary>
internal sealed class BlurThresholdMetaballRenderer : IMetaballRenderer
{
    private readonly SKPaint _shapePaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _layerPaint = new() { ColorFilter = SKColorFilter.CreateColorMatrix(AlphaThreshold.CreateMatrix()) };
    private readonly SKPaint _hazePaint = new() { IsAntialias = true };
    private SKImageFilter? _blur;
    private float _sigma = -1f;

    /// <inheritdoc />
    public bool Render(SKCanvas canvas, MetaballFrame frame, int width, int height)
    {
        EnsureBlur(frame.BodyBlend * 0.4f);

        if (frame.CursorStrength > 0.001f)
        {
            DrawHaze(canvas, frame);
        }

        _shapePaint.Color = new SKColor(Rgba.ToByte(frame.FillColor[0]), Rgba.ToByte(frame.FillColor[1]), Rgba.ToByte(frame.FillColor[2]));
        _shapePaint.ImageFilter = _blur;

        canvas.SaveLayer(_layerPaint);
        for (int i = 0; i < frame.Count; i++)
        {
            int r = i * 4;
            float x = frame.Rects[r];
            float y = frame.Rects[r + 1];
            float radius = frame.Radii[i];
            canvas.DrawRoundRect(new SKRect(x, y, x + frame.Rects[r + 2], y + frame.Rects[r + 3]), radius, radius, _shapePaint);
        }

        if (frame.CursorRadius > 0.5f)
        {
            canvas.DrawCircle(frame.Cursor[0], frame.Cursor[1], frame.CursorRadius, _shapePaint);
        }

        canvas.Restore();
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shapePaint.Dispose();
        _layerPaint.ColorFilter?.Dispose();
        _layerPaint.Dispose();
        _hazePaint.Dispose();
        _blur?.Dispose();
    }

    private void EnsureBlur(float sigma)
    {
        float clamped = MathF.Max(sigma, 0.5f);
        if (_blur is not null && MathF.Abs(clamped - _sigma) < 0.01f)
        {
            return;
        }

        _blur?.Dispose();
        _blur = SKImageFilter.CreateBlur(clamped, clamped);
        _sigma = clamped;
    }

    private void DrawHaze(SKCanvas canvas, MetaballFrame frame)
    {
        var center = new SKPoint(frame.Cursor[0], frame.Cursor[1]);
        var tint = new SKColor(Rgba.ToByte(frame.HazeColor[0] / MathF.Max(frame.HazeColor[3], 1e-4f)), Rgba.ToByte(frame.HazeColor[1] / MathF.Max(frame.HazeColor[3], 1e-4f)), Rgba.ToByte(frame.HazeColor[2] / MathF.Max(frame.HazeColor[3], 1e-4f)), Rgba.ToByte(frame.HazeColor[3]));
        using SKShader gradient = SKShader.CreateRadialGradient(center, frame.HazeRadius, [tint, tint.WithAlpha(0)], SKShaderTileMode.Clamp);
        _hazePaint.Shader = gradient;
        canvas.DrawCircle(center, frame.HazeRadius, _hazePaint);
        _hazePaint.Shader = null;
    }
}
