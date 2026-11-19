using Blocker.Contracts;
using Blocker.Core.PluginRuntime;
using Blocker.Sdk;
using Blocker.Sdk.Notifications;
using Blocker.Sdk.Sessions;

namespace Blocker.Core.Tests.PluginRuntime;

public sealed class PluginHostServicesTests
{
    private static readonly PluginId Id = new("com.test.calendar");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PluginInfo Plugin(params PluginCapability[] capabilities) =>
        new(Id, "Calendar", new Version(1, 0, 0), "Tester", capabilities.ToHashSet());

    [Fact]
    public async Task Starting_a_session_sends_whole_minutes_and_a_cleaned_reason()
    {
        var channel = new FakeChannel();
        var launcher = new HostSessionLauncher(Plugin(PluginCapability.SessionStart), channel);

        SessionStartResult result = await launcher.StartAsync(TimeSpan.FromMinutes(90.2), "Deep\nwork " + new string('x', 200), Ct);

        Assert.True(result.Started);
        PluginStartSessionRequest request = Assert.Single(channel.StartRequests);
        Assert.Equal(91, request.DurationMinutes);
        Assert.StartsWith("Deep?work x", request.Reason, StringComparison.Ordinal);
        Assert.Equal(SessionLaunchLimits.MaxReasonLength, request.Reason.Length);
        Assert.Equal(Id.Value, request.PluginId);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(721)]
    public async Task A_session_length_out_of_range_is_refused_without_calling_the_service(int minutes)
    {
        var channel = new FakeChannel();
        var launcher = new HostSessionLauncher(Plugin(PluginCapability.SessionStart), channel);

        SessionStartResult result = await launcher.StartAsync(TimeSpan.FromMinutes(minutes), "x", Ct);

        Assert.False(result.Started);
        Assert.Empty(channel.StartRequests);
    }

    [Fact]
    public async Task A_refused_session_start_reports_the_services_reason()
    {
        var channel = new FakeChannel { Start = _ => new PluginStartSessionResponse(false, "A session is already running.") };
        var launcher = new HostSessionLauncher(Plugin(PluginCapability.SessionStart), channel);

        SessionStartResult result = await launcher.StartAsync(TimeSpan.FromHours(1), "x", Ct);

        Assert.Equal(new SessionStartResult(false, "A session is already running."), result);
    }

    [Fact]
    public async Task Starting_a_session_needs_the_capability()
    {
        var launcher = new HostSessionLauncher(Plugin(PluginCapability.Tick), new FakeChannel());

        await Assert.ThrowsAsync<PluginCapabilityException>(async () => await launcher.StartAsync(TimeSpan.FromHours(1), "x", Ct));
    }

    [Fact]
    public async Task Toasts_are_cleaned_cut_and_need_the_capability()
    {
        var channel = new FakeChannel();
        var notifier = new HostPluginNotifier(Plugin(PluginCapability.Notify), channel);

        bool shown = await notifier.ShowAsync("a\tb" + new string('y', 300), Ct);
        bool empty = await notifier.ShowAsync("   ", Ct);

        Assert.True(shown);
        Assert.False(empty);
        PluginNotifyRequest request = Assert.Single(channel.NotifyRequests);
        Assert.StartsWith("a?by", request.Message, StringComparison.Ordinal);
        Assert.Equal(PluginNotifierLimits.MaxMessageLength, request.Message.Length);
        await Assert.ThrowsAsync<PluginCapabilityException>(async () => await new HostPluginNotifier(Plugin(), new FakeChannel()).ShowAsync("x", Ct));
    }

    private sealed class FakeChannel : IPluginHostServiceChannel
    {
        public Func<PluginStartSessionRequest, PluginStartSessionResponse> Start { get; set; } = static _ => new(true, null);

        public List<PluginStartSessionRequest> StartRequests { get; } = [];

        public List<PluginNotifyRequest> NotifyRequests { get; } = [];

        public ValueTask<PluginStartSessionResponse> StartSessionAsync(PluginStartSessionRequest request, CancellationToken cancellationToken)
        {
            StartRequests.Add(request);
            return ValueTask.FromResult(Start(request));
        }

        public ValueTask<PluginNotifyResponse> NotifyAsync(PluginNotifyRequest request, CancellationToken cancellationToken)
        {
            NotifyRequests.Add(request);
            return ValueTask.FromResult(new PluginNotifyResponse(true));
        }
    }
}
