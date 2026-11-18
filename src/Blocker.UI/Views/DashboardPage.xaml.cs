using System.Windows.Controls;
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
    }
}
