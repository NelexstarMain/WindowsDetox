using Blocker.Contracts;

namespace Blocker.UI.ViewModels;

/// <summary>Outcome of one install pipeline stage.</summary>
internal enum InstallStageStatus
{
    /// <summary>The stage passed.</summary>
    Passed,

    /// <summary>The stage failed; later stages did not run.</summary>
    Failed,

    /// <summary>An earlier stage failed, so this one did not run.</summary>
    NotRun,
}

/// <summary>One stage of the install pipeline.</summary>
/// <param name="Name">Stage name shown to the user.</param>
/// <param name="Status">Outcome.</param>
internal sealed record InstallStage(string Name, InstallStageStatus Status);

/// <summary>Result of one plugin test.</summary>
/// <param name="Name">Test display name.</param>
/// <param name="Passed">Whether it passed.</param>
/// <param name="Detail">Failure kind and message, or <see langword="null"/> when it passed.</param>
internal sealed record InstallTestResult(string Name, bool Passed, string? Detail);

/// <summary>The install result in a shape the report dialog can render.</summary>
/// <param name="Succeeded">Whether the plugin was installed.</param>
/// <param name="PluginId">Id of the installed plugin, if any.</param>
/// <param name="Stages">Pipeline stages in execution order.</param>
/// <param name="Errors">Errors that are not individual test results.</param>
/// <param name="Tests">Per-test outcomes: every test that ran when the service reports them, otherwise the failures parsed from its messages.</param>
internal sealed record PluginInstallReport(
    bool Succeeded,
    string? PluginId,
    IReadOnlyList<InstallStage> Stages,
    IReadOnlyList<string> Errors,
    IReadOnlyList<InstallTestResult> Tests)
{
    /// <summary>Gets whether any test result is listed.</summary>
    public bool HasTests => Tests.Count > 0;

    /// <summary>Gets whether any non-test error is listed.</summary>
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Maps <see cref="InstallPluginResponse"/> to a stage-by-stage report. Current services name the failing stage and list every test
/// that ran; for responses of older services without those members the failing stage is inferred from the message shapes the install
/// pipeline produces and test results are parsed from the error list.
/// </summary>
internal static class PluginInstallReportMapper
{
    private static readonly string[] StageNames = ["Package", "Manifest", "API policy", "Compile", "Load", "Tests"];
    private static readonly string[] TestOutcomes = ["Failed", "TimedOut", "NotSupported"];

    /// <summary>Maps a response.</summary>
    /// <param name="response">Response of <c>plugins.install</c>.</param>
    /// <returns>The report.</returns>
    public static PluginInstallReport Map(InstallPluginResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        List<InstallTestResult> reported = [.. response.Tests?.Select(ToResult) ?? []];
        if (response.Success)
        {
            return new PluginInstallReport(true, response.PluginId, [.. StageNames.Select(n => new InstallStage(n, InstallStageStatus.Passed))], [], reported);
        }

        List<InstallTestResult> tests = reported;
        List<string> errors = [];
        int failedStage = 0;
        foreach (string error in response.Errors)
        {
            if (TryParseTest(error, out InstallTestResult? test))
            {
                if (response.Tests is null)
                {
                    tests.Add(test);
                }

                failedStage = Math.Max(failedStage, 5);
                continue;
            }

            errors.Add(error);
            failedStage = Math.Max(failedStage, ClassifyStage(error));
        }

        if (StageIndex(response.FailedStage) is { } named)
        {
            failedStage = named;
        }

        if (response.Errors.Count == 0)
        {
            errors.Add("The plugin was not installed.");
        }

        InstallStage[] stages =
        [
            .. StageNames.Select((name, index) => new InstallStage(
                name,
                index < failedStage ? InstallStageStatus.Passed : index == failedStage ? InstallStageStatus.Failed : InstallStageStatus.NotRun)),
        ];
        return new PluginInstallReport(false, response.PluginId, stages, errors, tests);
    }

    /// <summary>Position of a stage named by the service; <c>Host</c> (the check did not complete) is before every stage; unknown names are <see langword="null"/>.</summary>
    private static int? StageIndex(string? stage) => stage switch
    {
        "Package" => 0,
        "Manifest" => 1,
        "Policy" => 2,
        "Compile" => 3,
        "Load" => 4,
        "Tests" => 5,
        "Host" => -1,
        _ => null,
    };

    private static InstallTestResult ToResult(PluginTestResultDto test) => test.Outcome == PluginTestOutcomeDto.Passed
        ? new InstallTestResult(test.Name, true, null)
        : new InstallTestResult(test.Name, false, string.IsNullOrEmpty(test.Message) ? test.Outcome.ToString() : $"{test.Outcome}: {test.Message}");

    private static bool TryParseTest(string error, out InstallTestResult test)
    {
        foreach (string outcome in TestOutcomes)
        {
            string prefix = outcome + ": ";
            if (!error.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = error[prefix.Length..];
            int split = rest.IndexOf(": ", StringComparison.Ordinal);
            test = split < 0
                ? new InstallTestResult(rest, false, outcome)
                : new InstallTestResult(rest[..split], false, $"{outcome}: {rest[(split + 2)..]}");
            return true;
        }

        test = null!;
        return false;
    }

    private static int ClassifyStage(string error) => true switch
    {
        _ when error.StartsWith("No tests were found", StringComparison.Ordinal) => 5,
        _ when error.Contains(": BLK", StringComparison.Ordinal) || error.StartsWith("BLK", StringComparison.Ordinal) => 2,
        _ when error.Contains("error CS", StringComparison.Ordinal) || error.Contains(": CS", StringComparison.Ordinal) || error.StartsWith("CS", StringComparison.Ordinal) => 3,
        _ when error.StartsWith("Entry type", StringComparison.Ordinal) || error.StartsWith("The plugin could not be loaded", StringComparison.Ordinal) => 4,
        _ when error.Contains("plugin.json", StringComparison.OrdinalIgnoreCase) || error.Contains("manifest", StringComparison.OrdinalIgnoreCase) => 1,
        _ => 0,
    };
}
