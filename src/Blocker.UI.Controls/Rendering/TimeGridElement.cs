using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Blocker.UI.Sdk;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Blocker.UI.Controls.Rendering;

/// <summary>
/// View state a <see cref="UiNodeView"/> keeps across re-renders, because every render builds a new element tree: the scroll position of
/// each grid (so the schedule does not jump back to <see cref="UiTimeGrid.ScrollToMinute"/> after a drag) and which popover already took focus.
/// </summary>
internal sealed class TimeGridViewState
{
    /// <summary>Gets the last vertical scroll offset per grid id.</summary>
    public Dictionary<string, double> ScrollOffsets { get; } = [];

    /// <summary>Gets or sets the anchor id of the popover that already focused its first input.</summary>
    public string? PopoverFocusedFor { get; set; }
}

/// <summary>
/// Host rendering of a <see cref="UiTimeGrid"/>: a day header row, a fixed-height scrolling body (<see cref="TimeGridSurface"/>) and an
/// in-control overlay that carries the popover card, so the popover is part of the element and shows up in bitmap captures.
/// </summary>
internal sealed class TimeGridElement : Grid
{
    private static readonly Effect CardShadow = CreateShadow();

    private readonly UiTimeGrid _grid;
    private readonly Action<UiAction> _raise;
    private readonly TimeGridViewState _state;
    private readonly TimeGridSurface _surface;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _overlay = new();
    private readonly Border? _card;
    private bool _scrollInitialised;

    /// <summary>Initializes a new instance of the <see cref="TimeGridElement"/> class.</summary>
    /// <param name="grid">The node to render.</param>
    /// <param name="raise">Receives the actions for the plugin.</param>
    /// <param name="buildPopover">Renders the popover content with the normal builder.</param>
    /// <param name="state">View state shared across re-renders.</param>
    public TimeGridElement(UiTimeGrid grid, Action<UiAction> raise, Func<UiNode, FrameworkElement> buildPopover, TimeGridViewState state)
    {
        _grid = grid;
        _raise = raise;
        _state = state;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(TimeGridMath.HeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        FrameworkElement header = BuildHeader();
        SetRow(header, 0);
        Children.Add(header);

        _surface = new TimeGridSurface(grid, raise);
        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _surface,
        };
        _scroller.ScrollChanged += OnScrollChanged;
        SetRow(_scroller, 1);
        Children.Add(_scroller);

        SetRow(_overlay, 0);
        SetRowSpan(_overlay, 2);
        Children.Add(_overlay);

        if (grid.Popover is { } popover && grid.PopoverAnchorId is not null)
        {
            _card = BuildCard(buildPopover(popover));
            _overlay.Children.Add(_card);
            Loaded += OnLoaded;
        }
        else
        {
            state.PopoverFocusedFor = null;
        }

        SizeChanged += (_, _) => PositionPopover();
        AutomationProperties.SetName(this, "Schedule");
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        double viewport = TimeGridMath.ViewportHeight(constraint.Height);
        if (_scroller.Height != viewport)
        {
            _scroller.Height = viewport;
        }

        return base.MeasureOverride(constraint);
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_surface.CancelDrag())
        {
            e.Handled = true;
        }
        else if (_card is not null && _grid.DismissId is { } dismissId)
        {
            _raise(UiAction.Click(dismissId));
            e.Handled = true;
        }
    }

    private static Effect CreateShadow()
    {
        var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.5 };
        shadow.Freeze();
        return shadow;
    }

    private static TextBox? FindFirstInput(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { IsEnabled: true, Focusable: true, IsVisible: true } box)
            {
                return box;
            }

            if (FindFirstInput(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private FrameworkElement BuildHeader()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeGridMath.GutterWidth) });
        for (int i = 0; i < _grid.Days.Count; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Button cell = BuildDayButton(_grid.Days[i]);
            Grid.SetColumn(cell, i + 1);
            row.Children.Add(cell);
        }

        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
        border.SetResourceReference(Border.BorderBrushProperty, "Blocker.Border.Strong");
        return border;
    }

    private Button BuildDayButton(UiTimeGridDay day)
    {
        var label = new TextBlock { Text = day.Label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(FrameworkElement.StyleProperty, "Blocker.Text.Caption");
        label.SetResourceReference(TextBlock.ForegroundProperty, day.IsToday ? "Blocker.Accent.Bar" : "Blocker.Text.Secondary");

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(label);
        if (day.Number is { } number)
        {
            var text = new TextBlock
            {
                Text = number.ToString(CultureInfo.InvariantCulture),
                FontSize = 18,
                FontWeight = day.IsToday ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, day.IsToday ? "Blocker.Text.OnAccent" : "Blocker.Text.Primary");
            var badge = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = text,
            };
            if (day.IsToday)
            {
                badge.SetResourceReference(Border.BackgroundProperty, "Blocker.Accent");
            }

            stack.Children.Add(badge);
        }

        var button = new Button { Content = stack, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(2, 2, 2, 4), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Blocker.Button.Subtle");
        string name = day.Number is { } n ? day.Label + " " + n.ToString(CultureInfo.InvariantCulture) : day.Label;
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, day.Id);
        string id = day.Id;
        button.Click += (_, _) => _raise(UiAction.Click(id));
        return button;
    }

    private Border BuildCard(FrameworkElement content)
    {
        // The card is capped at the control height: a long editor scrolls inside it, but the form's footer stays pinned below.
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(BuildCloseButton());
        SetRow(scroller, 1);
        layout.Children.Add(scroller);
        if (TakeSubmit(content) is { } submit)
        {
            submit.Margin = new Thickness(0, 14, 0, 0);
            SetRow(submit, 2);
            layout.Children.Add(submit);
        }

        var card = new Border
        {
            Padding = new Thickness(20, 8, 20, 18),
            CornerRadius = new CornerRadius(12),
            Child = layout,
            Effect = CardShadow,
            Visibility = Visibility.Hidden,
            ClipToBounds = true,
        };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Blocker.CardBorder");
        card.SetResourceReference(Border.BackgroundProperty, "Blocker.Bg.Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Blocker.Border.Strong");

        // A page title (28 px) is too big for a floating card; inside the card a Title text is the card's heading.
        var heading = new Style(typeof(TextBlock));
        heading.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new DynamicResourceExtension("Blocker.Font.Display")));
        heading.Setters.Add(new Setter(TextBlock.FontSizeProperty, 20.0));
        heading.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        heading.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Blocker.Text.Primary")));
        heading.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        card.Resources["Blocker.Text.Title"] = heading;
        return card;
    }

    private Button BuildCloseButton()
    {
        var close = new Button
        {
            Content = new SymbolIcon { Symbol = SymbolRegular.Dismiss24, FontSize = 16 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, -8, 4),
            ToolTip = "Close",
        };
        close.SetResourceReference(FrameworkElement.StyleProperty, "Blocker.Button.Subtle");
        AutomationProperties.SetName(close, "Close");
        if (_grid.DismissId is { } dismissId)
        {
            close.Click += (_, _) => _raise(UiAction.Click(dismissId));
        }

        return close;
    }

    private static FrameworkElement? TakeSubmit(DependencyObject node)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is FrameworkElement { Tag: UiVisualBuilder.FormSubmitTag } pinned && LogicalTreeHelper.GetParent(pinned) is Panel parent)
            {
                parent.Children.Remove(pinned);
                return pinned;
            }

            if (child is DependencyObject nested && TakeSubmit(nested) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_scrollInitialised)
        {
            if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0)
            {
                return;
            }

            _scrollInitialised = true;
            double target = _state.ScrollOffsets.TryGetValue(_grid.Id, out double saved) ? saved : InitialScrollOffset();
            _scroller.ScrollToVerticalOffset(target);
        }
        else if (e.VerticalChange != 0)
        {
            _state.ScrollOffsets[_grid.Id] = e.VerticalOffset;
        }

        PositionPopover();
    }

    // A little above the requested time, so the label of that hour is not cut by the top edge.
    private double InitialScrollOffset() => Math.Max(0, (_grid.ScrollToMinute * TimeGridMath.HourHeight / 60) - 10);

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            PositionPopover();
            FocusFirstInput();
        });

    private void PositionPopover()
    {
        if (_card is null || _grid.PopoverAnchorId is not { } anchorId || !_surface.IsLaidOut || _overlay.ActualWidth <= 0)
        {
            return;
        }

        double boundsWidth = _overlay.ActualWidth;
        double boundsHeight = _overlay.ActualHeight;
        double width = Math.Min(TimeGridMath.PopoverMaxWidth, boundsWidth - (2 * TimeGridMath.PopoverMargin));
        if (width <= 0)
        {
            return;
        }

        _card.Width = width;
        _card.MaxHeight = Math.Max(0, boundsHeight - (2 * TimeGridMath.PopoverMargin));
        _card.Measure(new Size(width, _card.MaxHeight));
        Size size = _card.DesiredSize;

        // The anchor is in surface coordinates; the overlay starts at the control's origin, below it sit the header and the scrolled body.
        PopoverPosition position = _surface.AnchorRect(anchorId) is { } anchor
            ? TimeGridMath.PlacePopover(anchor with { Y = anchor.Y + TimeGridMath.HeaderHeight - _scroller.VerticalOffset }, size.Width, size.Height, boundsWidth, boundsHeight)
            : TimeGridMath.CenterPopover(size.Width, size.Height, boundsWidth, boundsHeight);
        Canvas.SetLeft(_card, position.X);
        Canvas.SetTop(_card, position.Y);
        _card.Visibility = Visibility.Visible;
    }

    private void FocusFirstInput()
    {
        if (_card is null || _grid.PopoverAnchorId is not { } anchorId || _state.PopoverFocusedFor == anchorId || _card.Visibility != Visibility.Visible)
        {
            return;
        }

        _card.UpdateLayout();
        if (FindFirstInput(_card) is { } input)
        {
            _state.PopoverFocusedFor = anchorId;
            input.Focus();
            input.SelectAll();
        }
    }
}
