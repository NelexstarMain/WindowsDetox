using System.ComponentModel;
using System.Runtime.InteropServices;
using Blocker.Core.Ports;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Services;

namespace Blocker.Platform.Windows.Changes;

/// <summary>
/// <see cref="IServiceInstaller"/> over the SCM API (never sc.exe): own-process, auto-start, LocalSystem service with
/// failure actions "restart after 0 ms" x3 (reset period 24 h, also for non-crash failures) and no delayed auto-start.
/// Uninstall stops the service (waiting up to 30 s) before deleting it. Requires administrator rights; not unit-testable
/// off Windows (the pure parts live in <see cref="ServiceCommandLine"/>).
/// </summary>
public sealed unsafe class ScmServiceInstaller(string description = "WindowsDetox focus enforcement service") : IServiceInstaller
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public void InstallOrUpdate(string serviceName, string displayName, string executablePath, string arguments)
    {
        ChangeGuard.RequireServiceName(serviceName, nameof(serviceName));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        string commandLine = ServiceCommandLine.Build(executablePath, arguments);

        using CloseServiceHandleSafeHandle scm = ScmServiceSecurity.OpenScm(PInvoke.SC_MANAGER_CONNECT | PInvoke.SC_MANAGER_CREATE_SERVICE);
        CloseServiceHandleSafeHandle? existing = ScmServiceSecurity.OpenService(scm, serviceName, PInvoke.SERVICE_ALL_ACCESS);
        using CloseServiceHandleSafeHandle service = existing ?? CreateService(scm, serviceName, displayName, commandLine);
        if (existing is not null && !PInvoke.ChangeServiceConfig(
                service,
                ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS,
                SERVICE_START_TYPE.SERVICE_AUTO_START,
                SERVICE_ERROR.SERVICE_ERROR_NORMAL,
                commandLine,
                null,
                null,
                "LocalSystem",
                null,
                displayName))
        {
            throw ChangeGuard.LastError("ChangeServiceConfig");
        }

        ConfigureRecovery(service);
    }

    /// <inheritdoc />
    public void Uninstall(string serviceName)
    {
        ChangeGuard.RequireServiceName(serviceName, nameof(serviceName));
        using CloseServiceHandleSafeHandle scm = ScmServiceSecurity.OpenScm();
        using CloseServiceHandleSafeHandle? service = ScmServiceSecurity.OpenService(
            scm, serviceName, ServiceAccess.Stop | ServiceAccess.QueryStatus | ServiceAccess.Delete);
        if (service is null)
        {
            return; // already gone
        }

        if (!PInvoke.ControlService(service, PInvoke.SERVICE_CONTROL_STOP, out _))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is not ((int)WIN32_ERROR.ERROR_SERVICE_NOT_ACTIVE or (int)WIN32_ERROR.ERROR_SERVICE_CANNOT_ACCEPT_CTRL))
            {
                throw new Win32Exception(error, "ControlService(STOP) failed");
            }
        }

        if (!ServiceCommandLine.WaitUntilStopped(() => QueryState(service), TimeProvider.System, Thread.Sleep, StopTimeout, PollInterval))
        {
            throw new TimeoutException($"Service '{serviceName}' did not stop within {StopTimeout.TotalSeconds:0} seconds.");
        }

        if (!PInvoke.DeleteService(service) && Marshal.GetLastPInvokeError() != (int)WIN32_ERROR.ERROR_SERVICE_MARKED_FOR_DELETE)
        {
            throw ChangeGuard.LastError("DeleteService");
        }
    }

    /// <inheritdoc />
    public void EnsureStarted(string serviceName)
    {
        ChangeGuard.RequireServiceName(serviceName, nameof(serviceName));
        using CloseServiceHandleSafeHandle scm = ScmServiceSecurity.OpenScm();
        using CloseServiceHandleSafeHandle service = ScmServiceSecurity.OpenService(scm, serviceName, ServiceAccess.Start | ServiceAccess.QueryStatus)
            ?? throw new InvalidOperationException($"Service '{serviceName}' does not exist.");

        uint state = QueryState(service);
        if (state is (uint)SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING or (uint)SERVICE_STATUS_CURRENT_STATE.SERVICE_START_PENDING)
        {
            return;
        }

        if (state == (uint)SERVICE_STATUS_CURRENT_STATE.SERVICE_STOP_PENDING
            && !ServiceCommandLine.WaitUntilStopped(() => QueryState(service), TimeProvider.System, Thread.Sleep, StopTimeout, PollInterval))
        {
            throw new TimeoutException($"Service '{serviceName}' is still stopping.");
        }

        if (!PInvoke.StartService(service, default) && Marshal.GetLastPInvokeError() != (int)WIN32_ERROR.ERROR_SERVICE_ALREADY_RUNNING)
        {
            throw ChangeGuard.LastError("StartService");
        }
    }

    private static CloseServiceHandleSafeHandle CreateService(CloseServiceHandleSafeHandle scm, string name, string displayName, string commandLine)
    {
        CloseServiceHandleSafeHandle service = PInvoke.CreateService(
            scm,
            name,
            displayName,
            PInvoke.SERVICE_ALL_ACCESS,
            ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS,
            SERVICE_START_TYPE.SERVICE_AUTO_START,
            SERVICE_ERROR.SERVICE_ERROR_NORMAL,
            commandLine,
            null,
            null,
            null,
            null); // null account = LocalSystem
        if (service.IsInvalid)
        {
            throw ChangeGuard.LastError("CreateService");
        }

        return service;
    }

    private static uint QueryState(CloseServiceHandleSafeHandle service)
    {
        SERVICE_STATUS_PROCESS status = default;
        Span<byte> buffer = MemoryMarshal.AsBytes(new Span<SERVICE_STATUS_PROCESS>(ref status));
        return PInvoke.QueryServiceStatusEx(service, SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO, buffer, out _)
            ? (uint)status.dwCurrentState
            : throw ChangeGuard.LastError("QueryServiceStatusEx");
    }

    private void ConfigureRecovery(CloseServiceHandleSafeHandle service)
    {
        SC_ACTION* actions = stackalloc SC_ACTION[3];
        for (int i = 0; i < 3; i++)
        {
            actions[i] = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = 0 };
        }

        SERVICE_FAILURE_ACTIONSW failure = default;
        failure.dwResetPeriod = 86400;
        failure.cActions = 3;
        failure.lpsaActions = actions;
        Set(service, SERVICE_CONFIG.SERVICE_CONFIG_FAILURE_ACTIONS, &failure);

        SERVICE_FAILURE_ACTIONS_FLAG flag = default;
        flag.fFailureActionsOnNonCrashFailures = true;
        Set(service, SERVICE_CONFIG.SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, &flag);

        SERVICE_DELAYED_AUTO_START_INFO delayed = default;
        delayed.fDelayedAutostart = false;
        Set(service, SERVICE_CONFIG.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, &delayed);

        fixed (char* text = description)
        {
            SERVICE_DESCRIPTIONW desc = default;
            desc.lpDescription = new PWSTR(text);
            Set(service, SERVICE_CONFIG.SERVICE_CONFIG_DESCRIPTION, &desc);
        }
    }

    private static void Set(CloseServiceHandleSafeHandle service, SERVICE_CONFIG level, void* info)
    {
        if (!PInvoke.ChangeServiceConfig2W(service, level, info))
        {
            throw ChangeGuard.LastError($"ChangeServiceConfig2W({level})");
        }
    }
}
