using Blocker.SmokeTest;

// End-to-end smoke test of the INSTALLED WindowsDetox service. Run it elevated on a disposable Windows machine (the CI installer job, a VM):
// it starts a real 15-minute focus session through the service pipe, checks that the enforcement is in place and works, ends the session
// with the break-glass code and checks that everything was rolled back. Every step prints PASS, FAIL <name>: <detail> or SKIP; the exit code
// is 1 when any step failed. The service log tail is always printed last.
//
// Never hangs: every wait has its own timeout, the whole run is capped at 6 minutes, and a watchdog kills the process after 8.
Console.OutputEncoding = System.Text.Encoding.UTF8;
using Timer watchdog = new(
    static _ =>
    {
        Console.WriteLine("FAIL watchdog: the tool was still running after 8 minutes and was stopped");
        Environment.Exit(3);
    },
    null,
    TimeSpan.FromMinutes(8),
    Timeout.InfiniteTimeSpan);

using CancellationTokenSource cap = new(TimeSpan.FromMinutes(6));
StepReport report = new();
SmokeScenario scenario = new(report, cap.Token);
try
{
    await scenario.RunAsync();
}
catch (Exception ex)
{
    report.Fail("unexpected-error", ex.ToString());
}
finally
{
    await scenario.FinishAsync();
}

Console.WriteLine();
Console.WriteLine(report.Failures == 0 ? "RESULT: all steps passed" : $"RESULT: {report.Failures} step(s) failed");
scenario.PrintServiceLog();
return report.Failures == 0 ? 0 : 1;
