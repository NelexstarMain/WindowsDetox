using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>
/// The 24-hour body of a <see cref="UiTimeGrid"/>: draws the hour ruler and day separators itself, hosts one lightweight element per
/// block and runs the pointer gestures (create, move, resize, click). The blocks never take hit-testing: all pointer logic is geometric
/// and lives in <see cref="TimeGridGesture"/>, so dragging allocates nothing per mouse move.
/// </summary>
internal sealed class TimeGridSurface : Panel
{
    private static readonly TimeSpan RevertDelay = TimeSpan.FromMilliseconds(500);
    private static readonly Effect RaisedShadow = CreateShadow();

    private readonly UiTimeGrid _grid;
    private readonly Action<UiAction> _raise;
    private readonly ColumnSlot[] _slots;
    private readonly BlockVisual[] _blocks;
    private readonly RectD[] _rects;
    private readonly GhostVisual _pendingGhost = new();
    private readonly GhostVisual _dragGhost = new();
    private readonly NowLineVisual _nowLine = new();
    private readonly int _todayColumn;
    private readonly TimeSlot? _pendingSlot;

    private TimeGridGeometry _geometry;
    private TimeGridGesture? _gesture;
    private int _gestureBlock = -1;
    private int _previewBlock = -1;
    private RectD _previewRect;
    private RectD _dragGhostRect;
    private bool _dragGhostVisible;
    private DispatcherTimer? _revertTimer;
    private FormattedText[]? _hourLabels;

    /// <summary>Initializes a new instance of the <see cref="TimeGridSurface"/> class.</summary>
    /// <param name="grid">The node to draw.</param>
    /// <param name="raise">Receives the actions for the plugin.</param>
    public TimeGridSurface(UiTimeGrid grid, Action<UiAction> raise)
    {
        _grid = grid;
        _raise = raise;
        _slots = TimeGridMath.PackColumns(grid.Events);
        _blocks = new BlockVisual[grid.Events.Count];
        _rects = new RectD[grid.Events.Count];
        _todayColumn = grid.Days.ToList().FindIndex(d => d.IsToday);
        if (grid is { PendingDay: { } day, PendingStartMinute: { } start, PendingEndMinute: { } end } && day < grid.Days.Count && start < end)
        {
            _pendingSlot = new TimeSlot(day, start, end);
        }

        Focusable = true;
        FocusVisualStyle = null;
        AutomationProperties.SetName(this, "Schedule");

        for (int i = 0; i < _blocks.Length; i++)
        {
            _blocks[i] = new BlockVisual(grid.Events[i], RaisedShadow);
            Children.Add(_blocks[i]);
        }

        SetZIndex(_nowLine, 20);
        SetZIndex(_pendingGhost, 30);
        SetZIndex(_dragGhost, 30);
        Children.Add(_nowLine);
        Children.Add(_pendingGhost);
        Children.Add(_dragGhost);
        _pendingGhost.Visibility = _pendingSlot is null ? Visibility.Collapsed : Visibility.Visible;
        if (_pendingSlot is { } pending)
        {
            _pendingGhost.SetTime(TimeGridMath.FormatRange(pending.Start, pending.End));
        }

        _dragGhost.Visibility = Visibility.Collapsed;
        _nowLine.Visibility = _todayColumn >= 0 && grid.NowMinute is not null ? Visibility.Visible : Visibility.Collapsed;
        Unloaded += (_, _) => _revertTimer?.Stop();
    }

    /// <summary>Gets whether the surface has been laid out (anchors can be resolved).</summary>
    public bool IsLaidOut => _geometry.ColumnsWidth > 0;

    /// <summary>Finds the rectangle of a popover anchor in surface coordinates.</summary>
    /// <param name="anchorId">A block id, or the grid id for the pending new block.</param>
    /// <returns>The rectangle, or <see langword="null"/> when no such block exists.</returns>
    public RectD? AnchorRect(string anchorId)
    {
        if (anchorId == _grid.Id)
        {
            return _pendingSlot is { } pending ? TimeGridMath.BlockRect(_geometry, pending, 0, 1) : null;
        }

        for (int i = 0; i < _rects.Length; i++)
        {
            if (_grid.Events[i].Id == anchorId)
            {
                return _rects[i];
            }
        }

        return null;
    }

    /// <summary>Cancels a running drag (Escape) and restores the layout.</summary>
    /// <returns><see langword="true"/> when a gesture was running.</returns>
    public bool CancelDrag()
    {
        if (_gesture is null)
        {
            return false;
        }

        _gesture = null;
        ReleaseMouseCapture();
        ResetPreview();
        return true;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 700;
        UpdateGeometry(width);
        for (int i = 0; i < _blocks.Length; i++)
        {
            RectD rect = RectFor(i);
            _blocks[i].Prepare(rect.Height);
            _blocks[i].Measure(new Size(rect.Width, rect.Height));
        }

        MeasureGhost(_pendingGhost, _pendingSlot is { } pending ? TimeGridMath.BlockRect(_geometry, pending, 0, 1) : default);
        MeasureGhost(_dragGhost, _dragGhostRect);
        _nowLine.Measure(new Size(_geometry.ColumnWidth, NowLineVisual.BandHeight));
        return new Size(width, TimeGridMath.ContentHeight);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateGeometry(finalSize.Width);
        for (int i = 0; i < _blocks.Length; i++)
        {
            _blocks[i].Arrange(ToRect(RectFor(i)));
        }

        if (_pendingSlot is { } pending)
        {
            _pendingGhost.Arrange(ToRect(TimeGridMath.BlockRect(_geometry, pending, 0, 1)));
        }

        _dragGhost.Arrange(ToRect(_dragGhostRect));
        if (_todayColumn >= 0 && _grid.NowMinute is { } now)
        {
            double y = _geometry.YOf(now) - (NowLineVisual.BandHeight / 2);
            _nowLine.Arrange(new Rect(_geometry.ColumnX(_todayColumn), y, _geometry.ColumnWidth, NowLineVisual.BandHeight));
        }

        return finalSize;
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (_geometry.DayCount == 0 || width <= _geometry.ColumnsLeft)
        {
            return;
        }

        Brush lineBrush = Resource("Blocker.Border.Strong", Brushes.DimGray);
        var linePen = new Pen(lineBrush, 1);
        if (_todayColumn >= 0)
        {
            drawingContext.PushOpacity(0.35);
            drawingContext.DrawRectangle(Resource("Blocker.Accent.Soft", Brushes.DarkOliveGreen), null, new Rect(_geometry.ColumnX(_todayColumn), 0, _geometry.ColumnWidth, height));
            drawingContext.Pop();
        }

        drawingContext.PushOpacity(0.2);
        for (int hour = 0; hour < 24; hour++)
        {
            double half = Math.Round(_geometry.YOf((hour * 60) + 30)) + 0.5;
            drawingContext.DrawLine(linePen, new Point(_geometry.ColumnsLeft, half), new Point(width, half));
        }

        drawingContext.Pop();

        drawingContext.PushOpacity(0.55);
        for (int hour = 1; hour < 24; hour++)
        {
            double y = Math.Round(_geometry.YOf(hour * 60)) + 0.5;
            drawingContext.DrawLine(linePen, new Point(_geometry.ColumnsLeft - 6, y), new Point(width, y));
        }

        for (int day = 0; day < _geometry.DayCount; day++)
        {
            double x = Math.Round(_geometry.ColumnX(day)) + 0.5;
            drawingContext.DrawLine(linePen, new Point(x, 0), new Point(x, height));
        }

        drawingContext.Pop();

        FormattedText[] labels = _hourLabels ??= BuildHourLabels();
        for (int hour = 0; hour < labels.Length; hour++)
        {
            FormattedText label = labels[hour];
            double y = hour == 0 ? 2 : _geometry.YOf(hour * 60) - (label.Height / 2);
            drawingContext.DrawText(label, new Point(_geometry.ColumnsLeft - 10 - label.Width, y));
        }
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _hourLabels = null;
        base.OnDpiChanged(oldDpi, newDpi);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_grid is { Popover: not null, DismissId: { } dismissId })
        {
            _raise(UiAction.Click(dismissId));
            e.Handled = true;
            return;
        }

        Point point = e.GetPosition(this);
        if (point.X < _geometry.ColumnsLeft || _geometry.DayCount == 0)
        {
            return;
        }

        _revertTimer?.Stop();
        ResetPreview();
        int block = HitBlock(point, out bool onHandle);
        if (block >= 0)
        {
            UiTimeGridEvent ev = _grid.Events[block];
            TimeGridGestureKind kind = onHandle && ev.IsResizable ? TimeGridGestureKind.Resize
                : ev.IsMovable ? TimeGridGestureKind.Move
                : TimeGridGestureKind.Press;
            _gesture = new TimeGridGesture(kind, ev.Id, new TimeSlot(ev.Day, ev.StartMinute, ev.EndMinute), point.X, point.Y, _geometry);
            Cursor = kind == TimeGridGestureKind.Resize ? Cursors.SizeNS : null;
        }
        else if (_grid.AllowCreate)
        {
            _gesture = new TimeGridGesture(TimeGridGestureKind.Create, _grid.Id, new TimeSlot(_geometry.DayAt(point.X), 0, 0), point.X, point.Y, _geometry);
        }
        else
        {
            return;
        }

        _gestureBlock = block;
        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (_gesture is null)
        {
            Cursor = HitBlock(point, out bool onHandle) >= 0 && onHandle ? Cursors.SizeNS : null;
            return;
        }

        if (_gesture.Update(point.X, point.Y, _geometry))
        {
            ShowPreview(_gesture);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_gesture is not { } gesture)
        {
            return;
        }

        Point point = e.GetPosition(this);
        if (gesture.Update(point.X, point.Y, _geometry))
        {
            ShowPreview(gesture);
        }

        _gesture = null;
        ReleaseMouseCapture();
        UiAction? action = gesture.Complete((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0, _geometry);
        if (gesture.Moved && action is not null)
        {
            // Keep the dropped preview until the plugin re-renders; if it never does (the change was rejected) the layout reverts.
            StartRevertTimer();
        }
        else
        {
            ResetPreview();
        }

        e.Handled = true;
        if (action is not null)
        {
            _raise(action);
        }
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gesture is not null)
        {
            _gesture = null;
            ResetPreview();
        }
    }

    private static Effect CreateShadow()
    {
        var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 3, Opacity = 0.55 };
        shadow.Freeze();
        return shadow;
    }

    private static TextBlock CreateLabel(string text, double size, FontWeight weight, string brushKey)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(FrameworkElement.StyleProperty, "Blocker.Text.Caption");
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    private static Rect ToRect(RectD r) => new(r.X, r.Y, Math.Max(r.Width, 0), Math.Max(r.Height, 0));

    private static void MeasureGhost(GhostVisual ghost, RectD rect) => ghost.Measure(new Size(Math.Max(rect.Width, 0), Math.Max(rect.Height, 0)));

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void UpdateGeometry(double width)
    {
        _geometry = new TimeGridGeometry(
            TimeGridMath.GutterWidth,
            Math.Max(0, width - TimeGridMath.GutterWidth),
            _grid.Days.Count,
            TimeGridMath.HourHeight,
            _grid.SnapMinutes);
        for (int i = 0; i < _rects.Length; i++)
        {
            UiTimeGridEvent ev = _grid.Events[i];
            _rects[i] = TimeGridMath.BlockRect(_geometry, new TimeSlot(Math.Min(ev.Day, Math.Max(_geometry.DayCount - 1, 0)), ev.StartMinute, ev.EndMinute), _slots[i].Column, _slots[i].Columns);
        }
    }

    private RectD RectFor(int index) => index == _previewBlock ? _previewRect : _rects[index];

    private FormattedText[] BuildHourLabels()
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Brush brush = Resource("Blocker.Text.Muted", Brushes.Gray);
        var family = TryFindResource("Blocker.Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var labels = new FormattedText[24];
        for (int hour = 0; hour < labels.Length; hour++)
        {
            labels[hour] = new FormattedText(TimeGridMath.FormatTime(hour * 60), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, brush, dip);
        }

        return labels;
    }

    private int HitBlock(Point point, out bool onHandle)
    {
        onHandle = false;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = _blocks.Length - 1; i >= 0; i--)
            {
                if (_grid.Events[i].IsSelected != (pass == 0))
                {
                    continue;
                }

                RectD r = _rects[i];
                if (point.X >= r.X && point.X <= r.Right && point.Y >= r.Y && point.Y <= r.Bottom)
                {
                    onHandle = point.Y >= r.Bottom - TimeGridMath.ResizeHandleHeight && _grid.Events[i].IsResizable;
                    return i;
                }
            }
        }

        return -1;
    }

    private void ShowPreview(TimeGridGesture gesture)
    {
        TimeSlot slot = gesture.Current;
        string label = TimeGridMath.FormatRange(slot.Start, slot.End);
        RectD rect = TimeGridMath.BlockRect(_geometry, slot, 0, 1);
        if (gesture.Kind == TimeGridGestureKind.Create)
        {
            _dragGhostRect = rect;
            _dragGhostVisible = true;
            _dragGhost.SetTime(label);
            _dragGhost.Visibility = Visibility.Visible;
        }
        else if (_gestureBlock >= 0)
        {
            if (_previewBlock != _gestureBlock)
            {
                _previewBlock = _gestureBlock;
                SetZIndex(_blocks[_previewBlock], 40);
            }

            _previewRect = rect;
            _blocks[_previewBlock].SetPreviewTime(label);
        }

        InvalidateMeasure();
    }

    private void ResetPreview()
    {
        if (_previewBlock >= 0)
        {
            _blocks[_previewBlock].ClearPreview();
            _previewBlock = -1;
        }

        if (_dragGhostVisible)
        {
            _dragGhostVisible = false;
            _dragGhost.Visibility = Visibility.Collapsed;
        }

        Cursor = null;
        InvalidateMeasure();
    }

    private void StartRevertTimer()
    {
        if (_revertTimer is null)
        {
            _revertTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = RevertDelay };
            _revertTimer.Tick += (_, _) =>
            {
                _revertTimer.Stop();
                ResetPreview();
            };
        }

        _revertTimer.Stop();
        _revertTimer.Start();
    }

    /// <summary>One block: tone-coloured soft fill with a stronger bar on the left, a bold title and an optional detail line.</summary>
    private sealed class BlockVisual : Border
    {
        private readonly TextBlock _title;
        private readonly TextBlock _detail;
        private readonly Border _surface;
        private readonly string? _baseDetail;
        private readonly string _fullTitle;
        private readonly string _inlineTitle;
        private readonly bool _selected;
        private bool _detailShown;
        private bool _titleInline;

        public BlockVisual(UiTimeGridEvent ev, Effect raisedShadow)
        {
            _baseDetail = string.IsNullOrEmpty(ev.Detail) ? null : ev.Detail;
            _fullTitle = ev.Title;
            // A block too short for a second line carries its start inline: "Team standup, 11:00".
            _inlineTitle = _baseDetail is null ? ev.Title : ev.Title + ", " + _baseDetail.Split('–', ' ')[0];
            _selected = ev.IsSelected;
            IsHitTestVisible = false;
            CornerRadius = new CornerRadius(4);
            SetResourceReference(BackgroundProperty, "Blocker.Bg.Card");

            var bar = new Border { Width = 4, CornerRadius = new CornerRadius(3, 0, 0, 3) };
            bar.SetResourceReference(BackgroundProperty, ev.Tone == UiTone.Accent ? "Blocker.Accent.Bar" : ToneMap.FillKey(ev.Tone));
            DockPanel.SetDock(bar, Dock.Left);

            _title = CreateLabel(ev.Title, 12, FontWeights.SemiBold, "Blocker.Text.Primary");
            _detail = CreateLabel(_baseDetail ?? string.Empty, 11, FontWeights.Normal, "Blocker.Text.Secondary");
            _detailShown = _baseDetail is not null;
            _detail.Visibility = _detailShown ? Visibility.Visible : Visibility.Collapsed;

            var texts = new StackPanel { Margin = new Thickness(6, 3, 4, 3), VerticalAlignment = VerticalAlignment.Top };
            texts.Children.Add(_title);
            texts.Children.Add(_detail);

            _surface = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = new DockPanel { Children = { bar, texts } } };
            _surface.SetResourceReference(BackgroundProperty, ToneMap.SoftKey(ev.Tone));
            Child = _surface;

            if (ev.IsSelected)
            {
                _surface.SetResourceReference(BorderBrushProperty, "Blocker.Text.Primary");
                Effect = raisedShadow;
                SetZIndex(this, 10);
            }

            AutomationProperties.SetName(this, _baseDetail is null ? ev.Title : ev.Title + ", " + _baseDetail);
        }

        /// <summary>Shows or hides the detail line depending on the height the block gets.</summary>
        /// <param name="height">The block height.</param>
        public void Prepare(double height)
        {
            bool show = TimeGridMath.ShowsDetail(height) && (_baseDetail is not null || _detail.Text.Length > 0);
            if (show != _detailShown)
            {
                _detailShown = show;
                _detail.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            bool inline = !show && _baseDetail is not null;
            if (inline == _titleInline)
            {
                return;
            }

            _titleInline = inline;
            if (!inline)
            {
                _title.Inlines.Clear();
                _title.Text = _fullTitle;
                _title.FontSize = 12;
            }
            else
            {
                // Smaller so the start still fits next to the title in a narrow week column.
                _title.FontSize = 11;
                _title.Inlines.Clear();
                _title.Inlines.Add(new Run(_fullTitle));
                _title.Inlines.Add(new Run(_inlineTitle[_fullTitle.Length..]) { FontWeight = FontWeights.Normal, FontSize = 10 });
            }
        }

        /// <summary>Replaces the detail with the live time range while the block is dragged.</summary>
        /// <param name="label">The range text.</param>
        public void SetPreviewTime(string label)
        {
            if (_detail.Text != label)
            {
                _detail.Text = label;
            }
        }

        /// <summary>Restores the detail the plugin supplied and the normal stacking.</summary>
        public void ClearPreview()
        {
            _detail.Text = _baseDetail ?? string.Empty;
            SetZIndex(this, _selected ? 10 : 0);
        }
    }

    /// <summary>A dashed accent block: the pending new block, or the block being drawn by a drag.</summary>
    private sealed class GhostVisual : Grid
    {
        private readonly TextBlock _time;

        public GhostVisual()
        {
            IsHitTestVisible = false;
            var outline = new Rectangle { RadiusX = 4, RadiusY = 4, StrokeThickness = 1.5, StrokeDashArray = [4, 3], Margin = new Thickness(0.75) };
            outline.SetResourceReference(Shape.FillProperty, "Blocker.Accent.Soft");
            outline.SetResourceReference(Shape.StrokeProperty, "Blocker.Accent.Bar");
            Children.Add(outline);

            var texts = new StackPanel { Margin = new Thickness(8, 4, 6, 4), VerticalAlignment = VerticalAlignment.Top };
            texts.Children.Add(CreateLabel("(new)", 12, FontWeights.SemiBold, "Blocker.Text.Primary"));
            _time = CreateLabel(string.Empty, 11, FontWeights.Normal, "Blocker.Text.Secondary");
            texts.Children.Add(_time);
            Children.Add(texts);
        }

        public void SetTime(string label)
        {
            if (_time.Text != label)
            {
                _time.Text = label;
            }
        }
    }

    /// <summary>The current-time line across today's column, drawn above the blocks.</summary>
    private sealed class NowLineVisual : FrameworkElement
    {
        public const double BandHeight = 12;

        public NowLineVisual() => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext drawingContext)
        {
            Brush brush = TryFindResource("Blocker.Danger") as Brush ?? Brushes.IndianRed;
            double y = BandHeight / 2;
            drawingContext.DrawRectangle(brush, null, new Rect(0, y - 1, ActualWidth, 2));
            drawingContext.DrawEllipse(brush, null, new Point(5, y), 5, 5);
        }
    }
}
