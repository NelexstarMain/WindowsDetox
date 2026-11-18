using SkiaSharp;

namespace Blocker.UI.Controls.Metaballs;

/// <summary>
/// Draws the blueprint grid at full resolution from cached 4x4-cell tiles (so lines stay one device pixel wide) and
/// brightens it under the cursor with a radially masked second tile.
/// </summary>
internal sealed class BlueprintGridPainter : IDisposable
{
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    private readonly SKPaint _paint = new();
    private SKShader? _baseShader;
    private SKShader? _boostShader;
    private int _cell;
    private ThemeColors? _colors;

    /// <summary>Fills the canvas with the grid.</summary>
    /// <param name="canvas">Target canvas in device pixels.</param>
    /// <param name="width">Canvas width.</param>
    /// <param name="height">Canvas height.</param>
    /// <param name="pixelsPerDip">Device scale.</param>
    /// <param name="colors">Palette.</param>
    public void DrawBase(SKCanvas canvas, int width, int height, float pixelsPerDip, ThemeColors colors)
    {
        EnsureTiles(pixelsPerDip, colors);
        _paint.Shader = _baseShader;
        canvas.DrawRect(0, 0, width, height, _paint);
        _paint.Shader = null;
    }

    /// <summary>Draws brighter grid lines around the cursor.</summary>
    /// <param name="canvas">Target canvas in device pixels.</param>
    /// <param name="x">Cursor X in device pixels.</param>
    /// <param name="y">Cursor Y in device pixels.</param>
    /// <param name="pixelsPerDip">Device scale.</param>
    /// <param name="strength">Cursor presence, 0..1.</param>
    /// <param name="colors">Palette.</param>
    public void DrawSpotlight(SKCanvas canvas, float x, float y, float pixelsPerDip, float strength, ThemeColors colors)
    {
        EnsureTiles(pixelsPerDip, colors);
        if (_boostShader is null || strength <= 0.01f)
        {
            return;
        }

        float radius = MetaballTuning.SpotRadius * pixelsPerDip;
        var center = new SKPoint(x, y);
        SKColor[] stops = [new SKColor(255, 255, 255, Rgba.ToByte(strength)), new SKColor(255, 255, 255, 0)];
        using SKShader mask = SKShader.CreateRadialGradient(center, radius, stops, SKShaderTileMode.Clamp);
        using SKShader masked = SKShader.CreateCompose(_boostShader, mask, SKBlendMode.DstIn);
        _paint.Shader = masked;
        canvas.Save();
        canvas.ClipRect(new SKRect(x - radius, y - radius, x + radius, y + radius));
        canvas.DrawPaint(_paint);
        canvas.Restore();
        _paint.Shader = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _baseShader?.Dispose();
        _boostShader?.Dispose();
        _paint.Dispose();
    }

    private void EnsureTiles(float pixelsPerDip, ThemeColors colors)
    {
        int cell = Math.Max(4, (int)MathF.Round(MetaballTuning.GridCell * pixelsPerDip));
        if (_baseShader is not null && cell == _cell && ReferenceEquals(colors, _colors))
        {
            return;
        }

        _baseShader?.Dispose();
        _boostShader?.Dispose();
        _baseShader = CreateTile(cell, colors.GridSubtle, colors.GridStrong);
        _boostShader = CreateTile(cell, colors.GridBoost, colors.GridBoost);
        _cell = cell;
        _colors = colors;
    }

    private static SKShader CreateTile(int cell, Rgba minor, Rgba major)
    {
        int size = cell * 4;
        using SKSurface surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };

        paint.Color = ToColor(minor);
        for (int i = 1; i < 4; i++)
        {
            canvas.DrawRect(i * cell, 0, 1, size, paint);
            canvas.DrawRect(0, i * cell, size, 1, paint);
        }

        paint.Color = ToColor(major);
        canvas.DrawRect(0, 0, 1, size, paint);
        canvas.DrawRect(0, 0, size, 1, paint);

        using SKImage image = surface.Snapshot();
        return image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, Nearest);
    }

    private static SKColor ToColor(Rgba c) => new(Rgba.ToByte(c.R), Rgba.ToByte(c.G), Rgba.ToByte(c.B), Rgba.ToByte(c.A));
}
