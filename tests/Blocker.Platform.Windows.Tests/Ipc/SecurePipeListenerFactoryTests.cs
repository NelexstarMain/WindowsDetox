using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Blocker.Platform.Windows.Ipc;

namespace Blocker.Platform.Windows.Tests.Ipc;

[SupportedOSPlatform("windows10.0.19041")]
[Trait("Category", "WindowsOnly")]
public sealed class SecurePipeListenerFactoryTests
{
    [Theory]
    [InlineData(SecurePipeProfile.Standard)]
    [InlineData(SecurePipeProfile.PluginHost)]
    public void The_first_instance_is_created_and_labelled(SecurePipeProfile profile)
    {
        string name = "WindowsDetox.Test." + Guid.NewGuid().ToString("N");

        using NamedPipeServerStream first = new SecurePipeListenerFactory(profile).Create(name, isFirstInstance: true);

        Assert.False(first.SafePipeHandle.IsInvalid);
    }

    [Theory]
    [InlineData(SecurePipeProfile.Standard)]
    [InlineData(SecurePipeProfile.PluginHost)]
    public void Only_SYSTEM_may_create_further_instances_and_relabel_them(SecurePipeProfile profile)
    {
        // Further instances are created by the service (SYSTEM) for every connected client and the plugin-host pipe relabels each one,
        // which needs FILE_CREATE_PIPE_INSTANCE and WRITE_OWNER. CI runs as an administrator, not SYSTEM, so this checks the ACEs.
        const PipeAccessRights needed = PipeAccessRights.CreateNewInstance | PipeAccessRights.TakeOwnership;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        AuthorizationRuleCollection rules = SecurePipeListenerFactory.BuildSecurity(profile).GetAccessRules(true, false, typeof(SecurityIdentifier));
        PipeAccessRule[] all = [.. rules.Cast<PipeAccessRule>()];

        Assert.Contains(all, r => r.IdentityReference.Equals(system) && r.AccessControlType == AccessControlType.Allow && (r.PipeAccessRights & needed) == needed);
        Assert.DoesNotContain(all, r => !r.IdentityReference.Equals(system) && (r.PipeAccessRights & PipeAccessRights.CreateNewInstance) != 0);
    }
}
