using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Sessions;

namespace Blocker.Plugin.Testing.Tests;

public sealed class FakeHostServicesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_new_service_throws_without_its_capability()
    {
        var context = new FakePluginContext("com.example.test-plugin", PluginCapability.Tick);

        Assert.Equal(PluginCapability.SessionStart, (await Assert.ThrowsAsync<PluginCapabilityException>(async () => await context.Sessions.StartAsync(TimeSpan.FromHours(1), "x", Ct))).Capability);
        Assert.Equal(PluginCapability.Notify, (await Assert.ThrowsAsync<PluginCapabilityException>(async () => await context.Notifier.ShowAsync("x", Ct))).Capability);
    }

    [Fact]
    public async Task Sessions_are_recorded_and_the_result_can_be_scripted()
    {
        var context = new FakePluginContext();

        SessionStartResult ok = await context.Sessions.StartAsync(TimeSpan.FromHours(2), "Morning", Ct);
        context.FakeSessions.NextResult = new SessionStartResult(false, "busy");
        SessionStartResult busy = await context.Sessions.StartAsync(TimeSpan.FromMinutes(30), "Later", Ct);
        SessionStartResult tooShort = await context.Sessions.StartAsync(TimeSpan.FromMinutes(5), "Short", Ct);

        Assert.True(ok.Started);
        Assert.Equal("busy", busy.Error);
        Assert.False(tooShort.Started);
        Assert.Equal([(TimeSpan.FromHours(2), "Morning"), (TimeSpan.FromMinutes(30), "Later")], context.FakeSessions.Calls);
    }

    [Fact]
    public async Task Toasts_are_recorded_and_cut_to_the_limit()
    {
        var context = new FakePluginContext();

        Assert.True(await context.Notifier.ShowAsync("hello", Ct));
        Assert.True(await context.Notifier.ShowAsync(new string('z', 500), Ct));

        Assert.Equal(2, context.FakeNotifier.Messages.Count);
        Assert.Equal(200, context.FakeNotifier.Messages[1].Length);
    }
}
