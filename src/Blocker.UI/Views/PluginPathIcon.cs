using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Blocker.UI.Sdk;
using Wpf.Ui.Controls;

namespace Blocker.UI.Views;

/// <summary>
/// A navigation icon drawn from plugin-supplied SVG path data, filled with the inherited foreground so item states apply. Smaller and
/// slightly translucent so a filled plugin shape does not outweigh the thin built-in line icons beside it.
/// </summary>
internal sealed class PluginPathIcon : IconElement
{
    private const double IconSize = 16;
    private const double IconOpacity = 0.85;
    private readonly Geometry _geometry;

    private PluginPathIcon(Geometry geometry) => _geometry = geometry;

    /// <summary>Creates the icon, or <see langword="null"/> when <paramref name="pathData"/> is missing or not a valid path.</summary>
    /// <param name="pathData">SVG path data.</param>
    /// <returns>The icon element, or <see langword="null"/>.</returns>
    public static PluginPathIcon? TryCreate(string? pathData)
    {
        if (!UiTreeValidator.IsValidPathData(pathData))
        {
            return null;
        }

        try
        {
            Geometry geometry = Geometry.Parse(pathData);
            geometry.Freeze();
            return new PluginPathIcon(geometry);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override UIElement InitializeChildren()
    {
        var path = new System.Windows.Shapes.Path { Data = _geometry, Stretch = Stretch.Uniform, Width = IconSize, Height = IconSize, Opacity = IconOpacity };
        path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        return path;
    }
}
