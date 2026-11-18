using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Blocker.Platform.Windows.Processes;

/// <summary>Token acquisition and CreateProcessAsUserW plumbing shared by the user-session and restricted launchers.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal static unsafe class TokenProcess
{
    /// <summary>Interactive desktop of the logged-on user.</summary>
    public const string InteractiveDesktop = @"winsta0\default";

    private const uint NoActiveSession = 0xFFFFFFFF;
    private const int ErrorNoToken = 1008;

    /// <summary>Primary token of the active console user, or null when nobody is logged on. Caller must be SYSTEM (SeTcbPrivilege).</summary>
    public static SafeFileHandle? QueryConsoleUserToken()
    {
        var session = PInvoke.WTSGetActiveConsoleSessionId();
        if (session == NoActiveSession)
        {
            return null;
        }

        HANDLE raw = default;
        if (!PInvoke.WTSQueryUserToken(session, ref raw))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNoToken ? null : throw new Win32Exception(error);
        }

        using var queried = new SafeFileHandle((IntPtr)raw.Value, ownsHandle: true);
        return DuplicatePrimary(queried);
    }

    /// <summary>Primary copy of this process' own token.</summary>
    public static SafeFileHandle DuplicateOwnToken()
    {
        using var self = new SafeFileHandle((IntPtr)PInvoke.GetCurrentProcess().Value, ownsHandle: false);
        if (!PInvoke.OpenProcessToken(self, TOKEN_ACCESS_MASK.TOKEN_DUPLICATE | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var own))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (own)
        {
            return DuplicatePrimary(own);
        }
    }

    /// <summary>Duplicates a token as a primary token with full access (needed for CreateProcessAsUser and for lowering the integrity level).</summary>
    public static SafeFileHandle DuplicatePrimary(SafeHandle token) =>
        PInvoke.DuplicateTokenEx(token, TOKEN_ACCESS_MASK.TOKEN_ALL_ACCESS, null, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out var primary)
            ? primary
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    /// <summary>Starts a process with <paramref name="token"/> and the token's environment block.</summary>
    /// <param name="desktop">Window station and desktop, or null to inherit the caller's.</param>
    public static StartedProcess Start(SafeHandle token, string executablePath, string arguments, PROCESS_CREATION_FLAGS flags, string? desktop)
    {
        if (!PInvoke.CreateEnvironmentBlock(out var environment, token, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            Span<char> commandLine = (CommandLine.Build(executablePath, arguments) + '\0').ToCharArray();
            fixed (char* desktopName = desktop)
            {
                var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW), lpDesktop = desktopName };
                if (!PInvoke.CreateProcessAsUser(
                        token, executablePath, ref commandLine, null, null, false, flags | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                        environment, Path.GetDirectoryName(executablePath), in startup, out var info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return new StartedProcess(
                    new SafeFileHandle((IntPtr)info.hProcess.Value, ownsHandle: true),
                    new SafeFileHandle((IntPtr)info.hThread.Value, ownsHandle: true),
                    (int)info.dwProcessId);
            }
        }
        finally
        {
            PInvoke.DestroyEnvironmentBlock(environment);
        }
    }
}

/// <summary>A freshly created process and its primary thread (owned handles).</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class StartedProcess(SafeFileHandle process, SafeFileHandle thread, int processId) : IDisposable
{
    /// <summary>Process handle.</summary>
    public SafeFileHandle Process { get; } = process;

    /// <summary>Primary thread handle.</summary>
    public SafeFileHandle Thread { get; } = thread;

    /// <summary>Process id.</summary>
    public int ProcessId { get; } = processId;

    /// <summary>Resumes a process created with CREATE_SUSPENDED.</summary>
    public void Resume()
    {
        if (PInvoke.ResumeThread(Thread) == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Closes both handles (the process keeps running).</summary>
    public void Dispose()
    {
        Thread.Dispose();
        Process.Dispose();
    }
}
