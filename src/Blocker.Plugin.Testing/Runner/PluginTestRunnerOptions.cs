namespace Blocker.Plugin.Testing.Runner;

/// <summary>Limits applied by <see cref="PluginTestRunner"/>.</summary>
public sealed record PluginTestRunnerOptions
{
    /// <summary>Gets the maximum duration of one test including setup and teardown. Default 5 seconds.</summary>
    public TimeSpan PerTestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the maximum duration of the whole run; tests not started by then are reported as timed out. Default 60 seconds.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
