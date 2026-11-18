using System.Runtime.Versioning;
using Blocker.Core.Supervision;
using Blocker.Platform.Windows.Processes;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.System.Threading;

namespace Blocker.Platform.Windows.Supervision;

/// <summary>
/// <see cref="IProcessWaiter"/>: opens the process with SYNCHRONIZE and waits on the handle (thread-pool wait registration, no polling),
/// so the supervisor learns about an exit the instant the kernel signals it.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class ProcessWaiter : IProcessWaiter
{
    /// <inheritdoc/>
    public async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        using SafeFileHandle process = ProcessNative.Open(processId, PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE);
        if (process.IsInvalid)
        {
            // No such process (anymore): there is nothing to wait for.
            return;
        }

        _ = await HandleWaiter.WaitAsync(process, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
}
