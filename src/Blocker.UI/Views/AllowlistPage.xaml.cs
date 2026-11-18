using System.Windows.Controls;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>Allowlist page; all behaviour lives in <see cref="AllowlistViewModel"/>.</summary>
internal sealed partial class AllowlistPage : Page
{
    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Its view model.</param>
    public AllowlistPage(AllowlistViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
