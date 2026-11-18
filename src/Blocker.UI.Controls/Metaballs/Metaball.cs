using System.Windows;
using System.Windows.Media;

namespace Blocker.UI.Controls.Metaballs;

/// <summary>
/// Attached properties of the metaball layer. Setting <see cref="IsBodyProperty"/> on an element registers it as a
/// body with the nearest <see cref="MetaballHost"/> ancestor once it is loaded.
/// </summary>
public static class Metaball
{
    /// <summary>Marks an element as a metaball body.</summary>
    public static readonly DependencyProperty IsBodyProperty = DependencyProperty.RegisterAttached(
        "IsBody", typeof(bool), typeof(Metaball), new PropertyMetadata(false, OnIsBodyChanged));

    /// <summary>Corner radius of a body, in DIPs.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(double), typeof(Metaball), new PropertyMetadata(12.0));

    /// <summary>Requested quality of a <see cref="MetaballHost"/>.</summary>
    public static readonly DependencyProperty QualityProperty = DependencyProperty.RegisterAttached(
        "Quality", typeof(MetaballQuality), typeof(Metaball), new PropertyMetadata(MetaballQuality.High, OnQualityChanged));

    private static readonly DependencyProperty RegisteredHostProperty = DependencyProperty.RegisterAttached(
        "RegisteredHost", typeof(MetaballHost), typeof(Metaball));

    /// <summary>Gets whether <paramref name="element"/> is a metaball body.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The value.</returns>
    public static bool GetIsBody(DependencyObject element) => (bool)element.GetValue(IsBodyProperty);

    /// <summary>Sets whether <paramref name="element"/> is a metaball body.</summary>
    /// <param name="element">The element.</param>
    /// <param name="value">The value.</param>
    public static void SetIsBody(DependencyObject element, bool value) => element.SetValue(IsBodyProperty, value);

    /// <summary>Gets the body corner radius.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The radius in DIPs.</returns>
    public static double GetCornerRadius(DependencyObject element) => (double)element.GetValue(CornerRadiusProperty);

    /// <summary>Sets the body corner radius.</summary>
    /// <param name="element">The element.</param>
    /// <param name="value">The radius in DIPs.</param>
    public static void SetCornerRadius(DependencyObject element, double value) => element.SetValue(CornerRadiusProperty, value);

    /// <summary>Gets the requested quality.</summary>
    /// <param name="element">The host.</param>
    /// <returns>The quality.</returns>
    public static MetaballQuality GetQuality(DependencyObject element) => (MetaballQuality)element.GetValue(QualityProperty);

    /// <summary>Sets the requested quality.</summary>
    /// <param name="element">The host.</param>
    /// <param name="value">The quality.</param>
    public static void SetQuality(DependencyObject element, MetaballQuality value) => element.SetValue(QualityProperty, value);

    private static void OnQualityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MetaballHost host)
        {
            host.UpdateQuality();
        }
    }

    private static void OnIsBodyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.Loaded += OnBodyLoaded;
            element.Unloaded += OnBodyUnloaded;
            if (element.IsLoaded)
            {
                Attach(element);
            }
        }
        else
        {
            element.Loaded -= OnBodyLoaded;
            element.Unloaded -= OnBodyUnloaded;
            Detach(element);
        }
    }

    private static void OnBodyLoaded(object sender, RoutedEventArgs e) => Attach((FrameworkElement)sender);

    private static void OnBodyUnloaded(object sender, RoutedEventArgs e) => Detach((FrameworkElement)sender);

    private static void Attach(FrameworkElement element)
    {
        MetaballHost? host = FindHost(element);
        if (host is null || ReferenceEquals(element.GetValue(RegisteredHostProperty), host))
        {
            return;
        }

        Detach(element);
        element.SetValue(RegisteredHostProperty, host);
        host.Register(element);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(RegisteredHostProperty) is MetaballHost host)
        {
            host.Unregister(element);
            element.ClearValue(RegisteredHostProperty);
        }
    }

    private static MetaballHost? FindHost(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is MetaballHost host)
            {
                return host;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
