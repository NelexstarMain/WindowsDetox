using Blocker.Plugin.Testing.Fixtures;
using Blocker.Plugin.Testing.Runner;

namespace Blocker.Plugin.Testing.Tests;

// All runner tests share this class so xUnit runs them sequentially: the fixtures keep static counters.
public sealed class PluginTestRunnerTests
{
    private const string Ns = "Blocker.Plugin.Testing.Fixtures.";
    private static readonly PluginTestRunnerOptions FastTimeouts = new() { PerTestTimeout = TimeSpan.FromMilliseconds(100) };

    private static Task<PluginTestReport> RunAsync(params Type[] types) => new PluginTestRunner().RunAsync(types, TestContext.Current.CancellationToken);

    private static PluginTestResult Single(PluginTestReport report) => Assert.Single(report.Results);

    [Fact]
    public async Task Passing_FactsOfEveryReturnType_RunInNameOrder()
    {
        PluginTestReport report = await RunAsync(typeof(AllPassing));

        Assert.Equal(
            [Ns + "AllPassing.AsyncTask", Ns + "AllPassing.AsyncValueTask", Ns + "AllPassing.Sync"],
            report.Results.Select(r => r.DisplayName));
        Assert.All(report.Results, r =>
        {
            Assert.Equal(PluginTestOutcome.Passed, r.Outcome);
            Assert.Equal(Ns + "AllPassing", r.ClassName);
            Assert.Null(r.Message);
            Assert.Null(r.StackTrace);
        });
        Assert.True(report.Succeeded);
        Assert.Equal((3, 0, 0, 0), (report.Passed, report.Failed, report.TimedOut, report.NotSupported));
    }

    [Fact]
    public async Task Theory_ExpandsInlineDataInDeclarationOrder()
    {
        PluginTestReport report = await RunAsync(typeof(InlineTheory));

        Assert.Equal(
            [
                (Ns + "InlineTheory.Rows(2, \"b\")", PluginTestOutcome.Passed),
                (Ns + "InlineTheory.Rows(1, \"a\")", PluginTestOutcome.Passed),
                (Ns + "InlineTheory.SecondRowFails(1)", PluginTestOutcome.Passed),
                (Ns + "InlineTheory.SecondRowFails(2)", PluginTestOutcome.Failed),
            ],
            report.Results.Select(r => (r.DisplayName, r.Outcome)));
        Assert.StartsWith("Assert.Equal() Failure", report.Results[3].Message);
        Assert.False(report.Succeeded);
    }

    [Fact]
    public async Task Theory_MemberDataAndClassData_AreNotSupported()
    {
        PluginTestReport report = await RunAsync(typeof(UnsupportedData));

        Assert.Equal(
            [(Ns + "UnsupportedData.FromClass", PluginTestOutcome.NotSupported), (Ns + "UnsupportedData.FromMember", PluginTestOutcome.NotSupported)],
            report.Results.Select(r => (r.DisplayName, r.Outcome)));
        Assert.Equal("ClassDataAttribute is not supported by the plugin test runner; use InlineData.", report.Results[0].Message);
        Assert.Equal("MemberDataAttribute is not supported by the plugin test runner; use InlineData.", report.Results[1].Message);
        Assert.Equal(2, report.NotSupported);
        Assert.False(report.Succeeded);
    }

    [Fact]
    public async Task Theory_WithoutData_Fails()
    {
        PluginTestResult result = Single(await RunAsync(typeof(TheoryWithoutData)));

        Assert.Equal(PluginTestOutcome.Failed, result.Outcome);
        Assert.Equal("Theory has no data attributes.", result.Message);
    }

    [Fact]
    public async Task Skipped_FactAndTheory_Fail()
    {
        PluginTestReport report = await RunAsync(typeof(SkippedTests));

        Assert.Equal(
            [Ns + "SkippedTests.SkippedFact", Ns + "SkippedTests.SkippedTheory"],
            report.Results.Select(r => r.DisplayName));
        Assert.All(report.Results, r =>
        {
            Assert.Equal(PluginTestOutcome.Failed, r.Outcome);
            Assert.Equal("Skipped tests are not allowed.", r.Message);
        });
    }

    [Fact]
    public async Task Explicit_Fails() =>
        AssertFailed("Explicit tests are not allowed.", Single(await RunAsync(typeof(ExplicitTests))));

    [Fact]
    public async Task AsyncVoid_Fails() =>
        AssertFailed("async void test methods are not allowed; return Task or ValueTask.", Single(await RunAsync(typeof(AsyncVoidTests))));

    [Fact]
    public async Task ClassWithoutParameterlessConstructor_Fails() =>
        AssertFailed("Test classes need a public parameterless constructor.", Single(await RunAsync(typeof(NoDefaultConstructor))));

    [Fact]
    public async Task FactWithParameters_Fails() =>
        AssertFailed("Fact methods cannot have parameters; use Theory with InlineData.", Single(await RunAsync(typeof(FactWithParameter))));

    [Fact]
    public async Task UnsupportedReturnType_Fails() =>
        AssertFailed("Unsupported return type 'Int32'; use void, Task or ValueTask.", Single(await RunAsync(typeof(UnsupportedReturn))));

    [Fact]
    public async Task Failure_UnwrapsExceptionAndSanitizesMessage()
    {
        PluginTestReport report = await RunAsync(typeof(Failing));

        Assert.Equal(
            [Ns + "Failing.DeepStack", Ns + "Failing.FailedAssertion", Ns + "Failing.Throws"],
            report.Results.Select(r => r.DisplayName));
        Assert.All(report.Results, r => Assert.Equal(PluginTestOutcome.Failed, r.Outcome));
        Assert.Equal("deep", report.Results[0].Message);
        Assert.StartsWith("Assert.Equal() Failure", report.Results[1].Message);
        Assert.Equal("boom\nsecond line", report.Results[2].Message);
        Assert.Contains("Failing.Throws", report.Results[2].StackTrace);
        Assert.DoesNotContain("TargetInvocationException", report.Results[2].StackTrace);
    }

    [Fact]
    public async Task Failure_StackTraceIsTrimmedToTwentyLines()
    {
        PluginTestReport report = await RunAsync(typeof(Failing));

        Assert.Equal(20, report.Results[0].StackTrace!.Split('\n').Length);
    }

    [Fact]
    public async Task Lifecycle_InitializesAndDisposesAroundEachTestOnFreshInstances()
    {
        Lifecycle.Reset();

        PluginTestReport report = await RunAsync(typeof(Lifecycle));

        Assert.Equal([PluginTestOutcome.Passed, PluginTestOutcome.Passed], report.Results.Select(r => r.Outcome));
        Assert.Equal(["init", "A", "disposeAsync", "init", "B", "disposeAsync"], Lifecycle.Events);
    }

    [Fact]
    public async Task SyncDisposable_IsDisposedOncePerTest()
    {
        int before = Volatile.Read(ref SyncDisposable.Disposed);

        PluginTestReport report = await RunAsync(typeof(SyncDisposable));

        Assert.True(report.Succeeded);
        Assert.Equal(before + 1, Volatile.Read(ref SyncDisposable.Disposed));
    }

    [Fact]
    public async Task DisposeFailure_FailsAnOtherwisePassingTest() =>
        AssertFailed("dispose failed", Single(await RunAsync(typeof(DisposeThrows))));

    [Fact]
    public async Task InitializeFailure_FailsTest() =>
        AssertFailed("init failed", Single(await RunAsync(typeof(InitializeThrows))));

    [Fact]
    public async Task PerTestTimeout_MarksHangingTestTimedOutAndContinues()
    {
        PluginTestReport report = await new PluginTestRunner(FastTimeouts).RunAsync([typeof(Hanging)], TestContext.Current.CancellationToken);

        Assert.Equal([Ns + "Hanging.Hangs", Ns + "Hanging.ZPasses"], report.Results.Select(r => r.DisplayName));
        Assert.Equal([PluginTestOutcome.TimedOut, PluginTestOutcome.Passed], report.Results.Select(r => r.Outcome));
        Assert.Equal("Test exceeded the per-test timeout of 00:00:00.1000000.", report.Results[0].Message);
        Assert.Equal((1, 0, 1, 0), (report.Passed, report.Failed, report.TimedOut, report.NotSupported));
    }

    [Fact]
    public async Task TotalTimeout_StopsRunningAndMarksRemainingTestsTimedOut()
    {
        var options = new PluginTestRunnerOptions { PerTestTimeout = TimeSpan.FromSeconds(30), TotalTimeout = TimeSpan.FromMilliseconds(100) };

        PluginTestReport report = await new PluginTestRunner(options).RunAsync([typeof(Hanging)], TestContext.Current.CancellationToken);

        Assert.Equal([PluginTestOutcome.TimedOut, PluginTestOutcome.TimedOut], report.Results.Select(r => r.Outcome));
        Assert.Equal("Total timeout of 00:00:00.1000000 elapsed while the test was running.", report.Results[0].Message);
        Assert.Equal("Total timeout of 00:00:00.1000000 elapsed before the test started.", report.Results[1].Message);
    }

    [Fact]
    public async Task CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new PluginTestRunner().RunAsync([typeof(AllPassing)], cts.Token));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1000, 0)]
    public void Constructor_NonPositiveTimeouts_Throw(int perTestMs, int totalMs)
    {
        var options = new PluginTestRunnerOptions { PerTestTimeout = TimeSpan.FromMilliseconds(perTestMs), TotalTimeout = TimeSpan.FromMilliseconds(totalMs) };

        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginTestRunner(options));
    }

    [Fact]
    public async Task DisplayNames_AreEscapedQuotedAndTruncated()
    {
        PluginTestReport report = await RunAsync(typeof(DisplayNames));

        Assert.Equal(
            [
                Ns + "DisplayNames.Formats(\"a\\u000Ab\", null, 'x', 1.5)",
                Ns + "DisplayNames.Formats(\"tab\\u0009here\", \"quote\"\", '\\u0001', -2)",
            ],
            report.Results.Take(2).Select(r => r.DisplayName));
        string longName = report.Results[2].DisplayName;
        Assert.Equal(200, longName.Length);
        Assert.StartsWith(Ns + "DisplayNames.Long(\"0123456789", longName);
        Assert.EndsWith("...", longName);
        Assert.All(report.Results, r => Assert.Equal(PluginTestOutcome.Passed, r.Outcome));
    }

    [Fact]
    public async Task Order_IsClassThenMethodRegardlessOfInput()
    {
        PluginTestReport report = await RunAsync(typeof(ZLast), typeof(AFirst));

        Assert.Equal(
            [Ns + "AFirst.Alpha", Ns + "AFirst.Beta", Ns + "ZLast.Run"],
            report.Results.Select(r => r.DisplayName));
    }

    [Fact]
    public async Task AbstractAndNonTestClasses_AreIgnored_AndEmptyReportFails()
    {
        PluginTestReport report = await RunAsync(typeof(AbstractSkipped), typeof(NotATest));

        Assert.Empty(report.Results);
        Assert.False(report.Succeeded);
        string text = report.ToText().Replace("\r\n", "\n");
        Assert.StartsWith("FAIL: No tests discovered.\nPassed: 0, Failed: 0, TimedOut: 0, NotSupported: 0, Total: 0, Duration: ", text);
    }

    [Fact]
    public async Task Assembly_RunsEveryExportedTestClass()
    {
        PluginTestReport report = await new PluginTestRunner(FastTimeouts).RunAsync(typeof(AllPassing).Assembly, TestContext.Current.CancellationToken);

        Assert.Contains(report.Results, r => r.DisplayName == Ns + "AllPassing.Sync" && r.Outcome == PluginTestOutcome.Passed);
        Assert.DoesNotContain(report.Results, r => r.ClassName == Ns + "AbstractSkipped" || r.ClassName == Ns + "NotATest");
        Assert.Equal(report.Results.OrderBy(r => r.ClassName, StringComparer.Ordinal).Select(r => r.ClassName), report.Results.Select(r => r.ClassName));
        Assert.False(report.Succeeded);
    }

    [Fact]
    public void ToText_RendersResultsDetailsAndSummary()
    {
        var report = new PluginTestReport(
            [
                new PluginTestResult("C", "C.Ok", PluginTestOutcome.Passed, TimeSpan.FromMilliseconds(12), null, null),
                new PluginTestResult("C", "C.Bad", PluginTestOutcome.Failed, TimeSpan.FromMilliseconds(3), "line1\nline2", "at C.Bad()"),
                new PluginTestResult("C", "C.Slow", PluginTestOutcome.TimedOut, TimeSpan.FromMilliseconds(100), "too slow", null),
                new PluginTestResult("C", "C.Odd", PluginTestOutcome.NotSupported, TimeSpan.Zero, "unsupported", null),
            ],
            TimeSpan.FromMilliseconds(150));

        string text = report.ToText().Replace("\r\n", "\n");

        Assert.Equal(
            "[PASS] C.Ok (12 ms)\n"
            + "[FAIL] C.Bad (3 ms)\n    line1\n    line2\n    at C.Bad()\n"
            + "[TIMEOUT] C.Slow (100 ms)\n    too slow\n"
            + "[NOTSUPPORTED] C.Odd (0 ms)\n    unsupported\n"
            + "Passed: 1, Failed: 1, TimedOut: 1, NotSupported: 1, Total: 4, Duration: 150 ms",
            text);
    }

    private static void AssertFailed(string message, PluginTestResult result)
    {
        Assert.Equal(PluginTestOutcome.Failed, result.Outcome);
        Assert.Equal(message, result.Message);
    }
}
