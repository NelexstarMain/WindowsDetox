using System.Windows;
using System.Windows.Controls;
using System.Collections.Specialized;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace Blocker.UI.Views;

/// <summary>
/// View-only glue between the WPF-UI navigation rail and the view models: serves pages from DI, maps <see cref="INavigator"/> requests
/// to pages, and mirrors the plugin pages of <see cref="ShellViewModel"/> as extra navigation items. Holds no business logic.
/// </summary>
internal sealed class NavigationBinder
{
    private readonly NavigationView _navigation;
    private readonly ShellViewModel _shell;
    private readonly List<Control> _pluginItems = [];

    /// <summary>Creates the binder and connects it to the rail.</summary>
    /// <param name="navigation">The navigation rail.</param>
    /// <param name="shell">Shell view model that owns the plugin pages.</param>
    /// <param name="navigator">Source of programmatic navigation requests.</param>
    /// <param name="pageProvider">Creates pages through DI.</param>
    public NavigationBinder(NavigationView navigation, ShellViewModel shell, INavigator navigator, INavigationViewPageProvider pageProvider)
    {
        _navigation = navigation;
        _shell = shell;
        navigation.SetPageProviderService(pageProvider);
        navigator.NavigationRequested += OnNavigationRequested;
        navigator.PluginPageRequested += OnPluginPageRequested;
        shell.PluginPages.CollectionChanged += OnPluginPagesChanged;
        navigation.SelectionChanged += OnSelectionChanged;
        navigation.Loaded += (_, _) =>
        {
            RebuildPluginItems();
            _navigation.Navigate(typeof(DashboardPage));
        };
    }

    private static Type ToType(AppPage page) => page switch
    {
        AppPage.Allowlist or AppPage.Setup => typeof(SetupPage),
        AppPage.AppLimits => typeof(AppLimitsPage),
        AppPage.Plugins => typeof(PluginsPage),
        AppPage.Settings => typeof(SettingsPage),
        _ => typeof(DashboardPage),
    };

    private void OnNavigationRequested(object? sender, AppPage page) => _navigation.Navigate(ToType(page));

    // Shows the plugin's first page and selects its rail item (items are addressed by their TargetPageTag, the contribution key);
    // a plugin without a page opens the Plugins page instead.
    private void OnPluginPageRequested(object? sender, string pluginId)
    {
        if (_shell.FindPluginPage(pluginId) is not { } page)
        {
            _navigation.Navigate(typeof(PluginsPage));
            return;
        }

        if (!_navigation.Navigate(page.Key))
        {
            _navigation.Navigate(typeof(PluginPage));
        }

        _shell.CurrentPluginPage = page;
    }

    private void OnPluginPagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildPluginItems();

    private void OnSelectionChanged(NavigationView sender, System.Windows.RoutedEventArgs args)
    {
        _shell.CurrentPluginPage = sender.SelectedItem is NavigationViewItem { Tag: ContributionViewModel page } ? page : null;
    }

    private void RebuildPluginItems()
    {
        foreach (Control old in _pluginItems)
        {
            _navigation.MenuItems.Remove(old);
        }

        _pluginItems.Clear();
        if (_shell.PluginPages.Count == 0)
        {
            return;
        }

        int index = _navigation.MenuItems.Cast<object>().ToList().FindIndex(i => i is NavigationViewItem { TargetPageType: { } t } && t == typeof(PluginsPage));
        if (index < 0)
        {
            index = _navigation.MenuItems.Count;
        }

        List<Control> group = [CreateSeparator()];
        foreach (ContributionViewModel page in _shell.PluginPages)
        {
            group.Add(new NavigationViewItem
            {
                Content = page.Title,
                Icon = (IconElement?)PluginPathIcon.TryCreate(page.Icon) ?? new SymbolIcon(SymbolRegular.PuzzlePiece24),
                TargetPageType = typeof(PluginPage),
                TargetPageTag = page.Key,
                Tag = page,
                Style = (Style)_navigation.FindResource("Blocker.NavItem"),
            });
        }

        group.Add(CreateSeparator());
        foreach (Control item in group)
        {
            _pluginItems.Add(item);
            _navigation.MenuItems.Insert(index++, item);
        }
    }

    private NavigationViewItemSeparator CreateSeparator()
    {
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.HeightProperty, 1.0);
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 8, 10, 8));
        line.SetResourceReference(Border.BackgroundProperty, "Blocker.Border.Subtle");
        return new NavigationViewItemSeparator
        {
            Focusable = false,
            IsHitTestVisible = false,
            Template = new ControlTemplate(typeof(NavigationViewItemSeparator)) { VisualTree = line },
        };
    }
}

/// <summary>Creates navigation pages from the service provider so they receive their view models by injection.</summary>
/// <param name="services">The application's services.</param>
internal sealed class DiPageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    /// <inheritdoc />
    public object? GetPage(Type pageType) => services.GetService(pageType);
}
