using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Blocker.SmokeTest;

/// <summary>
/// An IFEO image key the service cannot write: SYSTEM and Administrators get read access only and the DACL is protected, which is what a key
/// owned by TrustedInstaller or guarded by security software looks like to the service (ACCESS_DENIED on the write). It reproduces the failure
/// seen on a real PC, where one refused IFEO write used to roll the whole session back. The owner stays the elevated creator (assigning
/// TrustedInstaller needs SeRestorePrivilege), which is enough: the DACL alone denies <c>KEY_SET_VALUE</c> to everyone but nobody is allowed it.
/// </summary>
internal sealed class LockedIfeoKey
{
    public const string ImageName = "smoke-locked.exe";

    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Creates the key (64-bit view, the one the service writes first) with the read-only DACL.</summary>
    public void Create()
    {
        Remove(); // a leftover of a crashed run is locked too

        RegistrySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in new[] { LocalSystem, Administrators })
        {
            security.AddAccessRule(new RegistryAccessRule(sid, RegistryRights.ReadKey, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        }

        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey ifeo = root.OpenSubKey(IfeoPath, writable: true) ?? throw new InvalidOperationException($"HKLM\\{IfeoPath} does not exist");
        using RegistryKey created = ifeo.CreateSubKey(ImageName, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None, security);
    }

    /// <summary>True when this (administrator) process is itself refused write access, proving the lock took effect.</summary>
    public bool RefusesWrites(out string detail)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? ifeo = root.OpenSubKey(IfeoPath);
        try
        {
            using RegistryKey? key = ifeo?.OpenSubKey(ImageName, writable: true);
            detail = key is null ? "the key does not exist" : "opening the key for writing succeeded";
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            detail = $"opening it for writing failed with {ex.GetType().Name}";
            return true;
        }
    }

    /// <summary>Restores a normal DACL first (the owner may always change it), then deletes the key; a missing key is fine.</summary>
    public void Remove()
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? ifeo = root.OpenSubKey(IfeoPath, writable: true);
        if (ifeo is null)
        {
            return;
        }

        using (RegistryKey? locked = ifeo.OpenSubKey(ImageName, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey))
        {
            if (locked is null)
            {
                return;
            }

            RegistrySecurity open = new();
            open.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            open.AddAccessRule(new RegistryAccessRule(Administrators, RegistryRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            open.AddAccessRule(new RegistryAccessRule(LocalSystem, RegistryRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            locked.SetAccessControl(open);
        }

        ifeo.DeleteSubKeyTree(ImageName, throwOnMissingSubKey: false);
    }
}
