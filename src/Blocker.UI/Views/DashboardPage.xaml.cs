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

        // Move keyboard focus into the widget picker when it opens, so Esc and Tab act on the sheet.
        PickerOverlay.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _ = Dispatcher.InvokeAsync(() => PickerClose.Focus(), DispatcherPriority.Input);
            }
        };
    }
}
