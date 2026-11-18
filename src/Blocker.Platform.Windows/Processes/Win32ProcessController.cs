using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Blocker.Core.Ports;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// Process control through Win32. <see cref="TryTerminate"/> may run in the SYSTEM service; <see cref="TryCloseGracefullyAsync"/> only works
/// in the interactive user session (it enumerates windows of the caller's desktop), so the service must delegate it to the UI agent.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class Win32ProcessController : IProcessController
{
    /// <summary>Exit code given to terminated processes.</summary>
    public const uint TerminatedExitCode = 0xDEAD;

    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInvalidParameter = 87;
    private const uint NoPackageError = 15700; // APPMODEL_ERROR_NO_PACKAGE
    private const uint InsufficientBufferError = 122;

    private readonly Func<string, bool> _isCritical;

    /// <param name="isCritical">Returns true for image paths that must never be terminated (second line of defence behind the engine's own critical set).</param>
    public Win32ProcessController(Func<string, bool> isCritical) => _isCritical = isCritical;

    /// <inheritdoc/>
    public bool TryTerminate(int processId, long startTimeTicks)
    {
        using var process = ProcessNative.Open(
            processId,
            PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (process.IsInvalid)
        {
            return false;
        }

        // The pid may have been recycled: only the (pid, creation time) pair identifies the process.
        if (!ProcessNative.TryGetStartTicks(process, out var ticks) || ticks != startTimeTicks)
        {
            return false;
        }

        // An image we cannot name cannot be proven non-critical.
        if (ProcessNative.TryGetImagePath(process) is not { } image || _isCritical(image))
        {
            return false;
        }

        if (PInvoke.TerminateProcess(process, TerminatedExitCode))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error is ErrorAccessDenied or ErrorInvalidHandle or ErrorInvalidParameter)
        {
            return false;
        }

        throw new Win32Exception(error);
    }

    /// <summary>Posts WM_CLOSE to the visible top-level windows of the process and waits for it to exit. Must run in the user session; false when there is no window or the timeout elapsed.</summary>
    public async Task<bool> TryCloseGracefullyAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = ProcessNative.Open(
            processId,
            PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (process.IsInvalid)
        {
            // Already gone counts as closed; access denied (other user / elevated) does not.
            return Marshal.GetLastWin32Error() == ErrorInvalidParameter;
        }

        var windows = FindWindows((uint)processId);
        if (windows.Count == 0)
        {
            return false;
        }

        foreach (var window in windows)
        {
            // Failure only means the window vanished meanwhile; the exit wait below is the real outcome.
            PInvoke.PostMessage(window, PInvoke.WM_CLOSE, default, default);
        }

        return await HandleWaiter.WaitAsync(process, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public string? GetPackageFamilyName(int processId)
    {
        using var process = ProcessNative.Open(processId, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (process.IsInvalid)
        {
            return null;
        }

        uint length = 0;
        var result = (uint)PInvoke.GetPackageFamilyName(process, ref length, default);
        if (result == NoPackageError)
        {
            return null;
        }

        if (result != InsufficientBufferError)
        {
            throw new Win32Exception((int)result);
        }

        var buffer = new char[length];
        result = (uint)PInvoke.GetPackageFamilyName(process, ref length, buffer);
        return result == 0 ? new string(buffer, 0, (int)length - 1) : throw new Win32Exception((int)result);
    }

    private static List<HWND> FindWindows(uint processId)
    {
        var found = new List<HWND>();
        WNDENUMPROC callback = (window, _) =>
        {
            if (PInvoke.GetWindowThreadProcessId(window, out var owner) != 0 && owner == processId && PInvoke.IsWindowVisible(window))
            {
                found.Add(window);
            }

            return true;
        };

        if (!PInvoke.EnumWindows(callback, default))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        GC.KeepAlive(callback);
        return found;
    }
}
