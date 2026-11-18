using System.ComponentModel;
using System.Windows;

namespace Blocker.Setup;

/// <summary>
/// Installer entry point. With the embedded payload it installs; the copy in the install directory uninstalls.
/// <c>--quiet</c> runs without a window and reports through the exit code (0 success, 1 failure, 3 files pending a restart).
/// </summary>
internal sealed partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool uninstall = !SetupEngine.HasPayload || e.Args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));
        if (!e.Args.Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)))
        {
            MainWindow = new MainWindow(uninstall);
            MainWindow.Show();
            return;
        }

        var engine = new SetupEngine(new Progress<string>(Console.WriteLine));
        try
        {
            int code = uninstall ? (await engine.UninstallAsync(CancellationToken.None) ? 3 : 0) : await InstallAsync(engine);
            Shutdown(code);
        }
        catch (Exception ex) when (ex is SetupException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            Shutdown(1);
        }
    }

    private static async Task<int> InstallAsync(SetupEngine engine)
    {
        await engine.InstallAsync(CancellationToken.None);
        return 0;
    }
}
