using System.Globalization;
using System.Windows;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocker.Stub;

/// <summary>
/// Entry of the block screen. Starts one window per blocked image (a named mutex plus an IFEO cooldown stamp stops stub storms when a
/// blocked app is launched repeatedly) and exits when that window closes.
/// </summary>
internal sealed partial class App : Application
{
    private static readonly TimeSpan IfeoCooldownWindow = TimeSpan.FromMinutes(2);

    private IHost? _host;
    private Mutex? _instanceLock;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StubArguments arguments = StubArguments.Parse(e.Args);

        _instanceLock = new Mutex(initiallyOwned: true, arguments.MutexName, out bool createdNew);
        if (!createdNew || !TryPassIfeoCooldown(arguments))
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
        _ = ShowUnlessTurnedOffAsync(window, _host.Services.GetRequiredService<BlockScreenViewModel>());
    }

    // The service is asked first: an IFEO launch starts the stub directly, so only its answer can honour "block notices off".
    private async Task ShowUnlessTurnedOffAsync(BlockWindow window, BlockScreenViewModel viewModel)
    {
        await viewModel.InitializeAsync(CancellationToken.None);
        if (viewModel.ShouldShow)
        {
            window.Show();
        }
        else
        {
            Shutdown();
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    private static bool TryPassIfeoCooldown(StubArguments arguments)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsDetox",
                "StubCooldown");
            Directory.CreateDirectory(dir);
            string stamp = Path.Combine(dir, arguments.ImageHash + ".stamp");
            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (arguments.IsIfeo && File.Exists(stamp))
            {
                DateTime lastUtc = File.GetLastWriteTimeUtc(stamp);
                if (now.UtcDateTime - lastUtc < IfeoCooldownWindow)
                {
                    return false;
                }
            }

            File.WriteAllText(stamp, now.UtcTicks.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unwritable profile must never hide the block notice.
            return true;
        }
    }
}
