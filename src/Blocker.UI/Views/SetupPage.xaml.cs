using System.Windows.Controls;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>Session and protection setup page; all behaviour lives in <see cref="SetupViewModel"/>.</summary>
internal sealed partial class SetupPage : Page
{
    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Its view model.</param>
    public SetupPage(SetupViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
