using System.Security.AccessControl;
using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

/// <summary>NTFS ACL, SCM and WFP calls against the real OS. Fail (not skip) when rights are missing; WFP needs an elevated token.</summary>
[Trait("Category", "WindowsOnly")]
public sealed class SystemIntegrationTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ntfs_deny_aces_are_applied_and_the_original_dacl_is_restored()
    {
        string root = Path.Combine(Path.GetTempPath(), "WindowsDetoxTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var handler = new NtfsDenyWriteChangeHandler([root]);
        try
        {
            string originalSddl = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            string? original = await handler.CaptureOriginalAsync(root, _ct);
            Assert.NotNull(original);

            await handler.ApplyAsync(root, "{}", _ct);
            string applied = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.True(new NtfsDenyAcl().HasDenyAces(applied));

            await handler.RestoreAsync(root, original, _ct);
            await handler.RestoreAsync(root, original, _ct); // idempotent
            string restored = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.False(new NtfsDenyAcl().HasDenyAces(restored));
            // Windows recomputes inherited ACEs on write and marks the DACL auto-inherited ("AI"); the ACEs themselves must match exactly.
            Assert.Equal(WithoutAutoInheritFlag(originalSddl), WithoutAutoInheritFlag(restored));
        }
        finally
        {
            await handler.SweepAsync("test-install", _ct);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Service_dacl_can_be_read_and_missing_services_are_reported_as_null()
    {
        var security = new ScmServiceSecurity();

        string? sddl = security.QuerySddl("EventLog");

        Assert.NotNull(sddl);
        Assert.StartsWith("D:", sddl, StringComparison.Ordinal);
        Assert.Null(security.QuerySddl("blocker-test-no-such-service"));
        Assert.False(security.TrySetSddl("blocker-test-no-such-service", ServiceSddl.BuildRestricted()));
    }

    [Fact]
    [Trait("Category", "RequiresAdmin")]
    public async Task Wfp_dynamic_session_enables_and_disables()
    {
        string exe = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        await using var firewall = new WfpBrowserFirewall();

        await firewall.EnableAsync([exe, @"C:\blocker-test\missing-browser.exe"], _ct); // missing exes are skipped
        Assert.True(firewall.IsEnabled);
        await firewall.EnableAsync([exe], _ct); // replace without a gap
        Assert.True(firewall.IsEnabled);

        await firewall.DisableAsync(_ct);
        Assert.False(firewall.IsEnabled);
    }

    private static string WithoutAutoInheritFlag(string sddl) =>
        sddl.StartsWith("D:AI", StringComparison.Ordinal) ? "D:" + sddl[4..] : sddl;
}
