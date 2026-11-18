using System.Windows;

namespace Blocker.UI.Controls.Metaballs;

/// <summary>SKELETON: attached properties of the metaball layer. Replaced by the real library at merge.</summary>
public static class Metaball
{
    /// <summary>Marks an element as a metaball body (cards).</summary>
    public static readonly DependencyProperty IsBodyProperty =
        DependencyProperty.RegisterAttached("IsBody", typeof(bool), typeof(Metaball), new PropertyMetadata(false));

    /// <summary>Gets whether the element is a metaball body.</summary>
    /// <param name="element">Element.</param>
    /// <returns>The value.</returns>
    public static bool GetIsBody(DependencyObject element) => (bool)element.GetValue(IsBodyProperty);

    /// <summary>Sets whether the element is a metaball body.</summary>
    /// <param name="element">Element.</param>
    /// <param name="value">The value.</param>
    public static void SetIsBody(DependencyObject element, bool value) => element.SetValue(IsBodyProperty, value);
}
