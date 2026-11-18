using System.Windows;

namespace Blocker.Setup;

/// <summary>Installer entry point. With the embedded payload it installs; the copy in the install directory uninstalls.</summary>
internal sealed partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool uninstall = !SetupEngine.HasPayload || e.Args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));
        MainWindow = new MainWindow(uninstall);
        MainWindow.Show();
    }
}
