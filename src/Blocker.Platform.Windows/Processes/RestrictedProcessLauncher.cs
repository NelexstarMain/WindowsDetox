using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Blocker.Core.Ports;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// Launches the plugin host with a token that cannot do much damage, inside a kill-on-close job object.
/// </summary>
/// <remarks>
/// <para>Threat model: plugin code is compiled and executed in this process, so it must be assumed hostile. The process runs with
/// (1) the console user's token (fallback when nobody is logged on: this service's own token) restricted by CreateRestrictedToken with
/// DISABLE_MAX_PRIVILEGE and the Administrators group set deny-only, (2) low integrity level (S-1-16-4096): mandatory integrity
/// policy blocks writes to every object with a higher label, i.e. user profile, Program Files, HKLM/HKCU and other processes,
/// and (3) a job object: one process only (no child processes), 512 MB per process, UI restrictions (no clipboard, desktop switching,
/// foreign USER handles, system parameters, exit-windows) and kill-on-job-close so the host cannot outlive its handle.</para>
/// <para>Not covered: this is not an AppContainer, so low-IL code can still read what Everyone/Users can read and use the network.
/// Network and file capabilities are mediated by the plugin API policy, not by this token.</para>
/// <para>Service integration notes: the named pipe the host connects to must grant access to the Low integrity level
/// (SACL label <c>S:(ML;;NW;;;LW)</c>) or the connect fails; the host executable and its folder must be readable by Low IL;
/// disposing the returned <see cref="IRestrictedProcess"/> kills the host.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class RestrictedProcessLauncher : IRestrictedProcessLauncher
{
    /// <summary>Per-process memory limit of the job.</summary>
    public const long ProcessMemoryLimitBytes = 512L * 1024 * 1024;

    private const string LowIntegritySid = "S-1-16-4096";

    /// <inheritdoc/>
    public IRestrictedProcess Launch(string executablePath, string arguments)
    {
        var stage = "start";
        try
        {
            return LaunchCore(executablePath, arguments, name => stage = name);
        }
        catch (Win32Exception ex)
        {
            // The bare Win32 message ("Access is denied") does not say which of the steps failed.
            throw new Win32Exception(ex.NativeErrorCode, $"Starting the restricted process failed at step '{stage}' for '{executablePath}': {ex.Message}");
        }
    }

    private static RestrictedProcess LaunchCore(string executablePath, string arguments, Action<string> stage)
    {
        stage("query the console user token");
        var userToken = TokenProcess.QueryConsoleUserToken();
        var inUserSession = userToken is not null;
        stage(inUserSession ? "use the console user token" : "duplicate the service token");
        using var baseToken = userToken ?? TokenProcess.DuplicateOwnToken();
        stage("create the restricted token");
        using var restricted = CreateRestrictedToken(baseToken);
        stage("lower the integrity level");
        SetLowIntegrity(restricted);

        stage("create the job object");
        var job = CreateJob();
        StartedProcess? started = null;
        try
        {
            stage("create the process (CreateProcessAsUser)");
            started = TokenProcess.Start(
                restricted, executablePath, arguments, PROCESS_CREATION_FLAGS.CREATE_SUSPENDED, inUserSession ? TokenProcess.InteractiveDesktop : null);

            // Suspended until it is inside the job, so it can never run (or spawn children) unconfined.
            stage("assign the process to the job");
            if (!PInvoke.AssignProcessToJobObject(job, started.Process))
            {
                var error = Marshal.GetLastWin32Error();
                PInvoke.TerminateProcess(started.Process, 1);
                throw new Win32Exception(error);
            }

            stage("apply the UI restrictions");
            _ = TryApplyUiRestrictions(job);

            stage("resume the process");
            started.Resume();
            started.Thread.Dispose();
            return new RestrictedProcess(job, started.Process, started.ProcessId);
        }
        catch
        {
            started?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private static unsafe SafeFileHandle CreateRestrictedToken(SafeHandle token)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        var adminBytes = new byte[admins.BinaryLength];
        admins.GetBinaryForm(adminBytes, 0);

        // CreateRestrictedToken rejects SIDs that are not in the token, so only deny groups that are present.
        var denyAdmins = identity.Groups?.Contains(admins) == true;
        fixed (byte* sid = adminBytes)
        {
            Span<SID_AND_ATTRIBUTES> disable = denyAdmins ? [new SID_AND_ATTRIBUTES { Sid = new PSID(sid) }] : [];
            return PInvoke.CreateRestrictedToken(token, CREATE_RESTRICTED_TOKEN_FLAGS.DISABLE_MAX_PRIVILEGE, disable, default, default, out var result)
                ? result
                : throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static unsafe void SetLowIntegrity(SafeHandle token)
    {
        var sid = new SecurityIdentifier(LowIntegritySid);
        var labelSize = sizeof(TOKEN_MANDATORY_LABEL);

        // TOKEN_MANDATORY_LABEL must be followed by its SID in the same buffer.
        var buffer = new byte[labelSize + sid.BinaryLength];
        sid.GetBinaryForm(buffer, labelSize);
        fixed (byte* start = buffer)
        {
            var label = (TOKEN_MANDATORY_LABEL*)start;
            label->Label.Sid = new PSID(start + labelSize);
            label->Label.Attributes = (uint)PInvoke.SE_GROUP_INTEGRITY;
            if (!PInvoke.SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    private static unsafe SafeFileHandle CreateJob()
    {
        var job = PInvoke.CreateJobObject(null, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { ProcessMemoryLimit = (nuint)ProcessMemoryLimitBytes };
            limits.BasicLimitInformation.LimitFlags =
                JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            limits.BasicLimitInformation.ActiveProcessLimit = 1;
            SetInformation(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, new ReadOnlySpan<byte>(&limits, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Adds the UI restrictions (no clipboard, desktop switching, foreign USER handles, system parameters, exit-windows) to a job that
    /// already holds the process. They are applied <em>after</em> the assignment because the service runs in session 0 while the host runs
    /// in the user's session: a job that carries UI limits from the start refuses a process from another session with ACCESS_DENIED
    /// (seen on Windows CI). The limits that matter most (one process, 512 MB, kill-on-close) are in place before the process runs.
    /// </summary>
    /// <returns><see langword="true"/> when Windows accepted the restrictions; <see langword="false"/> when it refused them (the host still
    /// runs confined by the low-integrity token and the other job limits).</returns>
    private static unsafe bool TryApplyUiRestrictions(SafeHandle job)
    {
        var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            UIRestrictionsClass = JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_DESKTOP | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_DISPLAYSETTINGS
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_EXITWINDOWS | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_GLOBALATOMS
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_HANDLES | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_READCLIPBOARD
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_WRITECLIPBOARD,
        };
        return PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectBasicUIRestrictions, new ReadOnlySpan<byte>(&ui, sizeof(JOBOBJECT_BASIC_UI_RESTRICTIONS)));
    }

    private static void SetInformation(SafeHandle job, JOBOBJECTINFOCLASS information, ReadOnlySpan<byte> data)
    {
        if (!PInvoke.SetInformationJobObject(job, information, data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>The launched host; disposing closes the job and therefore kills the process.</summary>
    private sealed class RestrictedProcess(SafeFileHandle job, SafeFileHandle process, int processId) : IRestrictedProcess
    {
        private const uint KilledExitCode = 1;
        private const uint StillActive = 259;

        public int ProcessId { get; } = processId;

        public int? ExitCode =>
            !process.IsClosed && PInvoke.GetExitCodeProcess(process, out var code) && code != StillActive ? unchecked((int)code) : null;

        public async Task WaitForExitAsync(CancellationToken cancellationToken) =>
            await HandleWaiter.WaitAsync(process, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

        public void Kill()
        {
            ObjectDisposedException.ThrowIf(job.IsClosed, this);
            if (!PInvoke.TerminateJobObject(job, KilledExitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public void Dispose()
        {
            job.Dispose();
            process.Dispose();
        }
    }
}
