using Blocker.Contracts;
using Blocker.Core.PluginRuntime;
using Blocker.Sdk.Events;
using Blocker.Sdk.Model;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.PluginRuntime;

public sealed class HostProtocolTests
{
    private static readonly DateTimeOffset Now = new(2031, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly ProcessDescriptor Process = new(5, 1, "a.exe", "C:\\a.exe", "ab", null, Now);

    public static TheoryData<IBlockerEvent> Events => new()
    {
        new SessionStarted(Guid.NewGuid(), TimeSpan.FromMinutes(25), Now),
        new SessionEnded(Guid.NewGuid(), SessionEndReason.BreakGlass, Now),
        new EmergencyPassStarted(Guid.NewGuid(), TimeSpan.FromMinutes(5), 2, Now),
        new EmergencyPassEnded(Guid.NewGuid(), Now),
        new ProcessBlocked(Process, BlockReason.PluginPolicy, "com.test.p", Now),
        new ActiveWindowChanged(new WindowDescriptor(5, "a.exe", "C:\\a.exe", null, "Title"), Now),
        new UserIdleChanged(true, TimeSpan.FromSeconds(90), Now),
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void Core_events_round_trip_through_their_wire_form(IBlockerEvent evt)
    {
        HostEventDto dto = HostEventDto.From(evt)!;

        HostEventDto wire = IpcJson.FromElement<HostEventDto>(IpcJson.ToElement(dto));

        Assert.Equal(evt, wire.ToEvent());
    }

    [Fact]
    public void Custom_plugin_events_never_cross_the_boundary_and_incomplete_dtos_are_rejected()
    {
        Assert.Null(HostEventDto.From(new CustomPluginEvent("a.b/x", "a.b", "{}", Now)));
        Assert.Null(new HostEventDto(HostEventKind.SessionStarted, Now).ToEvent());
        Assert.Null(new HostEventDto(HostEventKind.UserIdleChanged, Now, IsIdle: true, Seconds: -1).ToEvent());
        Assert.Null(new HostEventDto(HostEventKind.UserIdleChanged, Now, IsIdle: true, Seconds: double.NaN).ToEvent());
    }

    [Fact]
    public void Host_methods_are_server_to_client_and_host_reports_are_plugin_host_only()
    {
        string[] toHost =
        [
            IpcMethods.HostLoadPlugins, IpcMethods.HostEvaluatePackage, IpcMethods.HostInterceptProcess, IpcMethods.HostInterceptWindow,
            IpcMethods.HostEvaluateLock, IpcMethods.HostRenderSlot, IpcMethods.HostSendUiAction, IpcMethods.HostInvokePluginMethod,
            IpcMethods.HostSessionChanged, IpcMethods.HostPublishEvent, IpcMethods.HostShutdown,
        ];
        foreach (string method in toHost)
        {
            Assert.True(IpcMethodPolicy.TryGet(method, out IpcMethodRule? rule), method);
            Assert.Equal(IpcCallerRole.None, rule.AllowedRoles);
        }

        foreach (string method in new[] { IpcMethods.PluginHostPluginFaulted, IpcMethods.PluginHostRenderRequested })
        {
            Assert.True(IpcMethodPolicy.TryGet(method, out IpcMethodRule? rule), method);
            Assert.Equal(IpcCallerRole.PluginHost, rule.AllowedRoles);
        }
    }

    [Fact]
    public void Install_reports_serialize_without_the_computed_member()
    {
        var report = new PluginInstallReportDto(PluginInstallStageDto.Tests, ["x"], "a.b", "N", "1.0.0", "A", ["tick"], 1, 2);

        string json = IpcJson.ToElement(report).GetRawText();

        Assert.DoesNotContain("succeeded", json, StringComparison.Ordinal);
        Assert.Equal(report.Errors, IpcJson.FromElement<PluginInstallReportDto>(IpcJson.ToElement(report)).Errors);
    }

    [Theory]
    [InlineData("pipe1", "/plugins", "/data", "abc")]
    [InlineData("pipe1", @"C:\Program Data\Blocker\plugins\", @"C:\Program Data\Blocker\data\", null)]
    [InlineData("p", "dir with \"quote\"", "tail\\\\", "t")]
    public void Arguments_survive_formatting_and_the_windows_command_line_rules(string pipe, string plugins, string data, string? token)
    {
        var arguments = new PluginHostArguments(pipe, plugins, data, token);

        string[] split = SplitWindowsCommandLine(arguments.Format());

        Assert.True(PluginHostArguments.TryParse(split, out PluginHostArguments? parsed, out string? error), error);
        Assert.Equal(arguments, parsed);
    }

    [Theory]
    [InlineData("--pipe", "p", "--plugins", "x")]
    [InlineData("--pipe", "p", "--plugins", "x", "--data", "d", "--unknown", "1")]
    [InlineData("--pipe", "p", "--pipe", "q", "--plugins", "x", "--data", "d")]
    [InlineData("--pipe", "p", "--plugins", "x", "--data")]
    [InlineData("--pipe", " ", "--plugins", "x", "--data", "d")]
    public void Malformed_arguments_are_refused(params string[] args)
    {
        Assert.False(PluginHostArguments.TryParse(args, out PluginHostArguments? parsed, out string? error));
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void Verified_time_falls_back_to_the_clock_before_an_anchor_and_follows_it_afterwards()
    {
        var clock = new FakeTimeProvider(Now);
        var verified = new VerifiedTimeProvider(clock);

        Assert.Equal(Now, verified.GetUtcNow());

        var anchor = Now.AddDays(-400);
        verified.Anchor(anchor);
        clock.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(anchor.AddSeconds(90), verified.GetUtcNow());
        Assert.Equal(clock.GetTimestamp(), verified.GetTimestamp());
        Assert.Equal(clock.TimestampFrequency, verified.TimestampFrequency);
    }

    [Fact]
    public async Task Timers_come_from_the_underlying_clock()
    {
        var clock = new FakeTimeProvider(Now);
        var verified = new VerifiedTimeProvider(clock);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), verified);

        Task<bool> tick = timer.WaitForNextTickAsync(TestContext.Current.CancellationToken).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await tick);
    }

    // Minimal CommandLineToArgvW: quotes group, backslashes before a quote are halved.
    private static string[] SplitWindowsCommandLine(string text)
    {
        List<string> args = [];
        System.Text.StringBuilder current = new();
        bool inQuotes = false;
        bool any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                int slashes = 0;
                while (i < text.Length && text[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }

                any = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            args.Add(current.ToString());
        }

        return [.. args];
    }
}
