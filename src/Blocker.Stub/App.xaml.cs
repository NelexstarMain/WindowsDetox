using System.Windows;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocker.Stub;

/// <summary>
/// Entry of the block screen. Starts one window per blocked image (a named mutex stops stub storms when a blocked app is launched
/// repeatedly) and exits when that window closes.
/// </summary>
internal sealed partial class App : Application
{
    private IHost? _host;
    private Mutex? _instanceLock;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StubArguments arguments = StubArguments.Parse(e.Args);

        _instanceLock = new Mutex(initiallyOwned: true, arguments.MutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(arguments);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IStubTicker, DispatcherStubTicker>();
        builder.Services.AddSingleton(sp => new IpcClient(
            IpcPipeNames.Service,
            sp.GetRequiredService<ILogger<IpcClient>>(),
            new IpcClientOptions { ConnectTimeout = TimeSpan.FromSeconds(3), RequestTimeout = TimeSpan.FromSeconds(5) }));
        builder.Services.AddSingleton<IStubService, IpcStubService>();
        builder.Services.AddSingleton<BlockScreenViewModel>();
        builder.Services.AddSingleton<BlockWindow>();
        _host = builder.Build();

        BlockWindow window = _host.Services.GetRequiredService<BlockWindow>();
        MainWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
