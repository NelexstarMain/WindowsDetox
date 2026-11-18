using Blocker.Contracts;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class PluginInstallReportMapperTests
{
    [Fact]
    public void Success_marks_every_stage_passed()
    {
        PluginInstallReport report = PluginInstallReportMapper.Map(new InstallPluginResponse(true, "my.plugin", []));

        Assert.True(report.Succeeded);
        Assert.Equal("my.plugin", report.PluginId);
        Assert.Equal(["Package", "Manifest", "API policy", "Compile", "Load", "Tests"], report.Stages.Select(s => s.Name));
        Assert.All(report.Stages, s => Assert.Equal(InstallStageStatus.Passed, s.Status));
        Assert.Empty(report.Errors);
        Assert.False(report.HasTests);
    }

    [Theory]
    [InlineData("Package contains a file with an invalid name.", "Package")]
    [InlineData("plugin.json: capabilities must not be empty.", "Manifest")]
    [InlineData("Plugin.cs:3: BLK007: Type System.Net.Http.HttpClient needs the network capability.", "API policy")]
    [InlineData("Plugin.cs(4,9): error CS1002: ; expected", "Compile")]
    [InlineData("Plugin.cs:4: CS0103: The name 'x' does not exist.", "Compile")]
    [InlineData("Entry type 'My.Plugin' must be public.", "Load")]
    [InlineData("The plugin could not be loaded: boom", "Load")]
    [InlineData("No tests were found: the plugin must contain at least one public [Fact] or [Theory] test.", "Tests")]
    public void Failure_is_attributed_to_the_stage_the_message_belongs_to(string error, string failedStage)
    {
        PluginInstallReport report = PluginInstallReportMapper.Map(new InstallPluginResponse(false, null, [error]));

        Assert.False(report.Succeeded);
        int failedIndex = report.Stages.ToList().FindIndex(s => s.Name == failedStage);
        for (int i = 0; i < report.Stages.Count; i++)
        {
            InstallStageStatus expected = i < failedIndex ? InstallStageStatus.Passed : i == failedIndex ? InstallStageStatus.Failed : InstallStageStatus.NotRun;
            Assert.Equal(expected, report.Stages[i].Status);
        }
    }

    [Fact]
    public void Test_failures_become_per_test_results_and_are_not_repeated_as_errors()
    {
        PluginInstallReport report = PluginInstallReportMapper.Map(new InstallPluginResponse(
            false,
            "my.plugin",
            ["Failed: Tests.Adds: expected 3 but was 4", "TimedOut: Tests.Hangs: exceeded 2 s", "NotSupported: Tests.Data: MemberData is not supported"]));

        Assert.Equal(3, report.Tests.Count);
        Assert.All(report.Tests, t => Assert.False(t.Passed));
        Assert.Equal("Tests.Adds", report.Tests[0].Name);
        Assert.Equal("Failed: expected 3 but was 4", report.Tests[0].Detail);
        Assert.Equal("TimedOut: exceeded 2 s", report.Tests[1].Detail);
        Assert.Empty(report.Errors);
        Assert.True(report.HasTests);
        Assert.Equal(InstallStageStatus.Failed, report.Stages[^1].Status);
        Assert.All(report.Stages.Take(5), s => Assert.Equal(InstallStageStatus.Passed, s.Status));
    }

    [Fact]
    public void Empty_error_list_still_explains_that_nothing_was_installed()
    {
        PluginInstallReport report = PluginInstallReportMapper.Map(new InstallPluginResponse(false, null, []));

        Assert.Equal(["The plugin was not installed."], report.Errors);
    }
}
