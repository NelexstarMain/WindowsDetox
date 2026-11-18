using System.Diagnostics;
using Blocker.UI.Native;

namespace Blocker.UI.Agent;

/// <summary>Opens the main window from the agent process.</summary>
internal interface IMainWindowLauncher
{
    /// <summary>Activates the running main window, or starts <c>Blocker.UI.exe</c> without <c>--agent</c> when there is none.</summary>
    void OpenOrActivate();
}

/// <summary>Finds the window-mode process by its visible main window (the agent has none) and otherwise starts a new one directly, never through a shell.</summary>
internal sealed class MainWindowLauncher : IMainWindowLauncher
{
    /// <inheritdoc />
    public void OpenOrActivate()
    {
        string? path = Environment.ProcessPath;
        if (path is null)
        {
            return;
        }

        int self = Environment.ProcessId;
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
            {
                if (process.Id != self && process.MainWindowHandle != 0)
                {
                    TrayNative.Activate(process.MainWindowHandle);
                    return;
                }
            }
        }

        using Process? started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
    }
}
