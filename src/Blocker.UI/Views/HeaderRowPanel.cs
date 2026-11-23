using System.Windows;
using System.Windows.Controls;

namespace Blocker.UI.Views;

/// <summary>
/// Page header with two children: the title block and its actions. They share one row (actions right-aligned) while the title
/// keeps its natural width; otherwise the actions wrap below the title instead of squeezing it.
/// </summary>
internal sealed class HeaderRowPanel : Panel
{
    private const double RowGap = 12;
    private const double ColumnGap = 24;

    private bool _stacked;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2)
        {
            return default;
        }

        UIElement title = InternalChildren[0];
        UIElement actions = InternalChildren[1];
        title.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        actions.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        _stacked = title.DesiredSize.Width + ColumnGap + actions.DesiredSize.Width > availableSize.Width;
        if (!_stacked)
        {
            return new Size(availableSize.Width, Math.Max(title.DesiredSize.Height, actions.DesiredSize.Height));
        }

        title.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        actions.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(availableSize.Width, title.DesiredSize.Height + RowGap + actions.DesiredSize.Height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2)
        {
            return finalSize;
        }

        UIElement title = InternalChildren[0];
        UIElement actions = InternalChildren[1];
        if (_stacked)
        {
            title.Arrange(new Rect(0, 0, finalSize.Width, title.DesiredSize.Height));
            actions.Arrange(new Rect(0, title.DesiredSize.Height + RowGap, Math.Min(finalSize.Width, actions.DesiredSize.Width), actions.DesiredSize.Height));
        }
        else
        {
            title.Arrange(new Rect(0, (finalSize.Height - title.DesiredSize.Height) / 2, title.DesiredSize.Width, title.DesiredSize.Height));
            actions.Arrange(new Rect(finalSize.Width - actions.DesiredSize.Width, (finalSize.Height - actions.DesiredSize.Height) / 2, actions.DesiredSize.Width, actions.DesiredSize.Height));
        }

        return finalSize;
    }
}
