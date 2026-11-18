using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.UI.Agent;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Blocker.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Abstractions;

namespace Blocker.UI;

/// <summary>Builds the generic host for the window mode and for <c>--agent</c> mode.</summary>
internal static class AppHost
{
    /// <summary>Creates the host. Nothing starts until <see cref="IHost.StartAsync"/>.</summary>
    /// <param name="options">Command-line options.</param>
    /// <returns>The host.</returns>
    public static IHost Build(StartupOptions options)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        IServiceCollection services = builder.Services;

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton(sp => new IpcClient(IpcPipeNames.Service, sp.GetRequiredService<ILogger<IpcClient>>()));
        services.AddSingleton<ServiceConnection>();
        services.AddSingleton<IServiceConnection>(sp => sp.GetRequiredService<ServiceConnection>());
        services.AddHostedService(sp => sp.GetRequiredService<ServiceConnection>());

        if (options.IsAgent)
        {
            AddAgent(services);
        }
        else
        {
            AddWindowMode(services);
        }

        return builder.Build();
    }

    private static void AddAgent(IServiceCollection services)
    {
        services.AddSingleton<IToastPresenter, ToastPresenter>();
        services.AddSingleton<AgentService>();
        services.AddSingleton<IConnectionInitializer>(sp => sp.GetRequiredService<AgentService>());
        services.AddHostedService(sp => sp.GetRequiredService<AgentService>());
    }

    private static void AddWindowMode(IServiceCollection services)
    {
        services.AddSingleton<IUiTicker, DispatcherUiTicker>();
        services.AddSingleton<INavigator, Navigator>();
        services.AddSingleton<IUiPreferences, UiPreferences>();
        services.AddSingleton<IFileDialogs, FileDialogs>();

        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<Wpf.Ui.ISnackbarService, SnackbarService>();
        services.AddSingleton<IDialogService>(sp => new BlockerDialogService(sp.GetRequiredService<IContentDialogService>()));
        services.AddSingleton<Blocker.UI.Controls.Services.ISnackbarService>(sp => new BlockerSnackbarService(sp.GetRequiredService<Wpf.Ui.ISnackbarService>()));
        services.AddSingleton<IAppDialogs, AppDialogs>();
        services.AddSingleton<INavigationViewPageProvider, DiPageProvider>();

        services.AddSingleton<AllowlistViewModel>();
        services.AddSingleton<IAllowlistSelection>(sp => sp.GetRequiredService<AllowlistViewModel>());
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<DashboardPage>();
        services.AddSingleton<AllowlistPage>();
        services.AddSingleton<SetupPage>();
        services.AddSingleton<PluginsPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<PluginPage>();
        services.AddSingleton<MainWindow>();
    }
}
