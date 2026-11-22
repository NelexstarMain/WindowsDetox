using System.Windows;
using System.Windows.Controls;

namespace Blocker.UI.Controls.Rendering;

/// <summary>
/// A vertical stack for dashboard widgets: children that do not fit the height of the cell are left out as a whole instead of being cut
/// through the middle of a line. The first child always stays, and a heading (<see cref="KeepWithNextProperty"/>) is never left
/// alone at the bottom without what it introduces.
/// </summary>
internal sealed class FitStackPanel : Panel
{
    /// <summary>Marks a child that introduces the next one, so it is dropped when the next one does not fit.</summary>
    public static readonly DependencyProperty KeepWithNextProperty = DependencyProperty.RegisterAttached(
        "KeepWithNext", typeof(bool), typeof(FitStackPanel), new PropertyMetadata(false));

    /// <summary>Initializes a new instance of the <see cref="FitStackPanel"/> class.</summary>
    public FitStackPanel() => ClipToBounds = true;

    /// <summary>Reads <see cref="KeepWithNextProperty"/>.</summary>
    /// <param name="element">The child.</param>
    /// <returns>Whether the child introduces the next one.</returns>
    public static bool GetKeepWithNext(DependencyObject element) => (bool)element.GetValue(KeepWithNextProperty);

    /// <summary>Sets <see cref="KeepWithNextProperty"/>.</summary>
    /// <param name="element">The child.</param>
    /// <param name="value">Whether the child introduces the next one.</param>
    public static void SetKeepWithNext(DependencyObject element, bool value) => element.SetValue(KeepWithNextProperty, value);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
        }

        int visible = CountThatFit(availableSize.Height);
        double height = 0;
        for (int i = 0; i < visible; i++)
        {
            height += InternalChildren[i].DesiredSize.Height;
        }

        return new Size(width, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        int visible = CountThatFit(finalSize.Height);
        double y = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            UIElement child = InternalChildren[i];
            double height = child.DesiredSize.Height;
            if (i < visible)
            {
                child.Arrange(new Rect(0, y, finalSize.Width, height));
                y += height;
            }
            else
            {
                // Parked below the panel: the clip hides it and takes it out of hit testing.
                child.Arrange(new Rect(0, finalSize.Height + 1, finalSize.Width, height));
            }
        }

        return finalSize;
    }

    private int CountThatFit(double available)
    {
        int count = InternalChildren.Count;
        if (double.IsInfinity(available))
        {
            return count;
        }

        double used = 0;
        int fit = 0;
        while (fit < count && (fit == 0 || used + InternalChildren[fit].DesiredSize.Height <= available + 0.5))
        {
            used += InternalChildren[fit].DesiredSize.Height;
            fit++;
        }

        while (fit > 1 && fit < count && GetKeepWithNext(InternalChildren[fit - 1]))
        {
            fit--;
        }

        return fit;
    }
}
