using System.Windows;

namespace Blocker.UI.Controls;

/// <summary>Loads the Blocker resource dictionary (WPF-UI dark theme plus the industrial overrides).</summary>
public static class BlockerTheme
{
    /// <summary>Gets the pack URI of <c>Themes/Blocker.xaml</c>, for use in <c>App.xaml</c>.</summary>
    public static Uri ThemeUri { get; } = new("pack://application:,,,/Blocker.UI.Controls;component/Themes/Blocker.xaml", UriKind.Absolute);

    /// <summary>Merges the theme into the application resources when no <c>Blocker.Accent</c> resource is visible yet.</summary>
    public static void EnsureApplied()
    {
        Application? app = Application.Current;
        if (app is null || app.TryFindResource("Blocker.Accent") is not null)
        {
            return;
        }

        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeUri });
    }
}
