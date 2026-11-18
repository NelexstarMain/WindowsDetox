using System.Windows;
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
    private readonly List<NavigationViewItem> _pluginItems = [];

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
        AppPage.Allowlist => typeof(AllowlistPage),
        AppPage.Setup => typeof(SetupPage),
        AppPage.Plugins => typeof(PluginsPage),
        AppPage.Settings => typeof(SettingsPage),
        _ => typeof(DashboardPage),
    };

    private void OnNavigationRequested(object? sender, AppPage page) => _navigation.Navigate(ToType(page));

    private void OnPluginPagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildPluginItems();

    private void OnSelectionChanged(NavigationView sender, System.Windows.RoutedEventArgs args)
    {
        _shell.CurrentPluginPage = sender.SelectedItem is NavigationViewItem { Tag: ContributionViewModel page } ? page : null;
    }

    private void RebuildPluginItems()
    {
        foreach (NavigationViewItem old in _pluginItems)
        {
            _navigation.MenuItems.Remove(old);
        }

        _pluginItems.Clear();
        foreach (ContributionViewModel page in _shell.PluginPages)
        {
            NavigationViewItem item = new()
            {
                Content = page.Title,
                Icon = new SymbolIcon(SymbolRegular.Document24),
                TargetPageType = typeof(PluginPage),
                Tag = page,
                Style = (Style)_navigation.FindResource("Blocker.NavItem"),
            };
            _pluginItems.Add(item);
            _navigation.MenuItems.Add(item);
        }
    }
}

/// <summary>Creates navigation pages from the service provider so they receive their view models by injection.</summary>
/// <param name="services">The application's services.</param>
internal sealed class DiPageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    /// <inheritdoc />
    public object? GetPage(Type pageType) => services.GetService(pageType);
}
