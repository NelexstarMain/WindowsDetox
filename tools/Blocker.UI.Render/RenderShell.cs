using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Blocker.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Wpf.Ui;
using Wpf.Ui.Abstractions;

namespace Blocker.UI.Render;

/// <summary>
/// The real main window with the real pages and view models, wired like <c>AppHost.AddWindowMode</c> but on top of
/// <see cref="StubServiceConnection"/>, a frozen clock and a ticker that never ticks.
/// </summary>
internal sealed class RenderShell
{
    private RenderShell(ServiceProvider services, MainWindow window)
    {
        Services = services;
        Window = window;
    }

    public ServiceProvider Services { get; }

    public MainWindow Window { get; }

    public INavigator Navigator => Services.GetRequiredService<INavigator>();

    public static RenderShell Create(StubServiceConnection connection, FakeTimeProvider time)
    {
        ServiceCollection services = new();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IServiceConnection>(connection);
        services.AddSingleton<IUiTicker, StillTicker>();
        services.AddSingleton<INavigator, Navigator>();
        services.AddSingleton<IFileDialogs, NoFileDialogs>();

        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<Wpf.Ui.ISnackbarService, SnackbarService>();
        services.AddSingleton<IDialogService>(sp => new BlockerDialogService(sp.GetRequiredService<IContentDialogService>()));
        services.AddSingleton<Blocker.UI.Controls.Services.ISnackbarService>(sp => new BlockerSnackbarService(sp.GetRequiredService<Wpf.Ui.ISnackbarService>()));
        services.AddSingleton<IAppDialogs, AppDialogs>();
        services.AddSingleton<INavigationViewPageProvider, DiPageProvider>();

        services.AddSingleton<AllowlistViewModel>();
        services.AddSingleton<IAllowlistSelection>(sp => sp.GetRequiredService<AllowlistViewModel>());
        services.AddSingleton<IWidgetLayoutStore, DefaultWidgetLayout>();
        services.AddSingleton<WidgetBoardViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<AppSetsViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<AppLimitsViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<DashboardPage>();
        services.AddSingleton<SetupPage>();
        services.AddSingleton<AppLimitsPage>();
        services.AddSingleton<PluginsPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<PluginPage>();
        services.AddSingleton<MainWindow>();

        ServiceProvider provider = services.BuildServiceProvider();
        // Created up front so the session pushes of a scenario reach the dashboard before it is first shown.
        _ = provider.GetRequiredService<DashboardViewModel>();
        MainWindow window = provider.GetRequiredService<MainWindow>();
        window.RootNavigation.Transition = Wpf.Ui.Animations.Transition.None;
        return new RenderShell(provider, window);
    }

    /// <summary>Re-reads every plugin contribution slot, as the real window does on its own timers and pushes.</summary>
    public async Task RefreshPluginsAsync()
    {
        await Services.GetRequiredService<ShellViewModel>().RefreshPagesAsync(CancellationToken.None);
        await Services.GetRequiredService<DashboardViewModel>().RefreshWidgetsAsync(CancellationToken.None);
        await Services.GetRequiredService<SettingsViewModel>().RefreshSectionsAsync(CancellationToken.None);
    }
}

/// <summary>A ticker that never fires: countdowns stay at the value of the last snapshot.</summary>
internal sealed class StillTicker : IUiTicker
{
    public event EventHandler? Tick
    {
        add { }
        remove { }
    }
}

/// <summary>File pickers are never opened by a render.</summary>
internal sealed class NoFileDialogs : IFileDialogs
{
    public string? PickSavePath(string suggestedFileName, string filter) => null;

    public string? PickOpenPath(string filter) => null;
}

/// <summary>No saved dashboard layout: every widget the plugins offer is shown with its default size, as on first start.</summary>
internal sealed class DefaultWidgetLayout : IWidgetLayoutStore
{
    public IReadOnlyList<WidgetPlacement>? Load() => null;

    public void Save(IReadOnlyList<WidgetPlacement> layout)
    {
    }
}
