using System.Text;
using Blocker.Core.Plugins;
using Blocker.Plugin.Testing.Runner;

namespace Blocker.Core.Tests.Plugins;

public sealed class PluginInstallPipelineTests
{
    private const string FocusAssertion = "Assert.Equal(\"3\", await CounterValueAsync(ctx));";

    private static async Task<PluginInstallReport> EvaluateAsync(Stream zip)
    {
        await using (zip)
        {
            return await PluginInstallPipeline.EvaluateAsync(zip, CancellationToken.None);
        }
    }

    private static async Task<PluginInstallReport> EvaluateStarterAsync(Action<Dictionary<string, string>>? edit = null)
    {
        Dictionary<string, string> files = await PluginTestSupport.StarterFilesAsync();
        edit?.Invoke(files);
        return await EvaluateAsync(PluginTestSupport.ZipOf(files));
    }

    [Fact]
    public async Task The_starter_template_passes_every_stage()
    {
        using var zip = new MemoryStream();
        await Blocker.Plugin.Templates.PluginStarterTemplate.WriteZipAsync(zip, CancellationToken.None);
        zip.Position = 0;

        PluginInstallReport report = await PluginInstallPipeline.EvaluateAsync(zip, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(PluginInstallStage.Ready, report.Stage);
        Assert.Empty(report.Errors);
        PluginTestReport tests = Assert.IsType<PluginTestReport>(report.TestReport);
        Assert.True(tests.Succeeded);
        Assert.Equal(0, tests.Failed + tests.TimedOut + tests.NotSupported);
        Assert.Equal(tests.Results.Count, tests.Passed);
        Assert.Contains(tests.Results, r => r.DisplayName == "FocusCounter.Tests.FocusCounterTests.Counts_blocked_processes_during_a_session");
        PluginManifest manifest = Assert.IsType<PluginManifest>(report.Manifest);
        Assert.Equal("com.example.focus-counter", manifest.Id.Value);
        Assert.Equal("FocusCounter.PluginMain", manifest.EntryType);
    }

    [Fact]
    public async Task A_failing_plugin_test_stops_at_the_tests_stage()
    {
        PluginInstallReport report = await EvaluateStarterAsync(files =>
            files["PluginTests.cs"] = files["PluginTests.cs"].Replace(FocusAssertion, "Assert.Equal(\"4\", await CounterValueAsync(ctx));", StringComparison.Ordinal));

        Assert.False(report.Succeeded);
        Assert.Equal(PluginInstallStage.Tests, report.Stage);
        PluginTestReport tests = Assert.IsType<PluginTestReport>(report.TestReport);
        Assert.Equal(1, tests.Failed);
        Assert.Equal(tests.Results.Count - 1, tests.Passed);
        string error = Assert.Single(report.Errors);
        Assert.StartsWith("Failed: FocusCounter.Tests.FocusCounterTests.Counts_blocked_processes_during_a_session: ", error, StringComparison.Ordinal);
        Assert.Contains("Expected", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugin_without_tests_fails_at_the_tests_stage()
    {
        PluginInstallReport report = await EvaluateStarterAsync(files => files.Remove("PluginTests.cs"));

        Assert.Equal(PluginInstallStage.Tests, report.Stage);
        Assert.Equal(["No tests were found: the plugin must contain at least one public [Fact] or [Theory] test."], report.Errors);
        Assert.Empty(Assert.IsType<PluginTestReport>(report.TestReport).Results);
    }

    [Fact]
    public async Task A_skipped_plugin_test_fails_at_the_tests_stage()
    {
        PluginInstallReport report = await EvaluateStarterAsync(files =>
            files["PluginTests.cs"] = files["PluginTests.cs"].Replace("[Fact]\n    public async Task Counts_blocked_processes_during_a_session", "[Fact(Skip = \"later\")]\n    public async Task Counts_blocked_processes_during_a_session", StringComparison.Ordinal));

        Assert.Equal(PluginInstallStage.Tests, report.Stage);
        Assert.Equal(["Failed: FocusCounter.Tests.FocusCounterTests.Counts_blocked_processes_during_a_session: Skipped tests are not allowed."], report.Errors);
    }

    [Fact]
    public async Task File_access_is_a_policy_failure_and_nothing_is_compiled_or_run()
    {
        const string Extra = """
            using System.IO;
            namespace FocusCounter;
            public static class Leak
            {
                public static string Read() => File.ReadAllText("C:\\secrets.txt");
            }
            """;

        PluginInstallReport report = await EvaluateStarterAsync(files => files["Leak.cs"] = Extra);

        Assert.False(report.Succeeded);
        Assert.Equal(PluginInstallStage.Policy, report.Stage);
        Assert.Null(report.TestReport);
        string error = Assert.Single(report.Errors);
        Assert.Equal(
            "Leak.cs:5: BLK020: 'System.IO.File' is not allowed: Plugins cannot access the file system; use context.Storage.",
            error);
    }

    [Fact]
    public async Task Policy_violations_in_tests_are_rejected_too()
    {
        PluginInstallReport report = await EvaluateStarterAsync(files =>
            files["PluginTests.cs"] = files["PluginTests.cs"].Replace(FocusAssertion, FocusAssertion + "\n        _ = DateTime.UtcNow;", StringComparison.Ordinal));

        Assert.Equal(PluginInstallStage.Policy, report.Stage);
        string error = Assert.Single(report.Errors);
        Assert.StartsWith("PluginTests.cs:", error, StringComparison.Ordinal);
        Assert.Contains(": BLK025: 'System.DateTime.UtcNow' is not allowed: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_needs_the_network_capability()
    {
        const string Extra = """
            namespace FocusCounter;
            public static class Web
            {
                public static System.Net.Http.HttpClient Create() => new();
            }
            """;

        PluginInstallReport denied = await EvaluateStarterAsync(files => files["Web.cs"] = Extra);
        PluginInstallReport allowed = await EvaluateStarterAsync(files =>
        {
            files["Web.cs"] = Extra;
            files["plugin.json"] = files["plugin.json"].Replace("\"events.session\",", "\"events.session\", \"network\",", StringComparison.Ordinal);
        });

        Assert.Equal(PluginInstallStage.Policy, denied.Stage);
        Assert.StartsWith("Web.cs:4: BLK031: 'System.Net.Http.HttpClient' is not allowed: Requires the 'network' capability.", Assert.Single(denied.Errors), StringComparison.Ordinal);
        Assert.True(allowed.Succeeded);
    }

    [Fact]
    public async Task A_compile_error_fails_at_the_compile_stage_with_file_and_line()
    {
        PluginInstallReport report = await EvaluateStarterAsync(files => files["Broken.cs"] = "namespace FocusCounter;\npublic class Broken { int X = \"no\"; }\n");

        Assert.Equal(PluginInstallStage.Compile, report.Stage);
        Assert.Equal(["Broken.cs:2: CS0029: Cannot implicitly convert type 'string' to 'int'"], report.Errors);
        Assert.Null(report.TestReport);
    }

    [Fact]
    public async Task A_missing_entry_type_fails_at_the_load_stage()
    {
        PluginInstallReport report = await EvaluateStarterAsync(files =>
            files["plugin.json"] = files["plugin.json"].Replace("FocusCounter.PluginMain", "FocusCounter.Missing", StringComparison.Ordinal));

        Assert.False(report.Succeeded);
        Assert.Equal(PluginInstallStage.Load, report.Stage);
        Assert.Equal(["Entry type 'FocusCounter.Missing' was not found in the plugin."], report.Errors);
        Assert.Null(report.TestReport);
    }

    [Theory]
    [InlineData("FocusCounter.FocusCounterState", "Entry type 'FocusCounter.FocusCounterState' must implement IBlockerPlugin.")]
    [InlineData("FocusCounter.FocusContribution", "Entry type 'FocusCounter.FocusContribution' must be a non-abstract class.")]
    [InlineData("FocusCounter.Hidden", "Entry type 'FocusCounter.Hidden' must be public.")]
    [InlineData("FocusCounter.NeedsArgument", "Entry type 'FocusCounter.NeedsArgument' must have a public parameterless constructor.")]
    [InlineData("FocusCounter.Fake", "Entry type 'FocusCounter.Fake' must be a non-abstract class.")]
    public async Task An_unusable_entry_type_fails_at_the_load_stage(string entryType, string expected)
    {
        const string Extra = """
            using Blocker.Sdk;
            namespace FocusCounter;
            internal sealed class Hidden : IBlockerPlugin
            {
                public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            }
            public sealed class NeedsArgument(int value) : IBlockerPlugin
            {
                public int Value { get; } = value;
                public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            }
            public interface Fake : IBlockerPlugin;
            """;

        PluginInstallReport report = await EvaluateStarterAsync(files =>
        {
            files["Extra.cs"] = Extra;
            files["plugin.json"] = files["plugin.json"].Replace("FocusCounter.PluginMain", entryType, StringComparison.Ordinal);
        });

        Assert.Equal(PluginInstallStage.Load, report.Stage);
        Assert.Equal([expected], report.Errors);
    }

    [Fact]
    public async Task An_invalid_manifest_fails_at_the_manifest_stage()
    {
        PluginInstallReport report = await EvaluateStarterAsync(files =>
            files["plugin.json"] = files["plugin.json"].Replace("\"sdkVersion\": \"1.0\"", "\"sdkVersion\": \"2.0\"", StringComparison.Ordinal));

        Assert.Equal(PluginInstallStage.Manifest, report.Stage);
        Assert.Equal(["'sdkVersion' major version 2 is not supported; this Blocker supports SDK 1.x."], report.Errors);
        Assert.Null(report.Manifest);
    }

    [Fact]
    public async Task A_bad_package_fails_at_the_package_stage()
    {
        PluginInstallReport report = await EvaluateAsync(PluginTestSupport.Zip(("../evil.cs", "class E { }")));

        Assert.Equal(PluginInstallStage.Package, report.Stage);
        Assert.Equal(["Entry '../evil.cs': '.' and '..' path segments are not allowed."], report.Errors);
    }

    [Fact]
    public async Task Data_that_is_not_a_zip_fails_at_the_package_stage()
    {
        PluginInstallReport report = await EvaluateAsync(new MemoryStream(Encoding.UTF8.GetBytes("MZ not a zip")));

        Assert.Equal(PluginInstallStage.Package, report.Stage);
        Assert.StartsWith("The file is not a valid ZIP archive: ", Assert.Single(report.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using MemoryStream zip = PluginTestSupport.ZipOf(await PluginTestSupport.StarterFilesAsync());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginInstallPipeline.EvaluateAsync(zip, cts.Token));
    }

    [Fact]
    public async Task The_plugin_context_is_unloaded_after_evaluation()
    {
        PluginInstallReport report = await EvaluateStarterAsync();

        Assert.True(report.Succeeded);
        bool unloaded = false;
        for (int attempt = 0; attempt < 20 && !unloaded; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            unloaded = !System.Runtime.Loader.AssemblyLoadContext.All.Any(c => c.Name == "plugin:com.example.focus-counter");
        }

        Assert.True(unloaded);
    }
}
