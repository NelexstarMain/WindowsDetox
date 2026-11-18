using System.Windows.Controls;
using System.Windows.Threading;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>Session dashboard page; all behaviour lives in <see cref="DashboardViewModel"/>.</summary>
internal sealed partial class DashboardPage : Page
{
    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Its view model.</param>
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Move keyboard focus into the widget picker's search box when it opens, so typing filters and Esc and Tab act on the sheet.
        PickerOverlay.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _ = Dispatcher.InvokeAsync(() => PickerSearch.Focus(), DispatcherPriority.Input);
            }
        };

        // The sheet grows with its content up to three quarters of the page and scrolls beyond that.
        PickerOverlay.SizeChanged += (_, e) => PickerSheet.MaxHeight = e.NewSize.Height * 0.75;
    }
}
