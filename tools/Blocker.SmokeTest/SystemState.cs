using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Blocker.SmokeTest;

/// <summary>One <c>Image File Execution Options</c> image key as the service leaves it.</summary>
internal sealed record IfeoEntry(string View, string Image, string? Debugger, string? Owner);

/// <summary>
/// Read-only views of the machine state the service changes during a session. Everything is read without starting any external program
/// (cmd, reg, sc, icacls and the like are blocked while a session runs), and DACLs are read as raw bytes: the managed
/// <c>DirectorySecurity</c> canonicalises a non-canonical DACL on load and would hide exactly the ACE order that matters here.
/// </summary>
internal static partial class SystemState
{
    /// <summary>The deny mask the service puts on the protected directories (same expression as its NTFS deny handler).</summary>
    internal const int DenyMask = (int)(FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);

    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const uint DaclSecurityInformation = 4;
    private const int ServiceStopRight = 0x20;
    private const int FileWriteRights = 0x6; // FILE_WRITE_DATA | FILE_APPEND_DATA

    private static readonly SecurityIdentifier System = new("S-1-5-18");
    private static readonly SecurityIdentifier Administrators = new("S-1-5-32-544");
    private static readonly SecurityIdentifier Users = new("S-1-5-32-545");
    private static readonly SecurityIdentifier Everyone = new("S-1-1-0");
    private static readonly SecurityIdentifier AuthenticatedUsers = new("S-1-5-11");

    /// <summary>Reads the DACL of a directory exactly as stored.</summary>
    public static unsafe RawSecurityDescriptor ReadDirectoryDacl(string path)
    {
        _ = GetFileSecurity(path, DaclSecurityInformation, null, 0, out uint needed);
        if (needed == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Reading the DACL size of '{path}' failed");
        }

        byte[] buffer = new byte[needed];
        fixed (byte* pointer = buffer)
        {
            if (!GetFileSecurity(path, DaclSecurityInformation, pointer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Reading the DACL of '{path}' failed");
            }
        }

        return new RawSecurityDescriptor(buffer, 0);
    }

    public static string Sddl(RawSecurityDescriptor descriptor) => descriptor.GetSddlForm(AccessControlSections.Access);

    /// <summary>Counts the deny ACEs the service's NTFS handler would recognise as its own.</summary>
    public static int CountServiceDenyAces(RawSecurityDescriptor descriptor) =>
        descriptor.DiscretionaryAcl?.OfType<CommonAce>().Count(static ace =>
            ace.AceQualifier == AceQualifier.AccessDenied
            && ace.AccessMask == DenyMask
            && (ace.SecurityIdentifier == Administrators || ace.SecurityIdentifier == Users)) ?? 0;

    /// <summary>
    /// Walks the DACL in stored order the way the access check does for a SYSTEM token (user SYSTEM plus the groups Administrators, Everyone and
    /// Authenticated Users) asking for "create file / write data". The first deny that hits a wanted bit wins, even over a later allow.
    /// </summary>
    public static (bool Allowed, string Detail) EvaluateSystemWrite(RawSecurityDescriptor descriptor)
    {
        SecurityIdentifier[] token = [System, Administrators, Everyone, AuthenticatedUsers];
        int granted = 0;
        int position = 0;
        foreach (GenericAce generic in descriptor.DiscretionaryAcl ?? new RawAcl(2, 0))
        {
            position++;
            if (generic is not CommonAce ace || (ace.AceFlags & AceFlags.InheritOnly) != 0 || !token.Contains(ace.SecurityIdentifier))
            {
                continue;
            }

            if (ace.AceQualifier == AceQualifier.AccessDenied && (ace.AccessMask & FileWriteRights & ~granted) != 0)
            {
                return (false, $"denied by ACE #{position} ({Describe(ace.SecurityIdentifier)} deny 0x{ace.AccessMask:X}) before any allow granted the write right");
            }

            if (ace.AceQualifier == AceQualifier.AccessAllowed)
            {
                granted |= ace.AccessMask;
            }

            if ((granted & FileWriteRights) == FileWriteRights)
            {
                return (true, $"granted by ACE #{position} ({Describe(ace.SecurityIdentifier)} allow 0x{ace.AccessMask:X})");
            }
        }

        return (false, "no ACE grants the write right");
    }

    /// <summary>Reads the stored service security descriptor; <see langword="null"/> when the service has no explicit one (SCM default).</summary>
    public static RawSecurityDescriptor? ReadServiceSecurity(string serviceName)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}\Security");
        return key?.GetValue("Security") is byte[] bytes ? new RawSecurityDescriptor(bytes, 0) : null;
    }

    /// <summary>True when Administrators may stop the service (the normal state; the session DACL removes the right).</summary>
    public static bool AdministratorsMayStop(RawSecurityDescriptor? descriptor)
    {
        if (descriptor?.DiscretionaryAcl is not { } acl)
        {
            return true;
        }

        int granted = acl.OfType<CommonAce>()
            .Where(static ace => ace.AceQualifier == AceQualifier.AccessAllowed && ace.SecurityIdentifier == Administrators)
            .Aggregate(0, static (mask, ace) => mask | ace.AccessMask);
        return (granted & ServiceStopRight) != 0;
    }

    public static IReadOnlyList<IfeoEntry> ReadIfeo()
    {
        List<IfeoEntry> entries = [];
        foreach ((RegistryView view, string label) in new[] { (RegistryView.Registry64, "64-bit"), (RegistryView.Registry32, "32-bit") })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? ifeo = root.OpenSubKey(IfeoPath);
            if (ifeo is null)
            {
                continue;
            }

            foreach (string image in ifeo.GetSubKeyNames())
            {
                using RegistryKey? key = ifeo.OpenSubKey(image);
                if (key is not null)
                {
                    entries.Add(new IfeoEntry(
                        label,
                        image,
                        key.GetValue("Debugger", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                        key.GetValue("BlockerOwned") as string));
                }
            }
        }

        return entries;
    }

    /// <summary>The Edge policy the service writes to route browsers through its PAC proxy; <see langword="null"/> when absent.</summary>
    public static string? ReadEdgeProxySettings()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge");
        return key?.GetValue("ProxySettings") as string;
    }

    /// <summary>Reads the newest <c>service-*.log</c> (the service keeps it open, so it is opened with full sharing).</summary>
    /// <returns>The file path and its lines; the path is <see langword="null"/> when the directory or the log does not exist.</returns>
    public static (string? Path, IReadOnlyList<string> Lines) ReadNewestLog(string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return (null, []);
        }

        FileInfo? newest = new DirectoryInfo(logsDirectory).GetFiles("service-*.log").OrderByDescending(static f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null)
        {
            return (null, []);
        }

        List<string> lines = [];
        using FileStream stream = new(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return (newest.FullName, lines);
    }

    private static string Describe(SecurityIdentifier sid) =>
        sid == System ? "SYSTEM" : sid == Administrators ? "BUILTIN\\Administrators" : sid == Users ? "BUILTIN\\Users" : sid.Value;

    [LibraryImport("advapi32.dll", EntryPoint = "GetFileSecurityW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileSecurity(string fileName, uint requestedInformation, byte* securityDescriptor, uint length, out uint lengthNeeded);
}
