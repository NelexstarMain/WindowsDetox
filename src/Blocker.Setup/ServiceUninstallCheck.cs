namespace Blocker.Setup;

/// <summary>A setup failure with a message meant for the user.</summary>
internal sealed class SetupException(string message) : Exception(message);

/// <summary>
/// Reads the exit code of <c>Blocker.Service.exe --uninstall</c> (see <c>ExitCodes</c> in <c>src/Blocker.Service/ServiceCommands.cs</c>).
/// Everything but 0 means the services or the system changes are still there, so deleting the files, the data directory (which holds the id
/// that marks our changes) and the uninstall entry would strand them: the check must stop Setup before anything is removed.
/// </summary>
internal static class ServiceUninstallCheck
{
    private const int SessionActive = 3;
    private const int Incomplete = 4;

    /// <exception cref="SetupException">The exit code is not 0.</exception>
    public static void EnsureSucceeded(int exitCode, string sessionActiveMessage, string incompleteMessage)
    {
        switch (exitCode)
        {
            case 0:
                return;
            case SessionActive:
                throw new SetupException(sessionActiveMessage);
            case Incomplete:
                throw new SetupException(incompleteMessage);
            default:
                throw new SetupException(
                    $"Removing the installed services failed (code {exitCode}). Nothing was removed. Restart Windows, or run \"Blocker.Service.exe --recover\" as an administrator, then try again.");
        }
    }
}
