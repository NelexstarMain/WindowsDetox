using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Blocker.Core.Supervision;
using Blocker.Platform.Windows.Changes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Services;

namespace Blocker.Platform.Windows.Supervision;

/// <summary>
/// <see cref="IServiceControl"/> over the SCM API (OpenSCManagerW, OpenServiceW, QueryServiceStatusEx, StartServiceW; never sc.exe).
/// Opens and closes handles per call, so it holds nothing between calls and survives the SCM being restarted.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class ScmServiceControl : IServiceControl
{
    /// <inheritdoc/>
    public ServiceRunState QueryStatus(string serviceName)
    {
        using CloseServiceHandleSafeHandle scm = ScmServiceSecurity.OpenScm();
        using CloseServiceHandleSafeHandle? service = ScmServiceSecurity.OpenService(scm, ChangeGuard.RequireServiceName(serviceName, nameof(serviceName)), ServiceAccess.QueryStatus);
        return service is null ? ServiceRunState.NotInstalled : Map(Query(service).dwCurrentState);
    }

    /// <inheritdoc/>
    public void Start(string serviceName)
    {
        using CloseServiceHandleSafeHandle scm = ScmServiceSecurity.OpenScm();
        using CloseServiceHandleSafeHandle service = ScmServiceSecurity.OpenService(scm, ChangeGuard.RequireServiceName(serviceName, nameof(serviceName)), ServiceAccess.Start)
            ?? throw new InvalidOperationException($"Service '{serviceName}' does not exist.");
        if (!PInvoke.StartService(service, default) && Marshal.GetLastPInvokeError() != (int)WIN32_ERROR.ERROR_SERVICE_ALREADY_RUNNING)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "StartService failed");
        }
    }

    /// <inheritdoc/>
    public int? GetProcessId(string serviceName)
    {
        using CloseServiceHandleSafeHandle scm = ScmServiceSecurity.OpenScm();
        using CloseServiceHandleSafeHandle? service = ScmServiceSecurity.OpenService(scm, ChangeGuard.RequireServiceName(serviceName, nameof(serviceName)), ServiceAccess.QueryStatus);
        return service is not null && Query(service).dwProcessId is > 0 and var pid ? (int)pid : null;
    }

    private static ServiceRunState Map(SERVICE_STATUS_CURRENT_STATE state) => state switch
    {
        SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING => ServiceRunState.Running,
        SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED => ServiceRunState.Stopped,
        SERVICE_STATUS_CURRENT_STATE.SERVICE_START_PENDING => ServiceRunState.StartPending,
        _ => ServiceRunState.Other,
    };

    private static SERVICE_STATUS_PROCESS Query(CloseServiceHandleSafeHandle service)
    {
        SERVICE_STATUS_PROCESS status = default;
        Span<byte> buffer = MemoryMarshal.AsBytes(new Span<SERVICE_STATUS_PROCESS>(ref status));
        return PInvoke.QueryServiceStatusEx(service, SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO, buffer, out _)
            ? status
            : throw ChangeGuard.LastError("QueryServiceStatusEx");
    }
}
