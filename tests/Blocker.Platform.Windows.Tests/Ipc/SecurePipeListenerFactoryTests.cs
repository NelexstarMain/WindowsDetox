using System.IO.Pipes;
using System.Runtime.Versioning;
using Blocker.Platform.Windows.Ipc;

namespace Blocker.Platform.Windows.Tests.Ipc;

[SupportedOSPlatform("windows10.0.19041")]
[Trait("Category", "WindowsOnly")]
public sealed class SecurePipeListenerFactoryTests
{
    [Theory]
    [InlineData(SecurePipeProfile.Standard)]
    [InlineData(SecurePipeProfile.PluginHost)]
    public void Several_instances_of_a_pipe_can_be_created(SecurePipeProfile profile)
    {
        // The IPC server keeps one spare instance per connected client; on Windows the plugin-host profile used to fail from the
        // second instance on (labelling it needs WRITE_OWNER on the handle).
        string name = "WindowsDetox.Test." + Guid.NewGuid().ToString("N");
        var factory = new SecurePipeListenerFactory(profile);

        using NamedPipeServerStream first = factory.Create(name, isFirstInstance: true);
        using NamedPipeServerStream second = factory.Create(name, isFirstInstance: false);
        using NamedPipeServerStream third = factory.Create(name, isFirstInstance: false);

        Assert.All(new[] { first, second, third }, pipe => Assert.False(pipe.SafePipeHandle.IsInvalid));
    }
}
