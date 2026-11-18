using System.Security.AccessControl;
using System.Security.Principal;

namespace Blocker.Platform.Windows.Changes;

/// <summary>DACL transformation (SDDL in, SDDL out) used by the NTFS deny handler.</summary>
internal interface IDenyAclTransform
{
    /// <summary>Adds our ACEs (idempotent).</summary>
    string AddDenyAces(string sddl);

    /// <summary>Removes our deny ACEs; returns the new SDDL and how many were removed.</summary>
    (string Sddl, int Removed) RemoveDenyAces(string sddl);

    /// <summary>True when the DACL contains at least one of our deny ACEs.</summary>
    bool HasDenyAces(string sddl);
}

/// <summary>
/// The real <see cref="IDenyAclTransform"/>, built on the managed <see cref="RawSecurityDescriptor"/>. Those ACL types throw
/// <see cref="PlatformNotSupportedException"/> off Windows, so this class is tested in the Windows-only suite while the
/// handler logic around it is tested cross-platform with a fake transform.
/// </summary>
/// <remarks>
/// SYSTEM's token also contains BUILTIN\Administrators, so a plain deny for Administrators would lock SYSTEM out too.
/// The SYSTEM full-control allow ACE is therefore inserted <b>before</b> the deny ACEs (non-canonical order is honoured by
/// access checks: SYSTEM is granted everything at the first ACE and never reaches the denies).
/// </remarks>
internal sealed class NtfsDenyAcl : IDenyAclTransform
{
    /// <summary>Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership.</summary>
    internal const int DenyMask = (int)(FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);

    /// <summary>Container and object inherit.</summary>
    internal const AceFlags InheritFlags = AceFlags.ContainerInherit | AceFlags.ObjectInherit;

    internal static readonly SecurityIdentifier System = new("S-1-5-18");
    internal static readonly SecurityIdentifier Administrators = new("S-1-5-32-544");
    internal static readonly SecurityIdentifier Users = new("S-1-5-32-545");

    /// <summary>Adds the SYSTEM allow ACE and the Administrators/Users deny ACEs in front of the existing ACEs (idempotent).</summary>
    public string AddDenyAces(string sddl)
    {
        RawSecurityDescriptor descriptor = new(sddl);
        RawAcl existing = descriptor.DiscretionaryAcl
            ?? throw new InvalidOperationException("The directory has a NULL DACL, which is not supported.");

        RawAcl acl = new(existing.Revision, existing.Count + 3);
        acl.InsertAce(0, new CommonAce(InheritFlags, AceQualifier.AccessAllowed, (int)FileSystemRights.FullControl, System, false, null));
        acl.InsertAce(1, new CommonAce(InheritFlags, AceQualifier.AccessDenied, DenyMask, Administrators, false, null));
        acl.InsertAce(2, new CommonAce(InheritFlags, AceQualifier.AccessDenied, DenyMask, Users, false, null));
        foreach (GenericAce ace in existing)
        {
            if (!IsOurDeny(ace) && !IsOurSystemAllow(ace))
            {
                acl.InsertAce(acl.Count, ace);
            }
        }

        descriptor.DiscretionaryAcl = acl;
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    /// <summary>Removes every deny ACE matching our SIDs, rights mask and inheritance exactly; returns the new SDDL and the count.</summary>
    public (string Sddl, int Removed) RemoveDenyAces(string sddl)
    {
        RawSecurityDescriptor descriptor = new(sddl);
        if (descriptor.DiscretionaryAcl is not { } existing)
        {
            return (sddl, 0);
        }

        RawAcl acl = new(existing.Revision, existing.Count);
        int removed = 0;
        foreach (GenericAce ace in existing)
        {
            if (IsOurDeny(ace))
            {
                removed++;
            }
            else
            {
                acl.InsertAce(acl.Count, ace);
            }
        }

        if (removed == 0)
        {
            return (sddl, 0);
        }

        descriptor.DiscretionaryAcl = acl;
        return (descriptor.GetSddlForm(AccessControlSections.Access), removed);
    }

    /// <summary>True when the DACL contains at least one of our deny ACEs.</summary>
    public bool HasDenyAces(string sddl) =>
        new RawSecurityDescriptor(sddl).DiscretionaryAcl is { } acl && acl.OfType<GenericAce>().Any(IsOurDeny);

    private static bool IsOurDeny(GenericAce ace) =>
        ace is CommonAce { AceQualifier: AceQualifier.AccessDenied } common
        && common.AccessMask == DenyMask
        && (common.AceFlags & ~AceFlags.Inherited) == InheritFlags
        && (common.SecurityIdentifier == Administrators || common.SecurityIdentifier == Users);

    private static bool IsOurSystemAllow(GenericAce ace) =>
        ace is CommonAce { AceQualifier: AceQualifier.AccessAllowed } common
        && common.AccessMask == (int)FileSystemRights.FullControl
        && common.AceFlags == InheritFlags
        && common.SecurityIdentifier == System;
}
