using System.Windows.Controls;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>Settings page; all behaviour lives in <see cref="SettingsViewModel"/>.</summary>
internal sealed partial class SettingsPage : Page
{
    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Its view model.</param>
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
