using System.IO.Pipes;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;
using System.Security.AccessControl;
using System.Security.Principal;
using Blocker.Core.Ipc;

namespace Blocker.Platform.Windows.Ipc;

/// <summary>Which security descriptor a pipe gets.</summary>
public enum SecurePipeProfile
{
    /// <summary>Main pipe: SYSTEM full control; Administrators and Authenticated Users may only read and write data.</summary>
    Standard,

    /// <summary>
    /// Dedicated plugin-host pipe: like <see cref="Standard"/> plus access for restricted tokens and AppContainers and a Low
    /// mandatory label, so a low-integrity process can connect here and nowhere else.
    /// </summary>
    PluginHost,
}

/// <summary>
/// Creates named-pipe instances with an explicit DACL.
/// <list type="bullet">
/// <item>SYSTEM gets full control (it is the only identity that may create further instances).</item>
/// <item>Administrators and Authenticated Users get read/write <em>data</em> access plus SYNCHRONIZE, never <c>CreateNewInstance</c>
/// (<c>FILE_CREATE_PIPE_INSTANCE</c>, which <c>GENERIC_WRITE</c> would imply), so nobody else can add an instance to the pipe.
/// <see cref="IpcClient"/> opens the pipe with exactly these rights.</item>
/// <item>The first instance is created with <see cref="PipeOptions.FirstPipeInstance"/>: creation fails if another process already owns the
/// name, which prevents pipe squatting.</item>
/// </list>
/// <para>
/// Low integrity: a process below Medium cannot open an unlabelled pipe for writing (default No-Write-Up). The plugin-host pipe
/// therefore carries the label <c>S:(ML;;NW;;;LW)</c> (mandatory label ACE, policy No-Write-Up, Low integrity level). Its DACL also
/// names RESTRICTED (<c>RC</c>, for restricted tokens, whose access check runs twice) and ALL APPLICATION PACKAGES
/// (<c>AC</c>, for AppContainer). The label is applied by SDDL because <see cref="PipeSecurity"/> has no typed API for it.
/// </para>
/// </summary>
public sealed class SecurePipeListenerFactory(SecurePipeProfile profile = SecurePipeProfile.Standard) : IPipeListenerFactory
{
    private const PipeAccessRights ClientRights =
        PipeAccessRights.ReadData
        | PipeAccessRights.WriteData
        | PipeAccessRights.ReadAttributes
        | PipeAccessRights.ReadExtendedAttributes
        | PipeAccessRights.ReadPermissions
        | PipeAccessRights.Synchronize;

    // SY: SYSTEM gets FA (FILE_ALL_ACCESS: specific rights incl. FILE_CREATE_PIPE_INSTANCE and WRITE_OWNER). Not GA: a generic right in an
    // ACE given this way is not mapped for the pipe, so later instances were refused WRITE_OWNER (seen on Windows CI). BA/AU/RC/AC: 0x12008b = READ_CONTROL | SYNCHRONIZE | FILE_READ_ATTRIBUTES | FILE_READ_EA | FILE_WRITE_DATA | FILE_READ_DATA.
    // The label S:(ML;;NW;;;LW) makes the pipe writable from Low integrity.
    private const string PluginHostSddl =
        "D:P(A;;FA;;;SY)(A;;0x12008b;;;BA)(A;;0x12008b;;;AU)(A;;0x12008b;;;RC)(A;;0x12008b;;;AC)";

    private const string LowLabelSddl = "S:(ML;;NW;;;LW)";

    /// <inheritdoc/>
    public NamedPipeServerStream Create(string pipeName, bool isFirstInstance)
    {
        var options = PipeOptions.Asynchronous;
        if (isFirstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            BuildSecurity(profile),
            HandleInheritability.None,
            // Changing the label needs WRITE_OWNER on our handle. Only the first instance of a pipe gets it implicitly as the creator;
            // later instances are refused (ACCESS_DENIED) unless it is requested here.
            profile == SecurePipeProfile.PluginHost ? PipeAccessRights.TakeOwnership : 0);
        if (profile == SecurePipeProfile.PluginHost)
        {
            try
            {
                ApplyLowLabel(pipe);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        return pipe;
    }

    /// <summary>
    /// Sets the Low mandatory label with <c>LABEL_SECURITY_INFORMATION</c>, which only needs WRITE_OWNER (the creator has it).
    /// Passing the label inside the creation descriptor makes .NET write a SACL, which needs SeSecurityPrivilege that a
    /// service does not have enabled (ERROR_PRIVILEGE_NOT_HELD).
    /// </summary>
    private static unsafe void ApplyLowLabel(NamedPipeServerStream pipe)
    {
        if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(LowLabelSddl, PInvoke.SDDL_REVISION_1, out PSECURITY_DESCRIPTOR descriptor, out _))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSecurityDescriptorToSecurityDescriptor failed");
        }

        try
        {
            ACL* sacl = null;
            BOOL present, defaulted;
            if (!PInvoke.GetSecurityDescriptorSacl(descriptor, &present, &sacl, &defaulted))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetSecurityDescriptorSacl failed");
            }

            bool added = false;
            WIN32_ERROR error;
            try
            {
                pipe.SafePipeHandle.DangerousAddRef(ref added);
                error = PInvoke.SetSecurityInfo(
                    new HANDLE(pipe.SafePipeHandle.DangerousGetHandle()),
                    SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                    OBJECT_SECURITY_INFORMATION.LABEL_SECURITY_INFORMATION,
                    default,
                    default,
                    null,
                    sacl);
            }
            finally
            {
                if (added)
                {
                    pipe.SafePipeHandle.DangerousRelease();
                }
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                throw new System.ComponentModel.Win32Exception((int)error, "SetSecurityInfo(label) failed");
            }
        }
        finally
        {
            _ = PInvoke.LocalFree(new HLOCAL(descriptor.Value));
        }
    }

    internal static PipeSecurity BuildSecurity(SecurePipeProfile profile)
    {
        var security = new PipeSecurity();
        if (profile == SecurePipeProfile.PluginHost)
        {
            security.SetSecurityDescriptorSddlForm(PluginHostSddl);
            return security;
        }

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), ClientRights, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), ClientRights, AccessControlType.Allow));
        return security;
    }
}
