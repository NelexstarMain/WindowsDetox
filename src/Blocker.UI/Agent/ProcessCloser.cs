using Blocker.UI.Native;

namespace Blocker.UI.Agent;

/// <summary>Closes a process gracefully by asking its windows to close, never by terminating it.</summary>
internal static class ProcessCloser
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Posts <c>WM_CLOSE</c> to the windows of the process and waits for it to exit.</summary>
    /// <param name="processId">Process id.</param>
    /// <param name="startTimeTicks">Creation time (FILETIME ticks); a mismatch means the id belongs to another process now.</param>
    /// <param name="timeout">How long to wait; clamped to 0.1 to 60 seconds.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> when the process is gone (including when it was already gone or the id was reused).</returns>
    public static async Task<bool> CloseAsync(int processId, long startTimeTicks, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (processId <= 0 || !Win32Interop.IsProcessRunning((uint)processId, startTimeTicks))
        {
            return true;
        }

        _ = Win32Interop.PostCloseToProcessWindows((uint)processId);
        TimeSpan limit = TimeSpan.FromTicks(Math.Clamp(timeout.Ticks, MinTimeout.Ticks, MaxTimeout.Ticks));
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < limit)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            if (!Win32Interop.IsProcessRunning((uint)processId, startTimeTicks))
            {
                return true;
            }
        }

        return false;
    }
}
