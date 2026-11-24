using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Blocker.UI.Sdk;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Blocker.UI.Controls.Rendering;

/// <summary>Host rendering of a <see cref="UiScene"/>: the drawn world plus, when allowed, two buttons that turn the camera.</summary>
internal sealed class SceneElement : Grid
{
    /// <summary>Initializes a new instance of the <see cref="SceneElement"/> class.</summary>
    /// <param name="scene">The node to render.</param>
    /// <param name="raise">Receives the actions for the plugin.</param>
    /// <param name="turns">Camera turn per scene id, kept across re-renders.</param>
    public SceneElement(UiScene scene, Action<UiAction> raise, Dictionary<string, int> turns)
    {
        Height = scene.Height;
        var surface = new SceneSurface(scene, raise, turns.GetValueOrDefault(scene.Id));
        AutomationProperties.SetName(surface, scene.Label ?? string.Empty);
        AutomationProperties.SetAutomationId(surface, scene.Id);
        Children.Add(surface);
        if (!scene.AllowRotate)
        {
            return;
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        buttons.Children.Add(TurnButton(SymbolRegular.ArrowRotateCounterclockwise24, "Turn view left", 1));
        buttons.Children.Add(TurnButton(SymbolRegular.ArrowRotateClockwise24, "Turn view right", 3));
        Children.Add(buttons);

        Button TurnButton(SymbolRegular symbol, string name, int delta)
        {
            var button = new Button
            {
                Width = 32,
                Height = 32,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                Content = new SymbolIcon { Symbol = symbol, FontSize = 16 },
                ToolTip = name,
            };
            button.SetResourceReference(StyleProperty, "Blocker.Button.Subtle");
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) =>
            {
                int turn = (surface.Turns + delta) & 3;
                turns[scene.Id] = turn;
                surface.Turns = turn;
            };
            return button;
        }
    }

    /// <summary>Draws the projected polygons and turns mouse input into item clicks and tile picks.</summary>
    private sealed class SceneSurface : FrameworkElement
    {
        private const double Inset = 8;

        private readonly UiScene _scene;
        private readonly Action<UiAction> _raise;
        private readonly Dictionary<Color, Brush> _brushes = [];
        private static readonly Brush ShadowBrush = CreateShadowBrush();

        private readonly Dictionary<Color, Pen> _seams = [];
        private readonly Dictionary<Color, Brush> _gradients = [];
        private readonly Dictionary<string, UiSceneItem> _itemsById;
        private readonly HashSet<(int, int)> _occupied;
        private SceneProjection _projection;
        private int _turns;
        private double _scale = 1;
        private Vector _offset;
        private (int Column, int Row)? _hoverTile;
        private string? _hoverItem;

        public SceneSurface(UiScene scene, Action<UiAction> raise, int turns)
        {
            _scene = scene;
            _raise = raise;
            _turns = turns & 3;
            _itemsById = scene.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
            _occupied = [.. scene.Items.Select(i => (i.Column, i.Row))];
            _projection = SceneMath.Project(scene, _turns);
            Focusable = false;
        }

        private static Brush CreateShadowBrush()
        {
            var brush = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x70, 0x10, 0x18, 0x08), 0),
                    new(Color.FromArgb(0x38, 0x10, 0x18, 0x08), 0.55),
                    new(Color.FromArgb(0x00, 0x10, 0x18, 0x08), 1),
                });
            brush.Freeze();
            return brush;
        }

        public int Turns
        {
            get => _turns;
            set
            {
                _turns = value & 3;
                _projection = SceneMath.Project(_scene, _turns);
                _hoverTile = null;
                _hoverItem = null;
                InvalidateVisual();
            }
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? _scene.Height : availableSize.Width, _scene.Height);

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            Rect bounds = _projection.Bounds;
            double width = Math.Max(1, ActualWidth - (2 * Inset));
            double height = Math.Max(1, ActualHeight - (2 * Inset));
            _scale = Math.Min(width / Math.Max(bounds.Width, 1e-6), height / Math.Max(bounds.Height, 1e-6));
            _offset = new Vector(
                (ActualWidth / 2) - ((bounds.Left + (bounds.Width / 2)) * _scale),
                (ActualHeight / 2) - ((bounds.Top + (bounds.Height / 2)) * _scale));

            bool marksDrawn = false;
            foreach (ScenePolygon polygon in _projection.Polygons)
            {
                if (!marksDrawn && polygon.ItemId is not null)
                {
                    DrawShadowsAndMarks(dc);
                    marksDrawn = true;
                }

                Color fill = _hoverItem is not null && polygon.ItemId == _hoverItem ? Lighten(polygon.Fill) : polygon.Fill;
                // Item faces get a soft top-to-bottom gradient for a painted look; the thin same-colour stroke only closes anti-aliasing seams.
                dc.DrawGeometry(polygon.ItemId is null ? Brush(fill) : Gradient(fill), Seam(fill), Geometry(polygon.Points));
            }

            if (!marksDrawn)
            {
                DrawShadowsAndMarks(dc);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string? item = ItemAt(e.GetPosition(this));
            (int, int)? tile = item is null && _scene.AllowPick ? FreeTileAt(e.GetPosition(this)) : null;
            if (item != _hoverItem || tile != _hoverTile)
            {
                _hoverItem = item;
                _hoverTile = tile;
                Cursor = item is not null || tile is not null ? Cursors.Hand : null;
                ToolTip = item is not null ? _itemsById[item].Label : null;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverItem = null;
            _hoverTile = null;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Point position = e.GetPosition(this);
            if (ItemAt(position) is { } item)
            {
                _raise(UiAction.Click(item));
            }
            else if (_scene.AllowPick && FreeTileAt(position) is { } tile)
            {
                _raise(new UiAction(_scene.Id, UiActionKind.Pick, null, new Dictionary<string, string>
                {
                    ["column"] = tile.Column.ToString(CultureInfo.InvariantCulture),
                    ["row"] = tile.Row.ToString(CultureInfo.InvariantCulture),
                }));
            }
        }

        private void DrawShadowsAndMarks(DrawingContext dc)
        {
            if (!_scene.ShowGround)
            {
                return;
            }

            foreach (SceneShadow shadow in _projection.Shadows)
            {
                dc.DrawEllipse(ShadowBrush, null, Transform(shadow.Centre), shadow.RadiusX * _scale, shadow.RadiusY * _scale);
            }

            var selected = new Pen(Brushes.White, 2);
            selected.Freeze();
            foreach (UiSceneItem item in _scene.Items.Where(i => i.IsSelected))
            {
                dc.DrawGeometry(Brush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), selected, Tile(item.Column, item.Row));
            }

            if (_hoverTile is { } hover)
            {
                dc.DrawGeometry(Brush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), null, Tile(hover.Column, hover.Row));
            }
        }

        private Geometry Tile(int column, int row) => Geometry(SceneMath.TileOutline(_scene.Columns, _scene.Rows, _turns, column, row));

        private string? ItemAt(Point position)
        {
            // Topmost first: the polygons are in drawing order.
            for (int i = _projection.Polygons.Count - 1; i >= 0; i--)
            {
                ScenePolygon polygon = _projection.Polygons[i];
                if (polygon.ItemId is not null && Geometry(polygon.Points).FillContains(position))
                {
                    return polygon.ItemId;
                }
            }

            return null;
        }

        private (int Column, int Row)? FreeTileAt(Point position)
        {
            Point world = new((position.X - _offset.X) / _scale, (position.Y - _offset.Y) / _scale);
            return SceneMath.TileAt(_scene.Columns, _scene.Rows, _turns, world) is { } tile && !_occupied.Contains(tile) ? tile : null;
        }

        private StreamGeometry Geometry(Point[] points)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(Transform(points[0]), isFilled: true, isClosed: true);
                for (int i = 1; i < points.Length; i++)
                {
                    ctx.LineTo(Transform(points[i]), isStroked: true, isSmoothJoin: true);
                }
            }

            geometry.Freeze();
            return geometry;
        }

        private Point Transform(Point p) => new((p.X * _scale) + _offset.X, (p.Y * _scale) + _offset.Y);

        private static Color Lighten(Color c) =>
            Color.FromRgb((byte)Math.Min(255, c.R + 28), (byte)Math.Min(255, c.G + 28), (byte)Math.Min(255, c.B + 28));

        private Pen Seam(Color fill)
        {
            if (!_seams.TryGetValue(fill, out Pen? pen))
            {
                pen = new Pen(Brush(fill), 0.5) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                _seams[fill] = pen;
            }

            return pen;
        }

        private Brush Gradient(Color fill)
        {
            if (!_gradients.TryGetValue(fill, out Brush? brush))
            {
                Color top = Color.FromRgb((byte)Math.Min(255, fill.R * 1.07), (byte)Math.Min(255, fill.G * 1.07), (byte)Math.Min(255, fill.B * 1.07));
                Color bottom = Color.FromRgb((byte)(fill.R * 0.9), (byte)(fill.G * 0.9), (byte)(fill.B * 0.9));
                brush = new LinearGradientBrush(top, bottom, 90);
                brush.Freeze();
                _gradients[fill] = brush;
            }

            return brush;
        }

        private Brush Brush(Color colour)
        {
            if (!_brushes.TryGetValue(colour, out Brush? brush))
            {
                brush = new SolidColorBrush(colour);
                brush.Freeze();
                _brushes[colour] = brush;
            }

            return brush;
        }
    }
}
