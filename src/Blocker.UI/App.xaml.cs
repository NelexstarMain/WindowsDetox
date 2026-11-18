using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Blocker.UI.Views;

namespace Blocker.UI;

/// <summary>
/// Application entry. Hosts the generic host for the whole process lifetime. In window mode it shows <see cref="MainWindow"/>;
/// with <c>--agent</c> there is no window and the agent service drives everything.
/// </summary>
internal sealed partial class App : Application
{
    private IHost? _host;
    private Mutex? _agentLock;

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupOptions options = StartupOptions.Parse(e.Args);

        if (options.IsAgent)
        {
            // One agent per user session; a second launch exits quietly.
            _agentLock = new Mutex(initiallyOwned: true, @"Local\WindowsDetox.Agent", out bool createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }
        }

        _host = AppHost.Build(options);
        await _host.StartAsync();

        if (!options.IsAgent)
        {
            MainWindow window = _host.Services.GetRequiredService<MainWindow>();
            window.Closed += (_, _) => Shutdown();
            window.Show();
        }
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _agentLock?.Dispose();
        base.OnExit(e);
    }
}
