using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Blocker.UI.ViewModels;
using Blocker.UI.Views;

namespace Blocker.UI.Behaviors;

/// <summary>
/// Pointer handling of the dashboard widget board, attached to the <see cref="ItemsControl"/> that hosts a <see cref="WidgetGridPanel"/>:
/// a tap opens the widget's plugin; a long press (or any press in edit mode) lifts a widget so it can be dragged to a new place, with
/// the others reflowing live; the resize handle drags the widget's span in whole cells. Esc cancels a gesture. The board view model
/// decides everything that is saved; this class only translates pointer movement into its calls.
/// </summary>
internal static class WidgetBoardInteraction
{
    /// <summary>Identifies the <c>Board</c> attached property.</summary>
    public static readonly DependencyProperty BoardProperty = DependencyProperty.RegisterAttached(
        "Board", typeof(WidgetBoardViewModel), typeof(WidgetBoardInteraction), new PropertyMetadata(null, OnBoardChanged));

    /// <summary>Identifies the <c>IsResizeHandle</c> attached property.</summary>
    public static readonly DependencyProperty IsResizeHandleProperty = DependencyProperty.RegisterAttached(
        "IsResizeHandle", typeof(bool), typeof(WidgetBoardInteraction), new PropertyMetadata(false));

    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(Controller), typeof(WidgetBoardInteraction), new PropertyMetadata(null));

    /// <summary>Gets the board the element's widgets belong to.</summary>
    /// <param name="element">The items control.</param>
    /// <returns>The board, if set.</returns>
    public static WidgetBoardViewModel? GetBoard(DependencyObject element) => (WidgetBoardViewModel?)element.GetValue(BoardProperty);

    /// <summary>Sets the board the element's widgets belong to.</summary>
    /// <param name="element">The items control.</param>
    /// <param name="value">The board.</param>
    public static void SetBoard(DependencyObject element, WidgetBoardViewModel? value) => element.SetValue(BoardProperty, value);

    /// <summary>Gets whether the element is a widget's resize handle.</summary>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> for a resize handle.</returns>
    public static bool GetIsResizeHandle(DependencyObject element) => (bool)element.GetValue(IsResizeHandleProperty);

    /// <summary>Marks the element as a widget's resize handle.</summary>
    /// <param name="element">The element.</param>
    /// <param name="value">Whether it is a resize handle.</param>
    public static void SetIsResizeHandle(DependencyObject element, bool value) => element.SetValue(IsResizeHandleProperty, value);

    private static void OnBoardChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ItemsControl items)
        {
            return;
        }

        Controller controller = (Controller?)items.GetValue(ControllerProperty) ?? new Controller(items);
        items.SetValue(ControllerProperty, controller);
        controller.Board = (WidgetBoardViewModel?)e.NewValue;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            T? found = child as T ?? FindDescendant<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class Controller
    {
        private const double ScrollEdge = 40;
        private const double ScrollStep = 14;
        private const double SwapDistance = 24;
        private static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(450);

        private readonly ItemsControl _items;
        private readonly DispatcherTimer _longPress;
        private Mode _mode;
        private WidgetGridPanel? _panel;
        private UIElement? _container;
        private WidgetItemViewModel? _item;
        private Point _start;
        private Window? _window;
        private ScrollViewer? _scroll;

        // Reorder: pointer must come back into the dragged widget's own slot (or travel far enough) before it swaps again, so widgets
        // of different sizes cannot flip back and forth under a still pointer.
        private bool _armed;
        private Point _swapPoint;

        // Resize: spans are measured from where the widget started, so a widget that moves while it grows cannot oscillate.
        private Point _origin;
        private Vector _handleOffset;
        private int _startCols;
        private int _startRows;
        private bool _resized;

        public Controller(ItemsControl items)
        {
            _items = items;
            _longPress = new DispatcherTimer(DispatcherPriority.Input, items.Dispatcher) { Interval = LongPress };
            _longPress.Tick += OnLongPress;
            items.PreviewMouseLeftButtonDown += OnDown;
            items.PreviewMouseMove += OnMove;
            items.PreviewMouseLeftButtonUp += OnUp;
            items.LostMouseCapture += OnLostCapture;
        }

        private enum Mode
        {
            Idle,
            Pressed,
            Dragging,
            Resizing,
        }

        public WidgetBoardViewModel? Board { get; set; }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (_mode != Mode.Idle || Board is not { } board || FindDescendant<WidgetGridPanel>(_items) is not { } panel)
            {
                return;
            }

            bool handle = false;
            bool interactive = false;
            UIElement? container = null;
            WidgetItemViewModel? item = null;
            for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null && d != panel; d = ParentOf(d))
            {
                handle |= GetIsResizeHandle(d);
                interactive |= d is ButtonBase or TextBoxBase or Selector or ScrollBar or Thumb;
                if (ParentOf(d) == panel && d is FrameworkElement { DataContext: WidgetItemViewModel found } element)
                {
                    (container, item) = (element, found);
                }
            }

            // Controls inside a widget (toggles, the remove button) keep their clicks; the resize handle only exists in edit mode.
            if (container is null || item is null || (handle ? !board.IsEditing : interactive))
            {
                return;
            }

            (_panel, _container, _item) = (panel, container, item);
            _start = e.GetPosition(panel);
            if (handle)
            {
                BeginResize();
            }
            else
            {
                _mode = Mode.Pressed;
                if (!board.IsEditing)
                {
                    _longPress.Start();
                }
            }

            _items.CaptureMouse();
            e.Handled = true;
        }

        private void OnLongPress(object? sender, EventArgs e)
        {
            _longPress.Stop();
            if (_mode == Mode.Pressed && Board is { } board && _panel is not null)
            {
                board.IsEditing = true;
                BeginDrag(Mouse.GetPosition(_panel));
            }
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_mode == Mode.Idle || _panel is null)
            {
                return;
            }

            Point pointer = e.GetPosition(_panel);
            switch (_mode)
            {
                case Mode.Pressed when Math.Abs(pointer.X - _start.X) > SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(pointer.Y - _start.Y) > SystemParameters.MinimumVerticalDragDistance:
                    if (Board?.IsEditing == true)
                    {
                        BeginDrag(pointer);
                    }
                    else
                    {
                        Finish();
                    }

                    break;
                case Mode.Dragging:
                    Drag(pointer);
                    AutoScroll(e);
                    break;
                case Mode.Resizing:
                    Resize(pointer);
                    AutoScroll(e);
                    break;
            }
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_mode == Mode.Idle)
            {
                return;
            }

            (Mode mode, WidgetItemViewModel? item, WidgetBoardViewModel? board, WidgetGridPanel? panel, bool tap) =
                (_mode, _item, Board, _panel, _longPress.IsEnabled);
            int index = mode == Mode.Dragging ? panel!.EndDrag() : -1;
            Finish();
            switch (mode)
            {
                case Mode.Pressed when tap && item is not null:
                    board?.OpenCommand.Execute(item);
                    break;
                case Mode.Dragging when item is not null:
                    board?.Move(item, index);
                    break;
                case Mode.Resizing when _resized:
                    board?.Commit();
                    break;
            }

            e.Handled = true;
        }

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (_mode != Mode.Idle)
            {
                Cancel();
            }
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _mode is Mode.Dragging or Mode.Resizing)
            {
                Cancel();
                e.Handled = true;
            }
        }

        private void BeginDrag(Point pointer)
        {
            _longPress.Stop();
            if (_panel is null || _container is null || !_panel.BeginDrag(_container, pointer))
            {
                Finish();
                return;
            }

            _mode = Mode.Dragging;
            _armed = true;
            _swapPoint = pointer;
            ListenForEscape();
        }

        private void Drag(Point pointer)
        {
            if (_panel is not { } panel)
            {
                return;
            }

            panel.MoveDrag(pointer);
            UIElement? target = panel.ChildAt(pointer);
            if (target is null)
            {
                return;
            }

            if (target == panel.DraggedChild)
            {
                _armed = true;
            }
            else if ((_armed || (pointer - _swapPoint).Length > SwapDistance) && panel.MoveDragTo(panel.IndexOfChild(target)))
            {
                (_armed, _swapPoint) = (false, pointer);
            }
        }

        private void BeginResize()
        {
            if (_panel is null || _container is null || _item is null || _panel.SlotOf(_container) is not { } slot)
            {
                Finish();
                return;
            }

            _mode = Mode.Resizing;
            _origin = slot.TopLeft;
            _handleOffset = new Vector(slot.Right - _start.X, slot.Bottom - _start.Y);
            (_startCols, _startRows, _resized) = (_item.Cols, _item.Rows, false);
            ListenForEscape();
        }

        private void Resize(Point pointer)
        {
            if (_panel is not { } panel || _item is not { } item || Board is not { } board)
            {
                return;
            }

            (int cols, int rows) = WidgetGridLayout.SnapSpan(
                pointer.X + _handleOffset.X - _origin.X,
                pointer.Y + _handleOffset.Y - _origin.Y,
                panel.CellWidth,
                panel.Columns);
            _resized |= board.Resize(item, cols, rows, commit: false);
        }

        private void AutoScroll(MouseEventArgs e)
        {
            _scroll ??= FindAncestor<ScrollViewer>(_items);
            if (_scroll is null)
            {
                return;
            }

            double y = e.GetPosition(_scroll).Y;
            if (y < ScrollEdge)
            {
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - ScrollStep);
            }
            else if (y > _scroll.ActualHeight - ScrollEdge)
            {
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + ScrollStep);
            }
        }

        private static T? FindAncestor<T>(DependencyObject start)
            where T : DependencyObject
        {
            for (DependencyObject? d = ParentOf(start); d is not null; d = ParentOf(d))
            {
                if (d is T found)
                {
                    return found;
                }
            }

            return null;
        }

        private void ListenForEscape()
        {
            _window = Window.GetWindow(_items);
            if (_window is not null)
            {
                _window.PreviewKeyDown += OnKey;
            }
        }

        /// <summary>Abandons the gesture: a drag goes back to where it started, a resize to its original span.</summary>
        private void Cancel()
        {
            (Mode mode, WidgetItemViewModel? item, WidgetGridPanel? panel) = (_mode, _item, _panel);
            Finish();
            if (mode == Mode.Dragging)
            {
                panel?.CancelDrag();
            }
            else if (mode == Mode.Resizing && item is not null)
            {
                Board?.Resize(item, _startCols, _startRows, commit: false);
            }
        }

        private void Finish()
        {
            _longPress.Stop();
            if (_window is not null)
            {
                _window.PreviewKeyDown -= OnKey;
                _window = null;
            }

            _mode = Mode.Idle;
            _items.ReleaseMouseCapture();
            (_panel, _container, _item) = (null, null, null);
        }
    }
}
