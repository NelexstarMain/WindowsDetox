using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Blocker.Platform.Windows.Processes;

/// <summary>Waits on a kernel handle (process, job) without blocking a thread: uses the thread-pool wait registration.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal static class HandleWaiter
{
    /// <summary>Completes with true when <paramref name="handle"/> becomes signaled, false on timeout.</summary>
    /// <remarks>The caller keeps ownership of the handle; it is reference-counted for the duration of the wait so a concurrent Dispose cannot close it underneath the wait.</remarks>
    public static async Task<bool> WaitAsync(SafeHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            using var waitHandle = new BorrowedWaitHandle(handle.DangerousGetHandle());
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle, static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut), completion, timeout, executeOnlyOnce: true);
            try
            {
                using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), completion))
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                registration.Unregister(null);
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>A <see cref="WaitHandle"/> over a handle owned by someone else (never closes it).</summary>
    private sealed class BorrowedWaitHandle : WaitHandle
    {
        public BorrowedWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }
}
