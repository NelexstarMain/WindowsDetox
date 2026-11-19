using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Blocker.UI.Behaviors;

/// <summary>
/// A ScrollViewer marks every wheel event handled, even when it has nothing to scroll or is already at the end, so an outer
/// scroller never moves. This hands such events to the nearest ancestor ScrollViewer instead.
/// </summary>
internal static class WheelPassThrough
{
    /// <summary>Registers the class handler once for the process.</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Preview tunnels from the outside in: act only on the innermost scroller under the pointer.
        if (e.Handled || sender is not ScrollViewer viewer || e.OriginalSource is not DependencyObject source || Nearest(source) != viewer)
        {
            return;
        }

        bool canMove = e.Delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;
        if (canMove || FindParentScroller(viewer) is not { } parent)
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = viewer });
    }

    private static ScrollViewer? Nearest(DependencyObject start)
    {
        for (DependencyObject? node = start; node is not null; node = Parent(node))
        {
            if (node is ScrollViewer scroller)
            {
                return scroller;
            }
        }

        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static ScrollViewer? FindParentScroller(DependencyObject start)
    {
        for (DependencyObject? node = Parent(start); node is not null; node = Parent(node))
        {
            if (node is ScrollViewer scroller)
            {
                return scroller;
            }
        }

        return null;
    }
}
