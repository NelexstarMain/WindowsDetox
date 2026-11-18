using System.Windows;

namespace Blocker.Stub;

/// <summary>The block screen window; all behaviour lives in <see cref="BlockScreenViewModel"/>.</summary>
internal sealed partial class BlockWindow : Window
{
    /// <summary>Creates the window.</summary>
    /// <param name="viewModel">Its view model.</param>
    public BlockWindow(BlockScreenViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => viewModel.InitializeCommand.Execute(null);
    }
}
