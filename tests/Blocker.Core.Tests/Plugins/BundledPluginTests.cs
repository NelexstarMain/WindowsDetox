using Blocker.Core.Plugins;
using Blocker.Plugin.Testing.Runner;
using Blocker.Sdk;

namespace Blocker.Core.Tests.Plugins;

/// <summary>Proves that the first-party plugins under <c>plugins/</c> pass the same pipeline an uploaded plugin ZIP passes.</summary>
public sealed class BundledPluginTests
{
    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "WindowsDetox.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("WindowsDetox.slnx was not found above the test output directory.");
    }

    // Packages the plugin folder the way the build does: every .cs/.json/.md file, flat, at the ZIP root.
    private static MemoryStream ZipOfFolder(string name)
    {
        string folder = Path.Combine(RepositoryRoot(), "plugins", name);
        string[] files =
        [
            .. Directory.EnumerateFiles(folder)
                .Where(f => Path.GetExtension(f) is ".cs" or ".json" or ".md")
                .Order(StringComparer.Ordinal),
        ];
        Assert.Contains(files, f => Path.GetFileName(f) == "plugin.json");
        return PluginTestSupport.Zip([.. files.Select(f => (Path.GetFileName(f), File.ReadAllText(f)))]);
    }

    [Fact]
    public async Task Focus_Insights_passes_every_install_stage_and_all_of_its_tests()
    {
        using MemoryStream zip = ZipOfFolder("FocusInsights");

        PluginInstallReport report = await PluginInstallPipeline.EvaluateAsync(zip, TestContext.Current.CancellationToken);

        Assert.True(report.Errors.Count == 0, string.Join(Environment.NewLine, report.Errors));
        Assert.True(report.Succeeded);
        Assert.Equal(PluginInstallStage.Ready, report.Stage);
        PluginManifest manifest = Assert.IsType<PluginManifest>(report.Manifest);
        Assert.Equal("com.windowsdetox.insights", manifest.Id.Value);
        Assert.Equal(new Version(1, 0, 0), manifest.Version);
        Assert.Equal("FocusInsights.InsightsPlugin", manifest.EntryType);
        PluginTestReport tests = Assert.IsType<PluginTestReport>(report.TestReport);
        Assert.True(tests.Succeeded);
        Assert.True(tests.Passed >= 20, $"Only {tests.Passed} plugin tests ran.");
        Assert.Equal(tests.Results.Count, tests.Passed);
        Assert.Equal(0, tests.Failed + tests.TimedOut + tests.NotSupported);
    }

    [Fact]
    public async Task Focus_Insights_declares_only_the_capabilities_it_needs()
    {
        using MemoryStream zip = ZipOfFolder("FocusInsights");

        PluginInstallReport report = await PluginInstallPipeline.EvaluateAsync(zip, TestContext.Current.CancellationToken);

        PluginManifest manifest = Assert.IsType<PluginManifest>(report.Manifest);
        Assert.Equal(
            ["events.process", "events.session", "ui.dashboard", "ui.page", "ui.settings"],
            manifest.Capabilities.Select(PluginCapabilities.ToManifestName).Order(StringComparer.Ordinal));
    }
}
