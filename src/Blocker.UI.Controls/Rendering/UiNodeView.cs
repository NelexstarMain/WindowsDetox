using System.Windows;
using System.Windows.Controls;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>
/// Renders a plugin-supplied <see cref="UiNode"/> tree with the Blocker theme. The tree is validated first; an invalid
/// tree produces a small error card instead of the plugin content. Interactions are reported through <see cref="ActionInvoked"/>.
/// </summary>
public sealed class UiNodeView : ContentControl
{
    /// <summary>Identifies the <see cref="Node"/> dependency property.</summary>
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(
        nameof(Node), typeof(UiNode), typeof(UiNodeView), new PropertyMetadata(null, OnNodeChanged));

    /// <summary>Initializes a new instance of the <see cref="UiNodeView"/> class.</summary>
    public UiNodeView()
    {
        IsTabStop = false;
        BlockerTheme.EnsureApplied();
    }

    /// <summary>Raised when the user clicks a button, flips a toggle or submits a form. The argument is the action to deliver to the plugin.</summary>
    public event EventHandler<UiAction>? ActionInvoked;

    /// <summary>Gets or sets the tree to render.</summary>
    public UiNode? Node
    {
        get => (UiNode?)GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    private static void OnNodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((UiNodeView)d).Rebuild();

    private void Rebuild()
    {
        UiNode? node = Node;
        if (node is null)
        {
            Content = null;
            return;
        }

        UiValidationResult validation = UiTreeValidator.Validate(node);
        if (!validation.IsValid)
        {
            Content = UiVisualBuilder.BuildErrorCard(validation.Errors);
            return;
        }

        FrameworkElement visual = new UiVisualBuilder(action => ActionInvoked?.Invoke(this, action)).Build(node);
        Content = node is UiPage
            ? new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = visual }
            : visual;
    }
}
