using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace Blocker.UI.Views;

/// <summary>A thin single-stroke navigation icon drawn on a 24×24 grid, stroked with the inherited foreground so item states apply.</summary>
internal sealed class LineIcon : IconElement
{
    /// <summary>Identifies the <see cref="Data"/> property.</summary>
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(LineIcon), new PropertyMetadata(null));

    /// <summary>Gets or sets the outline, in 24×24 grid coordinates.</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc />
    protected override UIElement InitializeChildren()
    {
        var path = new Path
        {
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = false,
        };
        path.SetBinding(Path.DataProperty, new Binding(nameof(Data)) { Source = this });
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        return new Canvas { Width = 24, Height = 24, Children = { path }, LayoutTransform = new ScaleTransform(0.78, 0.78) };
    }
}
