using System.Windows.Controls;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>Renders the currently selected plugin page; bound to <see cref="ShellViewModel"/>.</summary>
internal sealed partial class PluginPage : Page
{
    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">The shell view model that tracks the selected plugin page.</param>
    public PluginPage(ShellViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
