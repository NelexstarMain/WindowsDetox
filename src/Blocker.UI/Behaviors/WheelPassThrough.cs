using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Blocker.UI.Behaviors;

/// <summary>
/// A ScrollViewer marks every wheel event handled, even when it has nothing to scroll or is already at the end, so an outer
/// scroller never moves. The shell nests several of them (the navigation host's scroller, the page scroller, the plugin page
/// scroller, cards with their own list or editor scroller) and often only the outermost has a scrollable extent, so the event
/// is handed to the nearest ancestor that can actually consume it, not just to the direct parent.
/// </summary>
internal static class WheelPassThrough
{
    // Layout rounding leaves fractions of a pixel of extent that no wheel notch can use.
    private const double Tolerance = 0.5;

    /// <summary>Registers the class handler once for the process.</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));

    /// <summary>Decides whether a scroller can move in the wheel direction.</summary>
    /// <param name="offset">Current vertical offset.</param>
    /// <param name="scrollableExtent">Largest vertical offset (extent minus viewport).</param>
    /// <param name="delta">Wheel delta; positive scrolls up, negative scrolls down.</param>
    /// <returns><see langword="true"/> when the scroller would move; <see langword="false"/> when it has no extent or is at the limit.</returns>
    internal static bool CanConsume(double offset, double scrollableExtent, int delta) =>
        delta > 0 ? offset > Tolerance : delta < 0 && offset < scrollableExtent - Tolerance;

    private static bool CanConsume(ScrollViewer viewer, int delta) =>
        viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && CanConsume(viewer.VerticalOffset, viewer.ScrollableHeight, delta);

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Preview tunnels from the outside in: act only on the innermost scroller under the pointer.
        if (e.Handled || sender is not ScrollViewer viewer || e.OriginalSource is not DependencyObject source || Nearest(source) != viewer || CanConsume(viewer, e.Delta))
        {
            return;
        }

        if (FindConsumingAncestor(viewer, e.Delta) is not { } target)
        {
            return;
        }

        e.Handled = true;
        target.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = viewer });
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

    private static ScrollViewer? FindConsumingAncestor(DependencyObject start, int delta)
    {
        for (DependencyObject? node = Parent(start); node is not null; node = Parent(node))
        {
            if (node is ScrollViewer scroller && CanConsume(scroller, delta))
            {
                return scroller;
            }
        }

        return null;
    }
}
