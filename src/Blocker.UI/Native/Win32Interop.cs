using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Blocker.UI.Native;

/// <summary>
/// The only place in the UI executable that calls the Win32 API. Every call is a plain function: handles are raw <see cref="nint"/>
/// values that the caller closes with <see cref="CloseHandle"/> in a <c>finally</c> block, and no memory is shared with native code
/// beyond the blittable structs and spans passed for the duration of one call.
/// </summary>
internal static unsafe partial class Win32Interop
{
    /// <summary>Foreground-window change event (<c>EVENT_SYSTEM_FOREGROUND</c>).</summary>
    public const uint EventSystemForeground = 0x0003;

    /// <summary>Callback is delivered on the installing thread through its message loop (<c>WINEVENT_OUTOFCONTEXT</c>).</summary>
    public const uint WinEventOutOfContext = 0x0000;

    /// <summary>Asks a window to close (<c>WM_CLOSE</c>).</summary>
    public const uint WmClose = 0x0010;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x0010_0000;
    private const uint WaitTimeout = 0x102;

    [ThreadStatic]
    private static List<nint>? s_enumeratedWindows;

    private static Action<nint>? s_foregroundCallback;

    /// <summary>Gets milliseconds since the last keyboard or mouse input, or <see langword="null"/> when the system refuses to say.</summary>
    /// <returns>Idle time in milliseconds.</returns>
    public static long? GetIdleMilliseconds()
    {
        LastInputInfo info = new() { CbSize = (uint)Unsafe.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        // Both values are 32-bit tick counts that wrap together, so unsigned subtraction is correct.
        return unchecked((uint)Environment.TickCount - info.DwTime);
    }

    /// <summary>
    /// Starts reporting foreground-window changes to <paramref name="callback"/> on the calling thread, which must pump messages
    /// (the WPF UI thread does). Only one hook can be active.
    /// </summary>
    /// <param name="callback">Receives the new foreground window handle. It must not throw.</param>
    /// <returns>The hook handle for <see cref="UnhookForegroundChanges"/>, or zero on failure.</returns>
    public static nint HookForegroundChanges(Action<nint> callback)
    {
        s_foregroundCallback = callback;
        return SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, &OnForegroundChanged, 0, 0, WinEventOutOfContext);
    }

    /// <summary>Stops the hook installed by <see cref="HookForegroundChanges"/>.</summary>
    /// <param name="hook">Hook handle.</param>
    public static void UnhookForegroundChanges(nint hook)
    {
        if (hook != 0)
        {
            _ = UnhookWinEvent(hook);
        }

        s_foregroundCallback = null;
    }

    /// <summary>Returns the handle of the current foreground window, or zero.</summary>
    /// <returns>Window handle.</returns>
    public static nint GetForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>Returns the id of the process that owns <paramref name="window"/>, or zero.</summary>
    /// <param name="window">Window handle.</param>
    /// <returns>Process id.</returns>
    public static uint GetWindowProcessId(nint window)
    {
        _ = GetWindowThreadProcessId(window, out uint processId);
        return processId;
    }

    /// <summary>Reads the creation time (FILETIME ticks) and executable path of a process.</summary>
    /// <param name="processId">Process id.</param>
    /// <param name="startTimeTicks">Creation time in FILETIME ticks.</param>
    /// <param name="imagePath">Executable path, or <see langword="null"/> when it cannot be read.</param>
    /// <returns><see langword="true"/> when the process exists and its creation time could be read.</returns>
    public static bool TryGetProcessInfo(uint processId, out long startTimeTicks, out string? imagePath)
    {
        startTimeTicks = 0;
        imagePath = null;
        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return false;
        }

        try
        {
            if (!GetProcessTimes(handle, out startTimeTicks, out _, out _, out _))
            {
                return false;
            }

            char* buffer = stackalloc char[1024];
            uint length = 1024;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref length))
            {
                imagePath = new string(buffer, 0, (int)length);
            }

            return true;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>Returns whether the process with this id and creation time is still running.</summary>
    /// <param name="processId">Process id.</param>
    /// <param name="startTimeTicks">Creation time in FILETIME ticks; a different value means the id was reused.</param>
    /// <returns><see langword="true"/> while that exact process runs.</returns>
    public static bool IsProcessRunning(uint processId, long startTimeTicks)
    {
        nint handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, processId);
        if (handle == 0)
        {
            return false;
        }

        try
        {
            return GetProcessTimes(handle, out long created, out _, out _, out _)
                && created == startTimeTicks
                && WaitForSingleObject(handle, 0) == WaitTimeout;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>Posts <c>WM_CLOSE</c> to every top-level window of the process.</summary>
    /// <param name="processId">Process id.</param>
    /// <returns>The number of windows that were asked to close.</returns>
    public static int PostCloseToProcessWindows(uint processId)
    {
        s_enumeratedWindows = [];
        try
        {
            _ = EnumWindows(&CollectWindow, (nint)processId);
            int count = 0;
            foreach (nint window in s_enumeratedWindows)
            {
                if (PostMessageW(window, WmClose, 0, 0))
                {
                    count++;
                }
            }

            return count;
        }
        finally
        {
            s_enumeratedWindows = null;
        }
    }

    // Called by Windows on the installing thread. It only forwards to the registered callback, which must not throw:
    // an exception escaping an UnmanagedCallersOnly method terminates the process.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnForegroundChanged(nint hook, uint eventId, nint window, int idObject, int idChild, uint eventThread, uint eventTime) =>
        s_foregroundCallback?.Invoke(window);

    // Called by Windows synchronously inside EnumWindows on the calling thread.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CollectWindow(nint window, nint processId)
    {
        if (GetWindowProcessId(window) == (uint)processId)
        {
            s_enumeratedWindows?.Add(window);
        }

        return 1;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> callback,
        uint idProcess,
        uint idThread,
        uint flags);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }
}
