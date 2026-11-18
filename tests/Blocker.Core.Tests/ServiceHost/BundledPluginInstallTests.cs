using System.IO.Compression;
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

    // The fake plugin host reports "com.test.sample" for every package; report the id from the manifest instead.
    private static void ShipSecondPackage(ServiceHostHarness h)
    {
        h.Plugins.Evaluate = package =>
        {
            using var archive = new ZipArchive(new MemoryStream(package.ToArray()), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("plugin.json")!.Open());
            return FakePluginHost.Ready(reader.ReadToEnd().Contains("com.test.second", StringComparison.Ordinal) ? "com.test.second" : "com.test.sample");
        };
        h.Fs.File.WriteAllBytes(
            h.Fs.Path.Combine(h.Paths.BundledPluginsDirectory, "com.test.second.zip"),
            PluginTestSupport.Zip(
                ("plugin.json", PluginTestSupport.ValidManifestJson.Replace("com.test.sample", "com.test.second", StringComparison.Ordinal)),
                ("Plugin.cs", "// plugin")).ToArray());
    }

    [Fact]
    public async Task Every_bundled_plugin_is_installed_and_remembered_separately()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync(arrange: harness =>
        {
            ShipPackage(harness);
            ShipSecondPackage(harness);
        });
        await h.AdvanceUntilAsync(() => h.Fs.File.Exists(h.Paths.BundledPluginsMarkerPath) && h.Fs.File.ReadAllLines(h.Paths.BundledPluginsMarkerPath).Length == 2);

        Assert.Equal(["com.test.sample.zip", "com.test.second.zip"], h.Fs.File.ReadAllLines(h.Paths.BundledPluginsMarkerPath));
        IpcClient admin = await h.ConnectAsync(IpcCallerRole.Administrator);
        ListPluginsResponse listed = await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct);
        Assert.Equal(["com.test.sample", "com.test.second"], listed.Plugins.Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_plugin_added_by_an_update_is_installed_while_a_remembered_one_stays_removed()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync(arrange: harness =>
        {
            ShipPackage(harness);
            ShipSecondPackage(harness);
            harness.Fs.Directory.CreateDirectory(harness.Paths.DataRoot);
            harness.Fs.File.WriteAllLines(harness.Paths.BundledPluginsMarkerPath, ["com.test.sample.zip"]);
        });
        await h.AdvanceUntilAsync(() => h.Fs.File.ReadAllLines(h.Paths.BundledPluginsMarkerPath).Length == 2);

        Assert.Equal(["com.test.sample.zip", "com.test.second.zip"], h.Fs.File.ReadAllLines(h.Paths.BundledPluginsMarkerPath));
        IpcClient admin = await h.ConnectAsync(IpcCallerRole.Administrator);
        ListPluginsResponse listed = await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct);
        Assert.Equal("com.test.second", Assert.Single(listed.Plugins).Id);
    }
}
