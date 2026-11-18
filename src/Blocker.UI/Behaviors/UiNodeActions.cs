using System.Windows;
using System.Windows.Input;
using Blocker.UI.Controls.Rendering;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Behaviors;

/// <summary>
/// Attached properties that turn <see cref="UiNodeView.ActionInvoked"/> into a command call, so plugin interactions reach view models
/// without code-behind. The command receives a <see cref="ContributionAction"/>.
/// </summary>
internal static class UiNodeActions
{
    /// <summary>Identifies the <c>Command</c> attached property.</summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(UiNodeActions), new PropertyMetadata(null, OnCommandChanged));

    /// <summary>Identifies the <c>Contribution</c> attached property.</summary>
    public static readonly DependencyProperty ContributionProperty =
        DependencyProperty.RegisterAttached("Contribution", typeof(ContributionViewModel), typeof(UiNodeActions), new PropertyMetadata(null));

    /// <summary>Gets the command that receives interactions.</summary>
    /// <param name="element">The view.</param>
    /// <returns>The command, if set.</returns>
    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    /// <summary>Sets the command that receives interactions.</summary>
    /// <param name="element">The view.</param>
    /// <param name="value">The command.</param>
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    /// <summary>Gets the contribution the view renders.</summary>
    /// <param name="element">The view.</param>
    /// <returns>The contribution, if set.</returns>
    public static ContributionViewModel? GetContribution(DependencyObject element) => (ContributionViewModel?)element.GetValue(ContributionProperty);

    /// <summary>Sets the contribution the view renders.</summary>
    /// <param name="element">The view.</param>
    /// <param name="value">The contribution.</param>
    public static void SetContribution(DependencyObject element, ContributionViewModel? value) => element.SetValue(ContributionProperty, value);

    private static void OnCommandChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UiNodeView view)
        {
            return;
        }

        view.ActionInvoked -= OnActionInvoked;
        if (e.NewValue is not null)
        {
            view.ActionInvoked += OnActionInvoked;
        }
    }

    private static void OnActionInvoked(object? sender, UiActionEventArgs e)
    {
        if (sender is not UiNodeView view || GetCommand(view) is not { } command || GetContribution(view) is not { } contribution)
        {
            return;
        }

        ContributionAction parameter = new(contribution, e.Action);
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
