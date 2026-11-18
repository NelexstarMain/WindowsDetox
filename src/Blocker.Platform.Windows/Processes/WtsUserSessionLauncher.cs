using System.Runtime.Versioning;
using Blocker.Core.Ports;
using Windows.Win32.System.Threading;

namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// Starts executables on the interactive desktop (<c>winsta0\default</c>) of the active console user. The caller must be SYSTEM
/// (WTSQueryUserToken needs SE_TCB_NAME; CreateProcessAsUserW needs SE_ASSIGNPRIMARYTOKEN_NAME), i.e. the Session 0 service.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class WtsUserSessionLauncher : IUserSessionLauncher
{
    /// <inheritdoc/>
    public int? LaunchInActiveSession(string executablePath, string arguments)
    {
        using var token = TokenProcess.QueryConsoleUserToken();
        if (token is null)
        {
            return null;
        }

        using var started = TokenProcess.Start(token, executablePath, arguments, default(PROCESS_CREATION_FLAGS), TokenProcess.InteractiveDesktop);
        return started.ProcessId;
    }
}
