using Blocker.Platform.Windows.Changes;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class ServiceCommandLineTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("abc", "abc")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\tb", "\"a\tb\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\", "a\\")]
    [InlineData("a b\\", "\"a b\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("C:\\Program Files\\x", "\"C:\\Program Files\\x\"")]
    public void QuoteArgument_follows_argv_rules(string argument, string expected) =>
        Assert.Equal(expected, ServiceCommandLine.QuoteArgument(argument));

    [Fact]
    public void JoinArguments_quotes_each_argument() =>
        Assert.Equal("--data \"C:\\Program Data\" x", ServiceCommandLine.JoinArguments(["--data", @"C:\Program Data", "x"]));

    [Fact]
    public void Build_always_quotes_the_executable()
    {
        Assert.Equal("\"C:\\Program Files\\Blocker\\svc.exe\" --service", ServiceCommandLine.Build(@"C:\Program Files\Blocker\svc.exe", "--service"));
        Assert.Equal("\"C:\\Blocker\\svc.exe\"", ServiceCommandLine.Build(@"C:\Blocker\svc.exe", ""));
        Assert.Equal("\"C:\\Blocker\\svc.exe\"", ServiceCommandLine.Build(@"C:\Blocker\svc.exe", null));
        Assert.Equal("\"C:\\Blocker\\svc.exe\" --a", ServiceCommandLine.Build(@"C:\Blocker\svc.exe", "  --a  "));
    }

    [Theory]
    [InlineData("svc.exe")]
    [InlineData(@"C:\a\..\svc.exe")]
    [InlineData(@"\\server\share\svc.exe")]
    [InlineData("C:\\a\"b\\svc.exe")]
    [InlineData("")]
    public void Build_refuses_non_normalized_executables(string exe) =>
        Assert.Throws<ArgumentException>(() => ServiceCommandLine.Build(exe, ""));

    [Theory]
    [InlineData("--a\n--b")]
    [InlineData("--a\0")]
    public void Build_refuses_control_characters_in_arguments(string arguments) =>
        Assert.Throws<ArgumentException>(() => ServiceCommandLine.Build(@"C:\x\svc.exe", arguments));

    [Fact]
    public void WaitUntilStopped_returns_true_as_soon_as_the_service_is_stopped()
    {
        var time = new FakeTimeProvider();
        long start = time.GetTimestamp();
        uint[] states = [4, 3, 3, 1];
        int i = 0;

        bool stopped = ServiceCommandLine.WaitUntilStopped(() => states[i++], time, time.Advance, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250));

        Assert.True(stopped);
        Assert.Equal(4, i);
        Assert.Equal(TimeSpan.FromMilliseconds(750), time.GetElapsedTime(start));
    }

    [Fact]
    public void WaitUntilStopped_gives_up_after_the_timeout()
    {
        var time = new FakeTimeProvider();
        int polls = 0;

        bool stopped = ServiceCommandLine.WaitUntilStopped(() => { polls++; return 3; }, time, time.Advance, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        Assert.False(stopped);
        Assert.Equal(31, polls);
    }
}
