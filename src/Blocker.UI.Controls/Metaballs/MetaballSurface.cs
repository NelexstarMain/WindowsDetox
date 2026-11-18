using System.Windows;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Blocker.UI.Controls.Metaballs;

/// <summary>
/// Skia surface that paints the blueprint grid, the cursor spotlight and the metaball mask behind the UI. Text is never
/// drawn here. Use it through <see cref="MetaballHost"/>, which feeds it body bounds and the cursor position.
/// </summary>
public sealed class MetaballSurface : SKElement
{
    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly MetaballFrame _frame = new();
    private readonly SKPaint _blitPaint = new();
    private MetaballScene? _scene;
    private ThemeColors _colors = ThemeColors.Default;
    private MetaballQuality _quality = MetaballQuality.High;
    private IMetaballRenderer? _renderer;
    private BlueprintGridPainter? _grid;
    private SKSurface? _layer;
    private int _layerWidth;
    private int _layerHeight;

    /// <summary>Initializes a new instance of the <see cref="MetaballSurface"/> class.</summary>
    public MetaballSurface()
    {
        IgnorePixelScaling = true;
        IsHitTestVisible = false;
        Focusable = false;
        Unloaded += (_, _) => ReleaseResources();
    }

    /// <summary>Gets the name of the active backend, or <see langword="null"/> before the first paint.</summary>
    public string? BackendName => _renderer switch
    {
        SdfMetaballRenderer => "SDF",
        BlurThresholdMetaballRenderer => "BlurThreshold",
        _ => null,
    };

    internal void Configure(MetaballScene scene, ThemeColors colors, MetaballQuality quality)
    {
        _scene = scene;
        _colors = colors;
        _quality = quality;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        int width = e.Info.Width;
        int height = e.Info.Height;
        if (width <= 0 || height <= 0 || ActualWidth <= 0)
        {
            return;
        }

        float pixelsPerDip = (float)(width / ActualWidth);
        _grid ??= new BlueprintGridPainter();
        _grid.DrawBase(canvas, width, height, pixelsPerDip, _colors);

        MetaballScene? scene = _scene;
        if (scene is null || _quality == MetaballQuality.Off)
        {
            return;
        }

        if (scene.CursorStrength > 0.01f)
        {
            _grid.DrawSpotlight(canvas, scene.CursorX * pixelsPerDip, scene.CursorY * pixelsPerDip, pixelsPerDip, scene.CursorStrength, _colors);
        }

        if (scene.Count > 0 || scene.CursorStrength > 0.01f)
        {
            DrawMask(canvas, scene, width, height, pixelsPerDip);
        }
    }

    private void DrawMask(SKCanvas canvas, MetaballScene scene, int width, int height, float pixelsPerDip)
    {
        float resolution = MetaballQualityResolver.ResolutionScale(_quality);
        int layerWidth = Math.Max(1, (int)MathF.Ceiling(width * resolution));
        int layerHeight = Math.Max(1, (int)MathF.Ceiling(height * resolution));
        SKSurface layer = EnsureLayer(layerWidth, layerHeight);

        _frame.Fill(scene, pixelsPerDip * resolution, _colors);
        layer.Canvas.Clear(SKColors.Transparent);

        _renderer ??= MetaballRenderers.Create();
        if (!_renderer.Render(layer.Canvas, _frame, layerWidth, layerHeight))
        {
            _renderer.Dispose();
            _renderer = new BlurThresholdMetaballRenderer();
            layer.Canvas.Clear(SKColors.Transparent);
            _renderer.Render(layer.Canvas, _frame, layerWidth, layerHeight);
        }

        canvas.Save();
        canvas.Scale(1f / resolution);
        layer.Draw(canvas, 0f, 0f, Smooth, _blitPaint);
        canvas.Restore();
    }

    private SKSurface EnsureLayer(int width, int height)
    {
        if (_layer is not null && _layerWidth == width && _layerHeight == height)
        {
            return _layer;
        }

        _layer?.Dispose();
        _layerWidth = width;
        _layerHeight = height;
        _layer = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        return _layer;
    }

    private void ReleaseResources()
    {
        _renderer?.Dispose();
        _renderer = null;
        _grid?.Dispose();
        _grid = null;
        _layer?.Dispose();
        _layer = null;
    }
}
