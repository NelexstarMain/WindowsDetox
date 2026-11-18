using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Blocker.UI.Controls;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>
/// Session dashboard page; behaviour lives in <see cref="DashboardViewModel"/>. This file only does what needs the visual tree: the
/// hover-intent timing of the widget picker rail and placing its flyout level with the item it belongs to.
/// </summary>
internal sealed partial class DashboardPage : Page
{
    private const double RailWidth = 84;
    private const double FlyoutGap = 8;
    private const double EdgeGap = 16;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly HoverIntent<WidgetPickerGroup> _hover = new(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(250));
    private readonly DispatcherTimer _hoverTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
    private FrameworkElement? _anchor;

    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Its view model.</param>
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        _hoverTimer.Tick += OnHoverTick;

        // Move keyboard focus onto the rail when it opens so Esc and Tab act on it; forget any hover when it closes.
        PickerOverlay.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _ = Dispatcher.InvokeAsync(() => PickerClose.Focus(), DispatcherPriority.Input);
            }
            else
            {
                _hoverTimer.Stop();
                _hover.Force(null);
            }
        };

        // The flyout never grows taller than the page.
        PickerOverlay.SizeChanged += (_, e) =>
        {
            PickerFlyout.MaxHeight = Math.Max(120, e.NewSize.Height - (2 * EdgeGap));
            PositionFlyout(animate: false);
        };
    }

    private WidgetPickerViewModel? Picker => PickerOverlay.DataContext as WidgetPickerViewModel;

    private void OnRailItemEnter(object sender, MouseEventArgs e) => PointAt(sender as FrameworkElement);

    private void OnRailItemLeave(object sender, MouseEventArgs e) => PointAt(null);

    private void OnFlyoutEnter(object sender, MouseEventArgs e) => PointAt(null, keepFlyout: true);

    private void OnFlyoutLeave(object sender, MouseEventArgs e) => PointAt(null);

    private void OnFlyoutSizeChanged(object sender, SizeChangedEventArgs e) => PositionFlyout(animate: false);

    // Keyboard focus (and a click, which also focuses the item) shows the flyout at once.
    private void OnRailItemFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WidgetPickerGroup group } item && Picker is { } picker)
        {
            _anchor = item;
            _hover.Force(group);
            bool follow = picker.HasFlyout;
            picker.HoveredGroup = group;
            PositionFlyout(follow);
        }
    }

    // The pointer is over a rail item, over the flyout (which keeps the plugin it shows), or over neither.
    private void PointAt(FrameworkElement? item, bool keepFlyout = false)
    {
        if (item is not null)
        {
            _anchor = item;
        }

        WidgetPickerGroup? group = item?.DataContext as WidgetPickerGroup ?? (keepFlyout ? Picker?.FlyoutGroup : null);
        _hover.Point(group, Clock.Elapsed);
        if (_hover.IsPending)
        {
            _hoverTimer.Start();
        }
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (_hover.Update(Clock.Elapsed) && Picker is { } picker)
        {
            bool follow = picker.HasFlyout && _hover.Current is not null;
            picker.HoveredGroup = _hover.Current;
            if (_hover.Current is not null)
            {
                PositionFlyout(follow);
            }
        }

        if (!_hover.IsPending)
        {
            _hoverTimer.Stop();
        }
    }

    /// <summary>Places the flyout left of the rail with its top at the hovered item, kept inside the page; slides there when it already shows.</summary>
    private void PositionFlyout(bool animate)
    {
        if (_anchor is not { IsLoaded: true } anchor || PickerOverlay.ActualHeight <= 0)
        {
            return;
        }

        double top = anchor.TranslatePoint(new Point(0, 0), PickerOverlay).Y;
        double room = Math.Max(EdgeGap, PickerOverlay.ActualHeight - PickerFlyout.ActualHeight - EdgeGap);
        Thickness target = new(0, Math.Clamp(top, EdgeGap, room), RailWidth + FlyoutGap, 0);
        if (target == PickerFlyout.Margin)
        {
            return;
        }

        if (animate && Motion.Enabled)
        {
            PickerFlyout.BeginAnimation(MarginProperty, new ThicknessAnimation(target, Motion.Scaled(Motion.Fast))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        }
        else
        {
            PickerFlyout.BeginAnimation(MarginProperty, null);
        }

        PickerFlyout.Margin = target;
    }
}
