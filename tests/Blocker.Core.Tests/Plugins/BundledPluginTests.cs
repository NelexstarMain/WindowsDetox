using System.Text.Json;
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
        Assert.Equal(new Version(1, 0, 1), manifest.Version);
        Assert.Equal("FocusInsights.InsightsPlugin", manifest.EntryType);
        Assert.True(Blocker.UI.Sdk.UiTreeValidator.IsValidPathData(manifest.Icon));
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

    [Fact]
    public async Task Goals_and_Tasks_passes_every_install_stage_and_all_of_its_tests()
    {
        using MemoryStream zip = ZipOfFolder("GoalsTasks");

        PluginInstallReport report = await PluginInstallPipeline.EvaluateAsync(zip, TestContext.Current.CancellationToken);

        Assert.True(report.Errors.Count == 0, string.Join(Environment.NewLine, report.Errors));
        Assert.True(report.Succeeded);
        Assert.Equal(PluginInstallStage.Ready, report.Stage);
        PluginManifest manifest = Assert.IsType<PluginManifest>(report.Manifest);
        Assert.Equal("com.windowsdetox.goals", manifest.Id.Value);
        Assert.Equal("Goals & Tasks", manifest.Name);
        Assert.Equal(new Version(1, 0, 1), manifest.Version);
        Assert.Equal("GoalsTasks.GoalsPlugin", manifest.EntryType);
        Assert.True(Blocker.UI.Sdk.UiTreeValidator.IsValidPathData(manifest.Icon));
        Assert.NotEqual(FocusInsightsIcon(), manifest.Icon);
        PluginTestReport tests = Assert.IsType<PluginTestReport>(report.TestReport);
        Assert.True(tests.Succeeded);
        Assert.True(tests.Passed >= 25, $"Only {tests.Passed} plugin tests ran.");
        Assert.Equal(tests.Results.Count, tests.Passed);
        Assert.Equal(0, tests.Failed + tests.TimedOut + tests.NotSupported);
        Assert.Equal(
            ["ui.dashboard", "ui.page", "ui.settings"],
            manifest.Capabilities.Select(PluginCapabilities.ToManifestName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Calendar_passes_every_install_stage_and_all_of_its_tests()
    {
        using MemoryStream zip = ZipOfFolder("Calendar");

        PluginInstallReport report = await PluginInstallPipeline.EvaluateAsync(zip, TestContext.Current.CancellationToken);

        Assert.True(report.Errors.Count == 0, string.Join(Environment.NewLine, report.Errors));
        Assert.True(report.Succeeded);
        Assert.Equal(PluginInstallStage.Ready, report.Stage);
        PluginManifest manifest = Assert.IsType<PluginManifest>(report.Manifest);
        Assert.Equal("com.windowsdetox.calendar", manifest.Id.Value);
        Assert.Equal("Calendar", manifest.Name);
        Assert.Equal(new Version(1, 0, 1), manifest.Version);
        Assert.Equal("CalendarPlanner.CalendarPlugin", manifest.EntryType);
        Assert.True(Blocker.UI.Sdk.UiTreeValidator.IsValidPathData(manifest.Icon));
        Assert.NotEqual(FocusInsightsIcon(), manifest.Icon);
        PluginTestReport tests = Assert.IsType<PluginTestReport>(report.TestReport);
        Assert.True(tests.Succeeded, string.Join(Environment.NewLine, tests.Results.Where(r => r.Outcome != PluginTestOutcome.Passed).Select(r => r.DisplayName + ": " + r.Message)));
        Assert.True(tests.Passed >= 250, $"Only {tests.Passed} plugin tests ran.");
        Assert.Equal(tests.Results.Count, tests.Passed);
        Assert.Equal(0, tests.Failed + tests.TimedOut + tests.NotSupported);
        Assert.Equal(
            ["notify", "session.start", "tick", "ui.dashboard", "ui.page", "ui.settings"],
            manifest.Capabilities.Select(PluginCapabilities.ToManifestName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_bundled_plugin_folder_has_a_unique_id_and_icon_and_is_covered_by_these_tests()
    {
        string[] folders = [.. Directory.EnumerateDirectories(Path.Combine(RepositoryRoot(), "plugins")).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal)];
        Assert.Equal(["Calendar", "FocusInsights", "GoalsTasks"], folders);

        List<JsonElement> manifests = [.. folders.Select(f => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "plugins", f, "plugin.json"))).RootElement)];

        Assert.Equal(folders.Length, manifests.Select(m => m.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(folders.Length, manifests.Select(m => m.GetProperty("icon").GetString()).Distinct().Count());
        Assert.All(manifests, m => Assert.True(Blocker.UI.Sdk.UiTreeValidator.IsValidPathData(m.GetProperty("icon").GetString())));
    }

    private static string FocusInsightsIcon() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "plugins", "FocusInsights", "plugin.json"))).RootElement.GetProperty("icon").GetString()!;
}
