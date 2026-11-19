using Blocker.Contracts;
using Blocker.Core.ServiceHost;
using Blocker.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class PluginHostRequestHandlersTests
{
    private const string Id = "com.test.calendar";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingAgent _agent = new();
    private readonly PluginHostRequestHandlers _handlers;
    private readonly List<int> _started = [];
    private (bool Started, string? Error) _startResult = (true, null);

    public PluginHostRequestHandlersTests()
    {
        _handlers = Create(["session.start", "notify"]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PluginHostRequestHandlers Create(string[] capabilities, bool enabled = true) =>
        new(
            new PluginHostRequestDependencies(
                (id, _) => ValueTask.FromResult<PluginGrant?>(enabled && id == Id ? new PluginGrant("Calendar", capabilities.ToHashSet(StringComparer.Ordinal)) : null),
                _agent,
                (minutes, _) =>
                {
                    _started.Add(minutes);
                    return ValueTask.FromResult(_startResult);
                },
                _time),
            NullLogger<PluginHostRequestHandlers>.Instance);

    [Fact]
    public async Task A_disabled_or_unknown_plugin_is_refused_everywhere()
    {
        PluginHostRequestHandlers disabled = Create(["session.start", "notify"], enabled: false);

        Assert.False((await disabled.StartSessionAsync(new(Id, 60, "x"), Ct)).Started);
        Assert.False((await disabled.NotifyAsync(new(Id, "x"), Ct)).Shown);
        Assert.False((await _handlers.StartSessionAsync(new("com.test.stranger", 60, "x"), Ct)).Started);
        Assert.False((await _handlers.StartSessionAsync(new("../escape", 60, "x"), Ct)).Started);
        Assert.Empty(_started);
        Assert.Empty(_agent.Toasts);
    }

    [Fact]
    public async Task A_started_session_is_announced_by_a_toast_and_never_reveals_a_break_glass_code()
    {
        PluginStartSessionResponse response = await _handlers.StartSessionAsync(new(Id, 120, "Morning focus"), Ct);

        Assert.True(response.Started);
        Assert.Equal([120], _started);
        NotificationDto toast = Assert.Single(_agent.Toasts);
        Assert.Equal("Focus session started", toast.Title);
        Assert.Contains("Calendar started a 2 h session: Morning focus", toast.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be shortened", toast.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(PluginStartSessionResponse).GetProperties().Select(p => p.Name).Order(), ["Error", "Started"]);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(721)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_session_length_outside_fifteen_minutes_to_twelve_hours_is_refused(int minutes)
    {
        PluginStartSessionResponse response = await _handlers.StartSessionAsync(new(Id, minutes, "x"), Ct);

        Assert.False(response.Started);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task The_service_refusing_a_session_is_reported_and_does_not_use_up_the_budget()
    {
        _startResult = (false, "A session is already running.");
        PluginStartSessionResponse refused = await _handlers.StartSessionAsync(new(Id, 60, "x"), Ct);
        _startResult = (true, null);
        PluginStartSessionResponse retried = await _handlers.StartSessionAsync(new(Id, 60, "x"), Ct);

        Assert.False(refused.Started);
        Assert.Equal("A session is already running.", refused.Error);
        Assert.True(retried.Started);
        Assert.Single(_agent.Toasts);
    }

    [Fact]
    public async Task Two_sessions_may_not_start_within_two_minutes_but_may_after_that()
    {
        Assert.True((await _handlers.StartSessionAsync(new(Id, 60, "a"), Ct)).Started);
        _time.Advance(TimeSpan.FromSeconds(90));
        Assert.False((await _handlers.StartSessionAsync(new(Id, 60, "b"), Ct)).Started);
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await _handlers.StartSessionAsync(new(Id, 60, "c"), Ct)).Started);
        Assert.Equal([60, 60], _started);
    }

    [Fact]
    public async Task A_plugin_may_start_at_most_sixteen_hours_of_sessions_per_day()
    {
        for (int i = 0; i < 4; i++)
        {
            Assert.True((await _handlers.StartSessionAsync(new(Id, 240, "block"), Ct)).Started);
            _time.Advance(TimeSpan.FromMinutes(5));
        }

        PluginStartSessionResponse over = await _handlers.StartSessionAsync(new(Id, 15, "one more"), Ct);
        _time.Advance(TimeSpan.FromHours(24));
        PluginStartSessionResponse nextDay = await _handlers.StartSessionAsync(new(Id, 15, "tomorrow"), Ct);

        Assert.False(over.Started);
        Assert.Contains("per day", over.Error, StringComparison.Ordinal);
        Assert.True(nextDay.Started);
    }

    [Fact]
    public async Task Without_the_session_capability_no_session_starts()
    {
        PluginHostRequestHandlers handlers = Create(["secrets", "notify"]);

        Assert.False((await handlers.StartSessionAsync(new(Id, 60, "x"), Ct)).Started);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task A_toast_carries_the_plugin_name_as_title_and_cleaned_text()
    {
        PluginNotifyResponse response = await _handlers.NotifyAsync(new(Id, "Starts in 1 min\r\nGet ready"), Ct);

        Assert.True(response.Shown);
        NotificationDto toast = Assert.Single(_agent.Toasts);
        Assert.Equal("Calendar", toast.Title);
        Assert.Equal("Starts in 1 min??Get ready", toast.Message);
    }

    [Fact]
    public async Task Toasts_are_limited_to_twenty_per_hour_and_empty_text_is_dropped()
    {
        for (int i = 0; i < 20; i++)
        {
            Assert.True((await _handlers.NotifyAsync(new(Id, "hi"), Ct)).Shown);
        }

        Assert.False((await _handlers.NotifyAsync(new(Id, "hi"), Ct)).Shown);
        _time.Advance(TimeSpan.FromMinutes(61));
        Assert.True((await _handlers.NotifyAsync(new(Id, "hi"), Ct)).Shown);
        Assert.False((await _handlers.NotifyAsync(new(Id, "  "), Ct)).Shown);
    }

    [Fact]
    public async Task A_toast_without_a_connected_agent_is_not_counted_as_shown()
    {
        _agent.Connected = false;

        Assert.False((await _handlers.NotifyAsync(new(Id, "hi"), Ct)).Shown);
        Assert.Empty(_agent.Toasts);
    }

    private sealed class RecordingAgent : IAgentToasts
    {
        public List<NotificationDto> Toasts { get; } = [];

        public bool Connected { get; set; } = true;

        public bool IsConnected => Connected;

        public ValueTask NotifyAsync(NotificationDto notification, CancellationToken cancellationToken)
        {
            if (Connected)
            {
                Toasts.Add(notification);
            }

            return ValueTask.CompletedTask;
        }
    }
}
