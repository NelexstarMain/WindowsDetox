using System.Security.AccessControl;
using System.Security.Principal;
using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class DataRootSecurityTests
{
    [Theory]
    [InlineData(DataRootSecurity.RootSddl)]
    [InlineData(DataRootSecurity.PluginsSddl)]
    public void Both_dacls_are_protected_and_give_system_and_administrators_full_control(string sddl)
    {
        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;OICI;FA;;;SY)", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;OICI;FA;;;BA)", sddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DataRootSecurity.RootSddl)]
    [InlineData(DataRootSecurity.PluginsSddl)]
    public void Users_never_get_a_right_that_modifies_anything(string sddl) =>
        Assert.True(DaclInspector.GrantsNothingDangerousToUnprivileged(sddl, DaclInspector.FileModify));

    [Fact]
    public void Users_can_traverse_the_root_but_nothing_inside_it_inherits_that()
    {
        Assert.Contains("(A;;0x1200A9;;;BU)", DataRootSecurity.RootSddl, StringComparison.Ordinal); // no OI/CI: this directory only
        Assert.DoesNotContain(";OICI;0x1200A9;;;BU", DataRootSecurity.RootSddl, StringComparison.Ordinal);
        Assert.Contains("(A;OICI;0x1200A9;;;BU)", DataRootSecurity.PluginsSddl, StringComparison.Ordinal); // the plugin host reads plugin sources
    }

    /// <summary>The effective result on a real NTFS directory: protected, SYSTEM and Administrators full, Users only read/execute on the plugins subtree.</summary>
    [Trait("Category", "WindowsOnly")]
    [Fact]
    public void Protect_applies_the_descriptors_to_a_real_directory_and_migrates_existing_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "WindowsDetoxTests-" + Guid.NewGuid().ToString("N"), "WindowsDetox");
        string plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(root);
        string existing = Path.Combine(root, "journal.bin");
        File.WriteAllText(existing, "x");
        try
        {
            DataRootDirectory.Protect(root, plugins);
            DataRootDirectory.Protect(root, plugins); // idempotent

            DirectorySecurity rootSecurity = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access);
            DirectorySecurity pluginsSecurity = new DirectoryInfo(plugins).GetAccessControl(AccessControlSections.Access);
            Assert.True(rootSecurity.AreAccessRulesProtected);
            Assert.True(pluginsSecurity.AreAccessRulesProtected);
            Assert.DoesNotContain(UsersRules(rootSecurity), rule => rule.InheritanceFlags != InheritanceFlags.None);
            Assert.All(UsersRules(rootSecurity).Concat(UsersRules(pluginsSecurity)), rule => Assert.Equal(0, (int)rule.FileSystemRights & (int)FileSystemRights.Write));

            // A file created before the DACL existed no longer carries any Users rule: the sealed state is unreadable to them.
            Assert.Empty(UsersRules(new FileInfo(existing).GetAccessControl(AccessControlSections.Access)));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    private static IEnumerable<FileSystemAccessRule> UsersRules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference == new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
}
