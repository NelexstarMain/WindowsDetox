using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Blocker.SmokeTest;

/// <summary>A process in the ancestry of this tool.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="ImagePath">Executable path, or <see langword="null"/> when it cannot be read.</param>
internal sealed record Ancestor(int ProcessId, string? ImagePath);

/// <summary>
/// Finds the processes this tool descends from (shell, CI runner...). The session kills every process that is neither allowlisted nor
/// inherited from an allowlisted parent, so the chain above this tool must be allowlisted or the CI step dies with its first kill.
/// </summary>
internal static partial class ProcessAncestry
{
    private const uint SnapshotProcess = 2;
    private const int MaxDepth = 16;

    public static IReadOnlyList<Ancestor> OfCurrentProcess()
    {
        Dictionary<int, int> parentOf = SnapshotParents();
        List<Ancestor> chain = [];
        HashSet<int> seen = [Environment.ProcessId];
        int current = Environment.ProcessId;
        while (chain.Count < MaxDepth && parentOf.TryGetValue(current, out int parent) && parent > 0 && seen.Add(parent))
        {
            if (!parentOf.ContainsKey(parent))
            {
                break; // the parent already exited: its pid may be reused, so the chain ends here
            }

            chain.Add(new Ancestor(parent, TryGetImagePath(parent)));
            current = parent;
        }

        return chain;
    }

    private static string? TryGetImagePath(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return null; // protected or exited process
        }
    }

    private static unsafe Dictionary<int, int> SnapshotParents()
    {
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == -1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateToolhelp32Snapshot failed");
        }

        try
        {
            Dictionary<int, int> parents = [];
            ProcessEntry entry = default;
            entry.Size = (uint)sizeof(ProcessEntry);
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }

            return parents;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
