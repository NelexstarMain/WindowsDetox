using System.Windows.Controls;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>Plugins page; all behaviour lives in <see cref="PluginsViewModel"/>.</summary>
internal sealed partial class PluginsPage : Page
{
    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Its view model.</param>
    public PluginsPage(PluginsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
