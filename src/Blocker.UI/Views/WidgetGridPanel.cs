using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>
/// Lays children out on a grid like an Android home screen: the column count follows the width, rows have a fixed height, each child
/// spans <see cref="ColumnSpanProperty"/> by <see cref="RowSpanProperty"/> cells and is placed at the first free spot (dense, in order).
/// Children slide to their new place (about 180 ms, ease-out) whenever the layout changes, and one child can be lifted and dragged
/// around while the rest reflow around its virtual position; the owner commits the order on drop.
/// </summary>
internal sealed class WidgetGridPanel : Panel
{
    /// <summary>Identifies the <c>ColumnSpan</c> attached property.</summary>
    public static readonly DependencyProperty ColumnSpanProperty = DependencyProperty.RegisterAttached(
        "ColumnSpan", typeof(int), typeof(WidgetGridPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>Identifies the <c>RowSpan</c> attached property.</summary>
    public static readonly DependencyProperty RowSpanProperty = DependencyProperty.RegisterAttached(
        "RowSpan", typeof(int), typeof(WidgetGridPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    private const double LiftScale = 1.03;
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan LiftTime = TimeSpan.FromMilliseconds(120);

    private readonly Dictionary<UIElement, Rect> _rects = [];
    private List<UIElement> _arranged = [];
    private GridSlot[] _slots = [];
    private List<UIElement>? _order;
    private UIElement? _dragged;
    private Vector _grab;
    private Point _dragTopLeft;
    private double _width;
    private bool _widthChanged;

    /// <summary>Gets the number of columns of the last layout.</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>Gets the width of one column of the last layout.</summary>
    public double CellWidth { get; private set; }

    /// <summary>Gets the child that is being dragged, if any.</summary>
    public UIElement? DraggedChild => _dragged;

    /// <summary>Gets the position the dragged child would take if dropped now, or -1 when nothing is dragged.</summary>
    public int DragIndex => _order is null || _dragged is null ? -1 : _order.IndexOf(_dragged);

    /// <summary>Gets the columns a child spans.</summary>
    /// <param name="element">The child.</param>
    /// <returns>The column span.</returns>
    public static int GetColumnSpan(UIElement element) => (int)element.GetValue(ColumnSpanProperty);

    /// <summary>Sets the columns a child spans.</summary>
    /// <param name="element">The child.</param>
    /// <param name="value">The column span.</param>
    public static void SetColumnSpan(UIElement element, int value) => element.SetValue(ColumnSpanProperty, value);

    /// <summary>Gets the rows a child spans.</summary>
    /// <param name="element">The child.</param>
    /// <returns>The row span.</returns>
    public static int GetRowSpan(UIElement element) => (int)element.GetValue(RowSpanProperty);

    /// <summary>Sets the rows a child spans.</summary>
    /// <param name="element">The child.</param>
    /// <param name="value">The row span.</param>
    public static void SetRowSpan(UIElement element, int value) => element.SetValue(RowSpanProperty, value);

    /// <summary>Gets the layout slot of a child, without any slide or drag offset.</summary>
    /// <param name="child">The child.</param>
    /// <returns>The slot in the panel's coordinates, or <see langword="null"/> when the child has not been laid out.</returns>
    public Rect? SlotOf(UIElement child) => _rects.TryGetValue(child, out Rect rect) ? rect : null;

    /// <summary>Gets the child whose slot contains a point (in the current, possibly virtual, order).</summary>
    /// <param name="point">Point in the panel's coordinates.</param>
    /// <returns>The child, or <see langword="null"/> for the gaps and empty space.</returns>
    public UIElement? ChildAt(Point point)
    {
        for (int i = 0; i < _arranged.Count; i++)
        {
            if (SlotRect(_slots[i]).Contains(point))
            {
                return _arranged[i];
            }
        }

        return null;
    }

    /// <summary>Gets the position of a child in the current, possibly virtual, order.</summary>
    /// <param name="child">The child.</param>
    /// <returns>The index, or -1.</returns>
    public int IndexOfChild(UIElement child) => _arranged.IndexOf(child);

    /// <summary>Lifts a child (slightly larger, with a shadow) so it can follow the pointer.</summary>
    /// <param name="child">The child.</param>
    /// <param name="pointer">Pointer position in the panel's coordinates.</param>
    /// <returns><see langword="true"/> when the drag started.</returns>
    public bool BeginDrag(UIElement child, Point pointer)
    {
        if (_dragged is not null || !_rects.TryGetValue(child, out Rect slot))
        {
            return false;
        }

        _dragged = child;
        _order = [.. _arranged];
        _grab = pointer - slot.TopLeft;
        _dragTopLeft = slot.TopLeft;

        TranslateTransform translate = TranslateOf(child);
        (double x, double y) = (translate.X, translate.Y);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        (translate.X, translate.Y) = (x, y);

        SetZIndex(child, 100);
        child.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        ScaleTransform scale = ScaleOf(child);
        Tween(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, LiftScale, LiftTime, hold: true);
        Tween(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, LiftScale, LiftTime, hold: true);
        return true;
    }

    /// <summary>Moves the dragged child with the pointer.</summary>
    /// <param name="pointer">Pointer position in the panel's coordinates.</param>
    public void MoveDrag(Point pointer)
    {
        if (_dragged is null)
        {
            return;
        }

        _dragTopLeft = pointer - _grab;
        if (_rects.TryGetValue(_dragged, out Rect slot))
        {
            ApplyDragOffset(TranslateOf(_dragged), slot);
        }
    }

    /// <summary>Moves the dragged child to another position of the virtual order; the other children slide out of the way.</summary>
    /// <param name="index">The new position.</param>
    /// <returns><see langword="true"/> when the order changed.</returns>
    public bool MoveDragTo(int index)
    {
        if (_order is null || _dragged is null)
        {
            return false;
        }

        int from = _order.IndexOf(_dragged);
        index = Math.Clamp(index, 0, _order.Count - 1);
        if (from < 0 || from == index)
        {
            return false;
        }

        _order.RemoveAt(from);
        _order.Insert(index, _dragged);
        InvalidateMeasure();
        return true;
    }

    /// <summary>Drops the dragged child; the child settles into its slot.</summary>
    /// <returns>The position it was dropped at, or -1 when nothing was dragged.</returns>
    public int EndDrag()
    {
        int index = DragIndex;
        ReleaseDrag();
        return index;
    }

    /// <summary>Ends the drag without keeping the virtual order: every child slides back to where it was.</summary>
    public void CancelDrag() => ReleaseDrag();

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? WidgetSpans.SlotWidth(2) : availableSize.Width;
        _widthChanged = Math.Abs(width - _width) > 0.5;
        _width = width;
        Columns = WidgetGridLayout.ColumnCount(width);
        CellWidth = WidgetGridLayout.CellWidth(width, Columns);

        List<UIElement> natural = [.. InternalChildren.Cast<UIElement>()];
        if (_dragged is not null && !natural.Contains(_dragged))
        {
            _dragged = null;
            _order = null;
        }

        _arranged = _order is null ? natural : [.. _order.Where(natural.Contains), .. natural.Where(c => !_order.Contains(c))];
        _slots = WidgetGridLayout.Pack([.. _arranged.Select(c => (GetColumnSpan(c), GetRowSpan(c)))], Columns);
        for (int i = 0; i < _arranged.Count; i++)
        {
            Rect slot = SlotRect(_slots[i]);
            _arranged[i].Measure(slot.Size);
        }

        foreach (UIElement gone in _rects.Keys.Where(k => !natural.Contains(k)).ToList())
        {
            _rects.Remove(gone);
        }

        return new Size(width, WidgetGridLayout.Height(WidgetGridLayout.RowCount(_slots)));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < _arranged.Count; i++)
        {
            Rect slot = SlotRect(_slots[i]);
            _arranged[i].Arrange(slot);
            SlideTo(_arranged[i], slot);
        }

        _widthChanged = false;
        return finalSize;
    }

    private static TransformGroup GroupOf(UIElement child)
    {
        if (child.RenderTransform is TransformGroup { Children.Count: 2 } group && group.Children[0] is ScaleTransform && group.Children[1] is TranslateTransform)
        {
            return group;
        }

        TransformGroup created = new() { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
        child.RenderTransform = created;
        child.RenderTransformOrigin = new Point(0.5, 0.5);
        return created;
    }

    private static ScaleTransform ScaleOf(UIElement child) => (ScaleTransform)GroupOf(child).Children[0];

    private static TranslateTransform TranslateOf(UIElement child) => (TranslateTransform)GroupOf(child).Children[1];

    private static void Tween(Animatable target, DependencyProperty property, double from, double to, TimeSpan duration, bool hold)
    {
        DoubleAnimation animation = new(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = hold ? FillBehavior.HoldEnd : FillBehavior.Stop,
        };
        target.BeginAnimation(property, animation);
    }

    private Rect SlotRect(GridSlot slot) => new(
        slot.Column * (CellWidth + WidgetSpans.Gap),
        slot.Row * (WidgetSpans.CellHeight + WidgetSpans.Gap),
        (slot.ColumnSpan * CellWidth) + ((slot.ColumnSpan - 1) * WidgetSpans.Gap),
        WidgetSpans.SlotHeight(slot.RowSpan));

    private void ApplyDragOffset(TranslateTransform translate, Rect slot)
    {
        translate.X = _dragTopLeft.X - slot.X;
        translate.Y = _dragTopLeft.Y - slot.Y;
    }

    /// <summary>Records where a child was arranged and, when that moved it, slides it there from where it was.</summary>
    private void SlideTo(UIElement child, Rect slot)
    {
        TranslateTransform translate = TranslateOf(child);
        if (child == _dragged)
        {
            ApplyDragOffset(translate, slot);
        }
        else if (_widthChanged)
        {
            // The window is being resized: follow it directly instead of chasing the moving slots.
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            (translate.X, translate.Y) = (0, 0);
        }
        else if (_rects.TryGetValue(child, out Rect before))
        {
            // The child is drawn at slot + offset; start from where it appears now so a slide in progress is continued, not restarted.
            double dx = before.X - slot.X + translate.X;
            double dy = before.Y - slot.Y + translate.Y;
            if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5)
            {
                (translate.X, translate.Y) = (0, 0);
                Tween(translate, TranslateTransform.XProperty, dx, 0, SlideTime, hold: false);
                Tween(translate, TranslateTransform.YProperty, dy, 0, SlideTime, hold: false);
            }
        }

        _rects[child] = slot;
    }

    private void ReleaseDrag()
    {
        if (_dragged is not { } child)
        {
            return;
        }

        _dragged = null;
        _order = null;
        SetZIndex(child, 0);
        child.Effect = null;
        ScaleTransform scale = ScaleOf(child);
        (double x, double y) = (scale.ScaleX, scale.ScaleY);
        (scale.ScaleX, scale.ScaleY) = (1, 1);
        Tween(scale, ScaleTransform.ScaleXProperty, x, 1, LiftTime, hold: false);
        Tween(scale, ScaleTransform.ScaleYProperty, y, 1, LiftTime, hold: false);
        InvalidateMeasure();
    }
}
