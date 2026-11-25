using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Blocker.Stub;

/// <summary>The block notice window: placement and the fade-in only; all behaviour lives in <see cref="BlockScreenViewModel"/>.</summary>
internal sealed partial class BlockWindow : Window
{
    private const double ScreenMargin = 16;

    private readonly BlockScreenViewModel _viewModel;

    /// <summary>Creates the window.</summary>
    /// <param name="viewModel">Its view model.</param>
    public BlockWindow(BlockScreenViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        PlaceInCorner();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            viewModel.InitializeCommand.Execute(null);
        };
        Card.MouseEnter += (_, _) => viewModel.IsPointerOver = true;
        Card.MouseLeave += (_, _) => viewModel.IsPointerOver = false;
    }

    private void PlaceInCorner()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Right - Width - ScreenMargin;
        Top = area.Bottom - Height - ScreenMargin;
    }

    private void OnHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsExpanded)
        {
            return;
        }

        _viewModel.ExpandCommand.Execute(null);
        Activate();
    }
}
