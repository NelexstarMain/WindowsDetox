using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Blocker.UI.Controls;
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
        Motion.Apply(Resources);
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
            EnsureAgentRunning();
            MainWindow window = _host.Services.GetRequiredService<MainWindow>();
            window.Closed += (_, _) => Shutdown();
            window.Show();
        }
    }

    /// <summary>Starts this executable with <c>--agent</c> when no agent runs in this session (for example right after installation).</summary>
    private static void EnsureAgentRunning()
    {
        if (Mutex.TryOpenExisting(@"Local\WindowsDetox.Agent", out Mutex? existing))
        {
            existing.Dispose();
            return;
        }

        if (Environment.ProcessPath is { } self)
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(self, "--agent") { UseShellExecute = false });
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
