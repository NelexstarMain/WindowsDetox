using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Services;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Service access rights used by this assembly (winnt.h / winsvc.h values).</summary>
internal static class ServiceAccess
{
    public const uint QueryStatus = 0x0004;
    public const uint Start = 0x0010;
    public const uint Stop = 0x0020;
    public const uint Delete = 0x00010000;
    public const uint ReadControl = 0x00020000;
    public const uint WriteDac = 0x00040000;
}

/// <summary>Reads and writes service DACLs through the SCM and the SDDL conversion APIs.</summary>
internal sealed unsafe class ScmServiceSecurity : IServiceSecurity
{
    public string? QuerySddl(string serviceName)
    {
        using CloseServiceHandleSafeHandle scm = OpenScm();
        using CloseServiceHandleSafeHandle? service = OpenService(scm, serviceName, ServiceAccess.ReadControl);
        if (service is null)
        {
            return null;
        }

        const uint dacl = (uint)OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION;
        PInvoke.QueryServiceObjectSecurity(service, dacl, default, 0, out uint needed);
        int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (needed == 0 || error != (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
        {
            throw ChangeGuard.LastError("QueryServiceObjectSecurity");
        }

        byte[] buffer = new byte[needed];
        fixed (byte* descriptor = buffer)
        {
            if (!PInvoke.QueryServiceObjectSecurity(service, dacl, new PSECURITY_DESCRIPTOR(descriptor), needed, out _))
            {
                throw ChangeGuard.LastError("QueryServiceObjectSecurity");
            }

            if (!PInvoke.ConvertSecurityDescriptorToStringSecurityDescriptor(
                    new PSECURITY_DESCRIPTOR(descriptor), PInvoke.SDDL_REVISION_1, OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, out PWSTR sddl))
            {
                throw ChangeGuard.LastError("ConvertSecurityDescriptorToStringSecurityDescriptor");
            }

            try
            {
                return sddl.ToString();
            }
            finally
            {
                _ = PInvoke.LocalFree(new HLOCAL(sddl.Value));
            }
        }
    }

    public bool TrySetSddl(string serviceName, string sddl)
    {
        using CloseServiceHandleSafeHandle scm = OpenScm();
        using CloseServiceHandleSafeHandle? service = OpenService(scm, serviceName, ServiceAccess.WriteDac);
        if (service is null)
        {
            return false;
        }

        if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, PInvoke.SDDL_REVISION_1, out PSECURITY_DESCRIPTOR descriptor, out _))
        {
            throw ChangeGuard.LastError("ConvertStringSecurityDescriptorToSecurityDescriptor");
        }

        try
        {
            if (!PInvoke.SetServiceObjectSecurity(service, OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, descriptor))
            {
                throw ChangeGuard.LastError("SetServiceObjectSecurity");
            }
        }
        finally
        {
            _ = PInvoke.LocalFree(new HLOCAL(descriptor.Value));
        }

        return true;
    }

    internal static CloseServiceHandleSafeHandle OpenScm(uint access = PInvoke.SC_MANAGER_CONNECT)
    {
        CloseServiceHandleSafeHandle scm = PInvoke.OpenSCManager(null, null, access);
        if (scm.IsInvalid)
        {
            throw ChangeGuard.LastError("OpenSCManager");
        }

        return scm;
    }

    /// <summary>Opens a service; null when it does not exist.</summary>
    internal static CloseServiceHandleSafeHandle? OpenService(CloseServiceHandleSafeHandle scm, string name, uint access)
    {
        CloseServiceHandleSafeHandle service = PInvoke.OpenService(scm, name, access);
        if (!service.IsInvalid)
        {
            return service;
        }

        int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        service.Dispose();
        return error == (int)WIN32_ERROR.ERROR_SERVICE_DOES_NOT_EXIST
            ? null
            : throw new System.ComponentModel.Win32Exception(error, "OpenService failed");
    }
}
