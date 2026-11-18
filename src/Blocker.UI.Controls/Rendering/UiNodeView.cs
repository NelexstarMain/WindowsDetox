using System.Windows;
using System.Windows.Controls;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>SKELETON: event data of <see cref="UiNodeView.ActionInvoked"/>.</summary>
/// <param name="action">The interaction.</param>
public sealed class UiActionEventArgs(UiAction action) : EventArgs
{
    /// <summary>Gets the interaction the user performed.</summary>
    public UiAction Action { get; } = action;
}

/// <summary>SKELETON: renders a <see cref="UiNode"/> tree with the host's controls. Empty stand-in.</summary>
public class UiNodeView : Control
{
    /// <summary>Identifies <see cref="Node"/>.</summary>
    public static readonly DependencyProperty NodeProperty =
        DependencyProperty.Register(nameof(Node), typeof(UiNode), typeof(UiNodeView), new PropertyMetadata(null));

    /// <summary>Gets or sets the tree to render.</summary>
    public UiNode? Node
    {
        get => (UiNode?)GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    /// <summary>Raised when the user interacts with a button, toggle or form of the tree.</summary>
    public event EventHandler<UiActionEventArgs>? ActionInvoked;

    /// <summary>Raises <see cref="ActionInvoked"/>.</summary>
    /// <param name="action">The interaction.</param>
    protected void OnActionInvoked(UiAction action) => ActionInvoked?.Invoke(this, new UiActionEventArgs(action));
}
