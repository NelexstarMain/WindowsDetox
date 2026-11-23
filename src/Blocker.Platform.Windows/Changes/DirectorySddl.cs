using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Applies a protected DACL (and optionally a mandatory label) from an SDDL string to an existing directory.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal static unsafe class DirectorySddl
{
    /// <summary>Replaces the DACL of <paramref name="path"/> and propagates the inheritable ACEs to existing children; needs an elevated administrator.</summary>
    /// <param name="path">Existing directory.</param>
    /// <param name="sddl">Security descriptor; its DACL is applied as protected, its SACL (the label) only when <paramref name="includeLabel"/>.</param>
    /// <param name="includeLabel">Whether the mandatory integrity label of the SACL is applied too.</param>
    public static void Apply(string path, string sddl, bool includeLabel)
    {
        if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, PInvoke.SDDL_REVISION_1, out PSECURITY_DESCRIPTOR descriptor, out _))
        {
            throw ChangeGuard.LastError("ConvertStringSecurityDescriptorToSecurityDescriptor");
        }

        try
        {
            ACL* dacl = null;
            ACL* sacl = null;
            BOOL present, defaulted;
            if (!PInvoke.GetSecurityDescriptorDacl(descriptor, &present, &dacl, &defaulted)
                || !PInvoke.GetSecurityDescriptorSacl(descriptor, &present, &sacl, &defaulted))
            {
                throw ChangeGuard.LastError("GetSecurityDescriptor");
            }

            OBJECT_SECURITY_INFORMATION info = OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION
                | OBJECT_SECURITY_INFORMATION.PROTECTED_DACL_SECURITY_INFORMATION;
            if (includeLabel)
            {
                info |= OBJECT_SECURITY_INFORMATION.LABEL_SECURITY_INFORMATION;
            }

            WIN32_ERROR error;
            fixed (char* name = path)
            {
                error = PInvoke.SetNamedSecurityInfo(new PWSTR(name), SE_OBJECT_TYPE.SE_FILE_OBJECT, info, default, default, dacl, includeLabel ? sacl : null);
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                throw new System.ComponentModel.Win32Exception((int)error, "SetNamedSecurityInfo failed");
            }
        }
        finally
        {
            _ = PInvoke.LocalFree(new HLOCAL(descriptor.Value));
        }
    }
}
