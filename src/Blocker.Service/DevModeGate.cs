using System.IO.Abstractions;
using Blocker.Core.ServiceHost;

namespace Blocker.Service;

/// <summary>
/// Decides whether developer abort may work. Three things must all be true: the binary was compiled with the <c>DEVMODE</c> symbol (Debug
/// builds only; a Release build can never have it), <c>--dev-mode</c> was passed, and the dev key file exists in the data directory
/// (so someone with write access to ProgramData made the choice, not just whoever can edit a service command line).
/// </summary>
internal static class DevModeGate
{
#if DEVMODE
    private const bool CompiledWithDevMode = true;
#else
    private const bool CompiledWithDevMode = false;
#endif

    /// <summary>Returns whether developer abort is enabled.</summary>
    /// <param name="requested">Whether <c>--dev-mode</c> was passed.</param>
    /// <param name="paths">File locations.</param>
    /// <param name="fileSystem">File system.</param>
    /// <returns><see langword="true"/> only in DEVMODE builds with the flag and the key file.</returns>
    public static bool IsEnabled(bool requested, BlockerPaths paths, IFileSystem fileSystem) =>
        CompiledWithDevMode && requested && fileSystem.File.Exists(paths.DevModeKeyPath);
}
