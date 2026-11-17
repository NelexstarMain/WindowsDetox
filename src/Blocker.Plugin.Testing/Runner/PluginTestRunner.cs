using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.v3;

namespace Blocker.Plugin.Testing.Runner;

/// <summary>
/// Runs xUnit-style plugin tests in-process, without the xUnit runner. Supports <c>[Fact]</c>, <c>[Theory]</c> with
/// <c>[InlineData]</c>, <c>IAsyncLifetime</c>, <c>IAsyncDisposable</c> and <c>IDisposable</c>. Tests run sequentially in
/// a deterministic order (class full name, method name, data index) on a fresh class instance each. Skipped and
/// explicit tests fail, so a green report always means every discovered test really ran.
/// </summary>
public sealed class PluginTestRunner
{
    private readonly PluginTestRunnerOptions _options;

    /// <summary>Creates a runner.</summary>
    /// <param name="options">Limits; <see langword="null"/> uses the defaults.</param>
    public PluginTestRunner(PluginTestRunnerOptions? options = null)
    {
        _options = options ?? new PluginTestRunnerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.PerTestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.TotalTimeout, TimeSpan.Zero);
    }

    /// <summary>Discovers and runs every test in the public types of <paramref name="assembly"/>.</summary>
    /// <param name="assembly">Assembly containing test classes.</param>
    /// <returns>The report.</returns>
    public Task<PluginTestReport> RunAsync(Assembly assembly) => RunAsync(assembly, CancellationToken.None);

    /// <summary>Discovers and runs every test in the public types of <paramref name="assembly"/>.</summary>
    /// <param name="assembly">Assembly containing test classes.</param>
    /// <param name="cancellationToken">Aborts the run with <see cref="OperationCanceledException"/>.</param>
    /// <returns>The report.</returns>
    public Task<PluginTestReport> RunAsync(Assembly assembly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return RunAsync(assembly.GetExportedTypes(), cancellationToken);
    }

    /// <summary>Discovers and runs every test in <paramref name="testClasses"/>. Non-public, abstract and generic types are ignored.</summary>
    /// <param name="testClasses">Candidate test classes.</param>
    /// <returns>The report.</returns>
    public Task<PluginTestReport> RunAsync(IEnumerable<Type> testClasses) => RunAsync(testClasses, CancellationToken.None);

    /// <summary>Discovers and runs every test in <paramref name="testClasses"/>. Non-public, abstract and generic types are ignored.</summary>
    /// <param name="testClasses">Candidate test classes.</param>
    /// <param name="cancellationToken">Aborts the run with <see cref="OperationCanceledException"/>.</param>
    /// <returns>The report.</returns>
    public async Task<PluginTestReport> RunAsync(IEnumerable<Type> testClasses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(testClasses);
        long started = Stopwatch.GetTimestamp();
        List<TestCase> cases = [.. testClasses.Distinct()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsVisible: true, ContainsGenericParameters: false })
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .SelectMany(Discover)];

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(_options.TotalTimeout);
        List<PluginTestResult> results = new(cases.Count);
        foreach (TestCase testCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(testCase.Rejection is { } rejection
                ? Reject(testCase, rejection.Outcome, rejection.Message)
                : total.IsCancellationRequested
                    ? Reject(testCase, PluginTestOutcome.TimedOut, $"Total timeout of {_options.TotalTimeout} elapsed before the test started.")
                    : await RunOneAsync(testCase, total.Token, cancellationToken).ConfigureAwait(false));
        }

        return new PluginTestReport(results, Stopwatch.GetElapsedTime(started));
    }

    private static IEnumerable<TestCase> Discover(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.GetCustomAttribute<FactAttribute>(inherit: true) is not null)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.GetParameters().Length)
            .ThenBy(m => m.ToString(), StringComparer.Ordinal))
        {
            FactAttribute fact = method.GetCustomAttribute<FactAttribute>(inherit: true)!;
            string className = type.FullName ?? type.Name;
            string plain = PluginTestFormatting.DisplayName(className, method.Name, null);
            string? invalid = Validate(type, method, fact);
            if (invalid is not null)
            {
                yield return new TestCase(type, method, className, plain, null, (PluginTestOutcome.Failed, invalid));
                continue;
            }

            if (fact is not ITheoryAttribute)
            {
                yield return method.GetParameters().Length > 0
                    ? new TestCase(type, method, className, plain, null, (PluginTestOutcome.Failed, "Fact methods cannot have parameters; use Theory with InlineData."))
                    : new TestCase(type, method, className, plain, null, null);
                continue;
            }

            List<IDataAttribute> dataAttributes = [.. method.GetCustomAttributes(inherit: true).OfType<IDataAttribute>()];
            if (dataAttributes.Count == 0)
            {
                yield return new TestCase(type, method, className, plain, null, (PluginTestOutcome.Failed, "Theory has no data attributes."));
                continue;
            }

            foreach (IDataAttribute data in dataAttributes)
            {
                if (data is not InlineDataAttribute inline)
                {
                    yield return new TestCase(type, method, className, plain, null,
                        (PluginTestOutcome.NotSupported, $"{data.GetType().Name} is not supported by the plugin test runner; use InlineData."));
                    continue;
                }

                object?[] args = inline.Data ?? [null];
                string name = PluginTestFormatting.DisplayName(className, method.Name, args);
                yield return data.Skip is not null
                    ? new TestCase(type, method, className, name, args, (PluginTestOutcome.Failed, SkipMessage))
                    : new TestCase(type, method, className, name, args, null);
            }
        }
    }

    private const string SkipMessage = "Skipped tests are not allowed.";

    private static string? Validate(Type type, MethodInfo method, FactAttribute fact)
    {
        if (fact.Skip is not null || fact.SkipUnless is not null || fact.SkipWhen is not null)
        {
            return SkipMessage;
        }

        if (fact.Explicit)
        {
            return "Explicit tests are not allowed.";
        }

        if (method.ReturnType == typeof(void) && method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
        {
            return "async void test methods are not allowed; return Task or ValueTask.";
        }

        if (method.ReturnType != typeof(void) && method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask))
        {
            return $"Unsupported return type '{method.ReturnType.Name}'; use void, Task or ValueTask.";
        }

        return type.GetConstructor(Type.EmptyTypes) is null
            ? "Test classes need a public parameterless constructor."
            : null;
    }

    private PluginTestResult Reject(TestCase testCase, PluginTestOutcome outcome, string message) =>
        new(testCase.ClassName, testCase.DisplayName, outcome, TimeSpan.Zero, message, null);

    private async Task<PluginTestResult> RunOneAsync(TestCase testCase, CancellationToken totalToken, CancellationToken callerToken)
    {
        long started = Stopwatch.GetTimestamp();
        Task<Exception?> execution = Task.Run(() => ExecuteAsync(testCase), CancellationToken.None);
        Exception? failure;
        try
        {
            failure = await execution.WaitAsync(_options.PerTestTimeout, totalToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abandon(execution);
            return Timed(testCase, started, $"Test exceeded the per-test timeout of {_options.PerTestTimeout}.");
        }
        catch (OperationCanceledException) when (totalToken.IsCancellationRequested)
        {
            Abandon(execution);
            callerToken.ThrowIfCancellationRequested();
            return Timed(testCase, started, $"Total timeout of {_options.TotalTimeout} elapsed while the test was running.");
        }

        TimeSpan duration = Stopwatch.GetElapsedTime(started);
        return failure is null
            ? new PluginTestResult(testCase.ClassName, testCase.DisplayName, PluginTestOutcome.Passed, duration, null, null)
            : new PluginTestResult(testCase.ClassName, testCase.DisplayName, PluginTestOutcome.Failed, duration,
                PluginTestFormatting.Message(failure.Message), PluginTestFormatting.StackTrace(failure.StackTrace));
    }

    private static PluginTestResult Timed(TestCase testCase, long started, string message) =>
        new(testCase.ClassName, testCase.DisplayName, PluginTestOutcome.TimedOut, Stopwatch.GetElapsedTime(started), message, null);

    private static void Abandon(Task<Exception?> execution) =>
        _ = execution.ContinueWith(static t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>Runs construction, setup, the test body and teardown; returns the first exception, or <see langword="null"/>.</summary>
    private static async Task<Exception?> ExecuteAsync(TestCase testCase)
    {
        object instance;
        try
        {
            instance = Activator.CreateInstance(testCase.Type)!;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException ?? ex;
        }

        Exception? failure = null;
        try
        {
            if (instance is IAsyncLifetime lifetime)
            {
                await lifetime.InitializeAsync().ConfigureAwait(false);
            }

            object? result = testCase.Method.Invoke(instance, testCase.Arguments);
            switch (result)
            {
                case Task task:
                    await task.ConfigureAwait(false);
                    break;
                case ValueTask valueTask:
                    await valueTask.ConfigureAwait(false);
                    break;
            }
        }
        catch (TargetInvocationException ex)
        {
            failure = ex.InnerException ?? ex;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            if (instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }

            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        return failure;
    }

    private sealed record TestCase(
        Type Type,
        MethodInfo Method,
        string ClassName,
        string DisplayName,
        object?[]? Arguments,
        (PluginTestOutcome Outcome, string Message)? Rejection);
}
