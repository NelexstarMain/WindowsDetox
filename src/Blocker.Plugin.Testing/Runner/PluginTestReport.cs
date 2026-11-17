using System.Globalization;
using System.Text;

namespace Blocker.Plugin.Testing.Runner;

/// <summary>Outcome of one test.</summary>
public enum PluginTestOutcome
{
    /// <summary>The test completed without throwing.</summary>
    Passed,

    /// <summary>The test threw, or violated a runner rule (skip, async void, missing constructor, ...).</summary>
    Failed,

    /// <summary>The test exceeded the per-test timeout, or the total timeout elapsed before it could run.</summary>
    TimedOut,

    /// <summary>The test uses a feature the mini runner does not implement, for example <c>MemberData</c>.</summary>
    NotSupported,
}

/// <summary>Result of one test or theory row.</summary>
/// <param name="ClassName">Full name of the declaring test class.</param>
/// <param name="DisplayName">Name such as <c>Ns.Class.Method</c> or <c>Ns.Class.Method(1, "a")</c>.</param>
/// <param name="Outcome">Outcome.</param>
/// <param name="Duration">Wall-clock duration including setup and teardown.</param>
/// <param name="Message">Failure message; <see langword="null"/> when passed.</param>
/// <param name="StackTrace">Failure stack trace trimmed to 20 lines; <see langword="null"/> when unavailable.</param>
public sealed record PluginTestResult(
    string ClassName,
    string DisplayName,
    PluginTestOutcome Outcome,
    TimeSpan Duration,
    string? Message,
    string? StackTrace);

/// <summary>Results of a runner invocation.</summary>
/// <param name="Results">Results in execution order.</param>
/// <param name="Duration">Total wall-clock duration.</param>
public sealed record PluginTestReport(IReadOnlyList<PluginTestResult> Results, TimeSpan Duration)
{
    /// <summary>Gets the number of passed tests.</summary>
    public int Passed => Count(PluginTestOutcome.Passed);

    /// <summary>Gets the number of tests with outcome <see cref="PluginTestOutcome.Failed"/> (timeouts and unsupported tests are counted separately).</summary>
    public int Failed => Count(PluginTestOutcome.Failed);

    /// <summary>Gets the number of tests with outcome <see cref="PluginTestOutcome.TimedOut"/>.</summary>
    public int TimedOut => Count(PluginTestOutcome.TimedOut);

    /// <summary>Gets the number of tests with outcome <see cref="PluginTestOutcome.NotSupported"/>.</summary>
    public int NotSupported => Count(PluginTestOutcome.NotSupported);

    /// <summary>Gets whether at least one test ran and every result passed.</summary>
    public bool Succeeded => Results.Count > 0 && Results.All(r => r.Outcome == PluginTestOutcome.Passed);

    /// <summary>Renders a human-readable report.</summary>
    /// <returns>Multi-line text; one line per test, details indented, then a summary line.</returns>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (PluginTestResult r in Results)
        {
            string tag = r.Outcome switch
            {
                PluginTestOutcome.Passed => "PASS",
                PluginTestOutcome.Failed => "FAIL",
                PluginTestOutcome.TimedOut => "TIMEOUT",
                _ => "NOTSUPPORTED",
            };
            sb.Append('[').Append(tag).Append("] ").Append(r.DisplayName).Append(" (")
                .Append(r.Duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)).AppendLine(" ms)");
            AppendIndented(sb, r.Message);
            AppendIndented(sb, r.StackTrace);
        }

        if (Results.Count == 0)
        {
            sb.AppendLine("FAIL: No tests discovered.");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"Passed: {Passed}, Failed: {Failed}, TimedOut: {TimedOut}, NotSupported: {NotSupported}, Total: {Results.Count}, Duration: {Duration.TotalMilliseconds:0} ms");
        return sb.ToString();
    }

    private int Count(PluginTestOutcome outcome) => Results.Count(r => r.Outcome == outcome);

    private static void AppendIndented(StringBuilder sb, string? text)
    {
        if (text is null)
        {
            return;
        }

        foreach (string line in text.Split('\n'))
        {
            sb.Append("    ").AppendLine(line);
        }
    }
}
