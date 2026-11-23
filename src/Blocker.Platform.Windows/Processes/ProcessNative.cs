using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Blocker.Core.Ports;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace Blocker.Platform.Windows.Processes;

/// <summary>Shared process helpers on top of CsWin32 (SafeHandle based; nothing here leaks a raw handle).</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal static class ProcessNative
{
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Opens a process; returns an invalid handle (check <see cref="SafeHandle.IsInvalid"/>) when it does not exist or access is denied.</summary>
    public static SafeFileHandle Open(int processId, PROCESS_ACCESS_RIGHTS rights) =>
        PInvoke.OpenProcess_SafeHandle(rights, false, (uint)processId);

    /// <summary>Process creation time as FILETIME ticks.</summary>
    public static bool TryGetStartTicks(SafeHandle process, out long ticks)
    {
        ticks = 0;
        if (!PInvoke.GetProcessTimes(process, out var created, out _, out _, out _))
        {
            return false;
        }

        ticks = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return true;
    }

    /// <summary>Start time of a live process by pid, or <see langword="null"/> when it cannot be opened or no longer exists.</summary>
    public static long? TryGetStartTicks(int processId)
    {
        using var process = Open(processId, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        return !process.IsInvalid && TryGetStartTicks(process, out var ticks) ? ticks : null;
    }

    /// <summary>Win32 image path of an open process, or null when it cannot be read (for example the process is exiting).</summary>
    public static string? TryGetImagePath(SafeHandle process)
    {
        for (var capacity = 512; capacity <= 32768; capacity *= 2)
        {
            var buffer = new char[capacity];
            var size = (uint)capacity;
            if (PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size))
            {
                return new string(buffer, 0, (int)size);
            }

            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Snapshot of all accessible processes. Processes that cannot be opened (protected, exited meanwhile) are skipped.</summary>
    public static IReadOnlyList<ProcessInfo> EnumerateRunning()
    {
        using var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var result = new List<ProcessInfo>();
        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        for (var ok = PInvoke.Process32FirstW(snapshot, ref entry); ok; ok = PInvoke.Process32NextW(snapshot, ref entry))
        {
            if (entry.th32ProcessID != 0 && TryDescribe(entry) is { } info)
            {
                result.Add(info);
            }
        }

        var error = Marshal.GetLastWin32Error();
        if (error != (int)WIN32_ERROR.ERROR_NO_MORE_FILES)
        {
            throw new Win32Exception(error);
        }

        return result;
    }

    private static ProcessInfo? TryDescribe(in PROCESSENTRY32W entry)
    {
        using var process = Open((int)entry.th32ProcessID, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        if (process.IsInvalid || !TryGetStartTicks(process, out var ticks))
        {
            return null;
        }

        var session = PInvoke.ProcessIdToSessionId(entry.th32ProcessID, out var sessionId) ? (int)sessionId : 0;
        return new ProcessInfo((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, session, TryGetImagePath(process) ?? string.Empty, ticks);
    }
}
