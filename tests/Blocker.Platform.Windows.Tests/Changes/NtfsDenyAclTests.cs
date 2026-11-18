using System.Security.AccessControl;
using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

/// <summary>The real ACL transform; <see cref="RawSecurityDescriptor"/> only works on Windows, so this runs in the Windows CI job.</summary>
[Trait("Category", "WindowsOnly")]
public sealed class NtfsDenyAclTests
{
    private const string OriginalSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    private readonly NtfsDenyAcl _acl = new();

    private static List<(AceQualifier Qualifier, string Sid, int Mask, AceFlags Flags)> Aces(string sddl) =>
        [.. new RawSecurityDescriptor(sddl).DiscretionaryAcl!.Cast<CommonAce>()
            .Select(a => (a.AceQualifier, a.SecurityIdentifier.Value, a.AccessMask, a.AceFlags))];

    [Fact]
    public void Deny_mask_is_write_delete_deletechild_changepermissions_takeownership() =>
        Assert.Equal(0xD0156, NtfsDenyAcl.DenyMask);

    [Fact]
    public void AddDenyAces_puts_system_allow_first_then_denies_then_the_original_aces()
    {
        List<(AceQualifier Qualifier, string Sid, int Mask, AceFlags Flags)> aces = Aces(_acl.AddDenyAces(OriginalSddl));

        const AceFlags inherit = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
        Assert.Equal(5, aces.Count); // the original explicit SYSTEM FullControl ACE is replaced by ours, not duplicated
        Assert.Equal((AceQualifier.AccessAllowed, "S-1-5-18", (int)FileSystemRights.FullControl, inherit), aces[0]);
        Assert.Equal((AceQualifier.AccessDenied, "S-1-5-32-544", NtfsDenyAcl.DenyMask, inherit), aces[1]);
        Assert.Equal((AceQualifier.AccessDenied, "S-1-5-32-545", NtfsDenyAcl.DenyMask, inherit), aces[2]);
        Assert.Equal("S-1-5-32-544", aces[3].Sid); // original Administrators allow survives, after the denies
        Assert.Equal(0x1200a9, aces[4].Mask);
    }

    [Fact]
    public void AddDenyAces_is_idempotent() =>
        Assert.Equal(Aces(_acl.AddDenyAces(OriginalSddl)), Aces(_acl.AddDenyAces(_acl.AddDenyAces(OriginalSddl))));

    [Fact]
    public void RemoveDenyAces_restores_the_original_aces_except_the_system_allow()
    {
        string protectedSddl = _acl.AddDenyAces(OriginalSddl);

        (string sddl, int removed) = _acl.RemoveDenyAces(protectedSddl);

        Assert.Equal(2, removed);
        Assert.False(_acl.HasDenyAces(sddl));
        Assert.True(_acl.HasDenyAces(protectedSddl));
        Assert.Equal(Aces(OriginalSddl).Count, Aces(sddl).Count); // our SYSTEM allow stands in for the original identical one
        Assert.Equal((OriginalSddl, 0), _acl.RemoveDenyAces(OriginalSddl));
    }

    [Fact]
    public void Only_exact_matches_count_as_our_deny_aces()
    {
        // Same SID but a different mask, and our mask for a foreign SID: neither may be swept.
        string foreign = "D:(D;OICI;0x10000;;;BA)(D;OICI;0xd0156;;;S-1-1-0)(D;;0xd0156;;;BU)";

        Assert.False(_acl.HasDenyAces(foreign));
        Assert.Equal((foreign, 0), _acl.RemoveDenyAces(foreign));
    }
}
