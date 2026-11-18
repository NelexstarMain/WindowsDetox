using System.Windows;
using System.Windows.Input;
using Blocker.UI.Controls.Rendering;
using Blocker.UI.Sdk;

namespace Blocker.Stub;

/// <summary>Turns <see cref="UiNodeView.ActionInvoked"/> into a command call that receives a <see cref="BlockContributionAction"/>.</summary>
internal static class UiNodeCommand
{
    /// <summary>Identifies the <c>Command</c> attached property.</summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(UiNodeCommand), new PropertyMetadata(null, OnCommandChanged));

    /// <summary>Identifies the <c>Contribution</c> attached property.</summary>
    public static readonly DependencyProperty ContributionProperty =
        DependencyProperty.RegisterAttached("Contribution", typeof(BlockContribution), typeof(UiNodeCommand), new PropertyMetadata(null));

    /// <summary>Gets the command.</summary>
    /// <param name="element">The view.</param>
    /// <returns>The command, if set.</returns>
    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    /// <summary>Sets the command.</summary>
    /// <param name="element">The view.</param>
    /// <param name="value">The command.</param>
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    /// <summary>Gets the rendered contribution.</summary>
    /// <param name="element">The view.</param>
    /// <returns>The contribution, if set.</returns>
    public static BlockContribution? GetContribution(DependencyObject element) => (BlockContribution?)element.GetValue(ContributionProperty);

    /// <summary>Sets the rendered contribution.</summary>
    /// <param name="element">The view.</param>
    /// <param name="value">The contribution.</param>
    public static void SetContribution(DependencyObject element, BlockContribution? value) => element.SetValue(ContributionProperty, value);

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

    private static void OnActionInvoked(object? sender, UiAction action)
    {
        if (sender is not UiNodeView view || GetCommand(view) is not { } command || GetContribution(view) is not { } contribution)
        {
            return;
        }

        BlockContributionAction parameter = new(contribution, action);
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
