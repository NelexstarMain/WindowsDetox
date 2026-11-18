using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Blocker.UI.Controls;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>
/// Lays children out on a grid like an Android home screen: the column count follows the width, rows have a fixed height, each child
/// spans <see cref="ColumnSpanProperty"/> by <see cref="RowSpanProperty"/> cells and is placed at the first free spot (dense, in order).
/// Children slide (and grow or shrink) to their new place with <see cref="Motion.Normal"/> ease-out whenever the order or a span changes,
/// continuing from where they are drawn if the target changes mid-flight; the first layout and a changing window width never animate.
/// One child can be lifted and dragged around while the rest reflow around its virtual position and settles into its slot on drop;
/// the owner commits the order on drop.
/// </summary>
internal sealed class WidgetGridPanel : Panel
{
    /// <summary>Identifies the <c>ColumnSpan</c> attached property.</summary>
    public static readonly DependencyProperty ColumnSpanProperty = DependencyProperty.RegisterAttached(
        "ColumnSpan", typeof(int), typeof(WidgetGridPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>Identifies the <c>RowSpan</c> attached property.</summary>
    public static readonly DependencyProperty RowSpanProperty = DependencyProperty.RegisterAttached(
        "RowSpan", typeof(int), typeof(WidgetGridPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    private const int LiftedZ = 100;
    private const int SettlingZ = 50;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Dictionary<UIElement, Rect> _rects = [];
    private readonly ReflowAnimator<UIElement> _reflow = new(Motion.Normal, Motion.EaseOut);
    private readonly HashSet<UIElement> _raised = [];
    private List<UIElement> _arranged = [];
    private GridSlot[] _slots = [];
    private List<UIElement>? _order;
    private UIElement? _dragged;
    private Vector _grab;
    private Point _dragTopLeft;
    private double _width;
    private bool _widthChanged;
    private bool _hasArranged;
    private bool _ticking;

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

        // Grab the child where it is drawn now, which differs from its slot while it is still sliding.
        Rect shown = ToRect(_reflow.Current(child, Clock.Elapsed));
        _dragged = child;
        _order = [.. _arranged];
        _grab = pointer - shown.TopLeft;
        _dragTopLeft = shown.TopLeft;
        ApplyDragOffset(TranslateOf(child), slot);

        _raised.Remove(child);
        SetZIndex(child, LiftedZ);
        DropShadowEffect shadow = new() { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        child.Effect = shadow;
        shadow.BeginAnimation(DropShadowEffect.OpacityProperty, Motion.Animate(0, shadow.Opacity, Motion.Fast));
        ScaleTransform scale = ScaleOf(child);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Animate(scale.ScaleX, Motion.LiftScale, Motion.Fast, hold: true));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Animate(scale.ScaleY, Motion.LiftScale, Motion.Fast, hold: true));
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
        _widthChanged |= Math.Abs(width - _width) > 0.5;
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
            _raised.Remove(gone);
        }

        _reflow.Prune(natural);

        return new Size(width, WidgetGridLayout.Height(WidgetGridLayout.RowCount(_slots)));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        TimeSpan now = Clock.Elapsed;
        bool animate = Motion.Enabled && !_widthChanged;
        for (int i = 0; i < _arranged.Count; i++)
        {
            UIElement child = _arranged[i];
            Rect slot = SlotRect(_slots[i]);
            bool isNew = !_reflow.Contains(child);
            if (child == _dragged)
            {
                // The pointer decides where it is drawn; it stays in its slot for layout and is offset by a transform.
                _reflow.Hold(child, new Box(_dragTopLeft.X, _dragTopLeft.Y, slot.Width, slot.Height), now);
                child.Arrange(slot);
                ApplyDragOffset(TranslateOf(child), slot);
            }
            else
            {
                child.Arrange(ToRect(_reflow.Retarget(child, ToBox(slot), now, animate)));
            }

            _rects[child] = slot;
            if (isNew && _hasArranged && animate)
            {
                child.BeginAnimation(OpacityProperty, Motion.Animate(0, 1, Motion.Normal));
            }
        }

        _hasArranged |= _arranged.Count > 0;
        _widthChanged = false;
        if (!_reflow.IsAnimating(now))
        {
            LowerSettled();
        }
        else if (!_ticking)
        {
            _ticking = true;
            CompositionTarget.Rendering += OnRendering;
        }

        return finalSize;
    }

    private static Box ToBox(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static Rect ToRect(Box box) => new(box.X, box.Y, box.Width, box.Height);

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

    /// <summary>Re-arranges every frame while something slides; when it has settled, draws the exact end state once more and lowers the dropped child.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_reflow.IsAnimating(Clock.Elapsed))
        {
            CompositionTarget.Rendering -= OnRendering;
            _ticking = false;
            LowerSettled();
        }

        InvalidateArrange();
    }

    private void LowerSettled()
    {
        foreach (UIElement child in _raised.Where(c => c != _dragged))
        {
            SetZIndex(child, 0);
        }

        _raised.RemoveWhere(c => c != _dragged);
    }

    private void ReleaseDrag()
    {
        if (_dragged is not { } child)
        {
            return;
        }

        _dragged = null;
        _order = null;

        // Stay above the others until the child has settled into its slot (the next arrange slides it from where the pointer left it).
        _raised.Add(child);
        SetZIndex(child, SettlingZ);
        TranslateTransform translate = TranslateOf(child);
        (translate.X, translate.Y) = (0, 0);

        ScaleTransform scale = ScaleOf(child);
        (double x, double y) = (scale.ScaleX, scale.ScaleY);
        (scale.ScaleX, scale.ScaleY) = (1, 1);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Animate(x, 1, Motion.Fast));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Animate(y, 1, Motion.Fast));

        if (child.Effect is DropShadowEffect shadow)
        {
            DoubleAnimation fade = Motion.Animate(shadow.Opacity, 0, Motion.Fast, leaving: true, hold: true);
            fade.Completed += (_, _) =>
            {
                if (child.Effect == shadow && _dragged != child)
                {
                    child.Effect = null;
                }
            };
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
        }

        InvalidateMeasure();
    }
}
