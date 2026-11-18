using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Tests.Plugins;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class BundledPluginInstallTests
{
    private static CancellationToken Ct => ServiceHostHarness.Ct;

    private static void ShipPackage(ServiceHostHarness h)
    {
        h.Fs.Directory.CreateDirectory(h.Paths.BundledPluginsDirectory);
        h.Fs.File.WriteAllBytes(
            h.Fs.Path.Combine(h.Paths.BundledPluginsDirectory, "com.test.sample.zip"),
            PluginTestSupport.ZipWithManifest(("Plugin.cs", "// plugin")).ToArray());
    }

    [Fact]
    public async Task A_bundled_plugin_is_installed_once_and_remembered()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync(arrange: ShipPackage);
        await h.AdvanceUntilAsync(() => h.Fs.File.Exists(h.Paths.BundledPluginsMarkerPath));

        Assert.Equal(["com.test.sample.zip"], h.Fs.File.ReadAllLines(h.Paths.BundledPluginsMarkerPath));
        IpcClient admin = await h.ConnectAsync(IpcCallerRole.Administrator);
        ListPluginsResponse listed = await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct);
        Assert.Equal("com.test.sample", Assert.Single(listed.Plugins).Id);
    }

    [Fact]
    public async Task A_bundled_plugin_the_user_removed_is_not_reinstalled()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync(arrange: harness =>
        {
            ShipPackage(harness);
            harness.Fs.Directory.CreateDirectory(harness.Paths.DataRoot);
            harness.Fs.File.WriteAllLines(harness.Paths.BundledPluginsMarkerPath, ["com.test.sample.zip"]);
        });

        IpcClient admin = await h.ConnectAsync(IpcCallerRole.Administrator);
        ListPluginsResponse listed = await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct);
        Assert.Empty(listed.Plugins);
    }
}
