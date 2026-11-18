using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class RollingFileLoggerTests
{
    private static readonly string Dir = MockUnixSupport.Path(@"C:\ProgramData\WindowsDetox\logs");

    private readonly MockFileSystem _fs = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 30, 15, 250, TimeSpan.Zero));

    private string[] Files() => [.. _fs.Directory.GetFiles(Dir).Select(_fs.Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Lines_carry_time_level_category_and_message_and_exceptions_follow_on_their_own_lines()
    {
        await using var provider = new RollingFileLoggerProvider(_fs, Dir, _time);
        ILogger logger = provider.CreateLogger("Blocker.Test");

        logger.LogInformation("Session {Id} started", 42);
        logger.LogError(new InvalidOperationException("boom"), "It failed");
        await provider.DisposeAsync();

        string text = _fs.File.ReadAllText(_fs.Path.Combine(Dir, "service-20261117.log"));
        string[] lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("2026-11-17T12:30:15.250Z [Information] Blocker.Test: Session 42 started", lines[0]);
        Assert.Equal("2026-11-17T12:30:15.250Z [Error] Blocker.Test: It failed", lines[1]);
        Assert.Contains("InvalidOperationException: boom", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Levels_below_the_minimum_are_not_written()
    {
        await using var provider = new RollingFileLoggerProvider(_fs, Dir, _time, new RollingFileLoggerOptions { MinimumLevel = LogLevel.Warning });
        ILogger logger = provider.CreateLogger("c");

        logger.LogInformation("quiet");
        logger.LogWarning("loud");
        await provider.DisposeAsync();

        string text = _fs.File.ReadAllText(_fs.Path.Combine(Dir, "service-20261117.log"));
        Assert.DoesNotContain("quiet", text, StringComparison.Ordinal);
        Assert.Contains("loud", text, StringComparison.Ordinal);
        Assert.False(logger.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public async Task A_full_file_is_closed_and_a_numbered_one_starts()
    {
        await using var provider = new RollingFileLoggerProvider(_fs, Dir, _time, new RollingFileLoggerOptions { MaxFileBytes = 100 });
        ILogger logger = provider.CreateLogger("c");

        for (int i = 0; i < 6; i++)
        {
            logger.LogInformation("{Line}", new string('x', 60));
        }

        await provider.DisposeAsync();

        string[] files = Files();
        Assert.Contains("service-20261117.log", files);
        Assert.Contains("service-20261117.1.log", files);
        Assert.True(files.Length >= 3);
        Assert.All(files, f => Assert.True(_fs.FileInfo.New(_fs.Path.Combine(Dir, f)).Length < 300));
    }

    [Fact]
    public async Task A_new_day_starts_a_new_file_and_files_older_than_the_retention_are_deleted()
    {
        _fs.Directory.CreateDirectory(Dir);
        _fs.File.WriteAllText(_fs.Path.Combine(Dir, "service-20261001.log"), "old");
        _fs.File.WriteAllText(_fs.Path.Combine(Dir, "service-20261110.log"), "recent");
        _fs.File.WriteAllText(_fs.Path.Combine(Dir, "notes.txt"), "not ours");
        await using var provider = new RollingFileLoggerProvider(_fs, Dir, _time, new RollingFileLoggerOptions { RetainDays = 14 });
        ILogger logger = provider.CreateLogger("c");

        logger.LogInformation("today");
        _time.Advance(TimeSpan.FromDays(1));
        logger.LogInformation("tomorrow");
        await provider.DisposeAsync();

        Assert.Equal(["notes.txt", "service-20261110.log", "service-20261117.log", "service-20261118.log"], Files());
    }

    [Fact]
    public async Task A_failing_disk_never_reaches_the_caller_and_is_counted()
    {
        await using var provider = new RollingFileLoggerProvider(_fs, Dir, _time);
        ILogger logger = provider.CreateLogger("c");
        _fs.Directory.CreateDirectory(Dir);
        _fs.File.WriteAllText(_fs.Path.Combine(Dir, "service-20261117.log"), "x");
        _fs.File.SetAttributes(_fs.Path.Combine(Dir, "service-20261117.log"), FileAttributes.ReadOnly);

        logger.LogInformation("lost");
        await provider.DisposeAsync();

        Assert.Equal(1, provider.FailedWrites);
    }
}
