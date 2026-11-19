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

    // Installs "com.test.sample" at the given version before the service starts, as an earlier release would have.
    private static void InstallEarlier(ServiceHostHarness h, string version, bool enabled)
    {
        h.Fs.Directory.CreateDirectory(h.Paths.DataRoot);
        h.Fs.File.WriteAllLines(h.Paths.BundledPluginsMarkerPath, ["com.test.sample.zip"]);
        var store = new Blocker.Core.PluginRuntime.PluginStore(h.Fs, h.Paths.PluginsDirectory, h.Paths.PluginDataDirectory);
        byte[] old = PluginTestSupport.Zip(
            ("plugin.json", PluginTestSupport.ValidManifestJson.Replace("1.2.3", version, StringComparison.Ordinal)),
            ("Plugin.cs", "// old")).ToArray();
        store.InstallAsync(new Blocker.Sdk.PluginId("com.test.sample"), old, enabled, Ct).AsTask().GetAwaiter().GetResult();
    }

    private static async Task<string> InstalledVersionAsync(ServiceHostHarness h)
    {
        IpcClient admin = await h.ConnectAsync(IpcCallerRole.Administrator);
        ListPluginsResponse listed = await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct);
        return Assert.Single(listed.Plugins).Version;
    }

    [Fact]
    public async Task A_newer_bundled_version_replaces_an_installed_older_one_after_an_update()
    {
        int evaluatedAtReload = -1;
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync(arrange: harness =>
        {
            ShipPackage(harness);
            InstallEarlier(harness, "1.0.0", enabled: true);
            harness.Plugins.Evaluate = package =>
            {
                evaluatedAtReload = harness.Plugins.Reloads;
                return FakePluginHost.Ready("com.test.sample");
            };
        });

        // The install replaces the package and then reloads the plugin host.
        await h.AdvanceUntilAsync(() => evaluatedAtReload >= 0 && h.Plugins.Reloads > evaluatedAtReload);

        Assert.Equal("1.2.3", await InstalledVersionAsync(h));
        Assert.Equal(["com.test.sample.zip"], h.Fs.File.ReadAllLines(h.Paths.BundledPluginsMarkerPath));
    }

    // The packages are checked in name order, so once the second (new) package was installed the check of the first one is over.
    private static async Task<int> EvaluationsOfSampleAsync(bool enabled, string installed)
    {
        int evaluations = 0;
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync(arrange: harness =>
        {
            ShipPackage(harness);
            ShipSecondPackage(harness);
            InstallEarlier(harness, installed, enabled);
            Func<ReadOnlyMemory<byte>, PluginInstallReportDto> inner = harness.Plugins.Evaluate;
            harness.Plugins.Evaluate = package =>
            {
                PluginInstallReportDto report = inner(package);
                if (report.PluginId == "com.test.sample")
                {
                    evaluations++;
                }

                return report;
            };
        });

        await h.AdvanceUntilAsync(() => h.Fs.File.ReadAllLines(h.Paths.BundledPluginsMarkerPath).Contains("com.test.second.zip"));
        return evaluations;
    }

    [Fact]
    public async Task The_same_installed_version_is_left_alone() => Assert.Equal(0, await EvaluationsOfSampleAsync(enabled: true, installed: "1.2.3"));

    [Fact]
    public async Task A_newer_installed_version_is_never_downgraded() => Assert.Equal(0, await EvaluationsOfSampleAsync(enabled: true, installed: "2.0.0"));

    [Fact]
    public async Task A_disabled_plugin_is_not_upgraded_or_switched_on() => Assert.Equal(0, await EvaluationsOfSampleAsync(enabled: false, installed: "1.0.0"));
}
