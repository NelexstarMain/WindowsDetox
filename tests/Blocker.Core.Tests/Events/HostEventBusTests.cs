using Blocker.Core.Events;
using Blocker.Core.Resilience;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Blocker.Sdk.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.Events;

public sealed class HostEventBusTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(At);
    private readonly HostEventBus _bus;

    public HostEventBusTests() => _bus = new HostEventBus(_time, NullLogger<HostEventBus>.Instance);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _bus.DisposeAsync();

    private static PluginInfo Plugin(string id, params PluginCapability[] caps) =>
        new(new PluginId(id), id, new Version(1, 0), "test", new HashSet<PluginCapability>(caps));

    private PluginCircuitBreaker Breaker(PluginInfo p) => new(p.Id, _time);

    private IEventBus View(PluginInfo p, out PluginCircuitBreaker breaker)
    {
        breaker = Breaker(p);
        return _bus.CreatePluginView(p, breaker);
    }

    private static SessionStarted Started(int minutes = 30) => new(Guid.Empty, TimeSpan.FromMinutes(minutes), At);

    private static ActiveWindowChanged Window(string? title) =>
        new(new WindowDescriptor(7, "app.exe", @"C:\app.exe", null, title), At);

    private static Task<T> Next<T>(IEventBus bus)
        where T : class, IBlockerEvent
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Subscribe<T>((e, _) =>
        {
            tcs.TrySetResult(e);
            return ValueTask.CompletedTask;
        });
        return tcs.Task;
    }

    [Fact]
    public async Task Delivers_core_event_to_capable_subscriber()
    {
        IEventBus view = View(Plugin("com.example.a", PluginCapability.SessionEvents), out _);
        Task<SessionStarted> next = Next<SessionStarted>(view);

        await _bus.PublishAsync(Started(), Ct);

        Assert.Equal(Started(), await next.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public void Subscribe_requires_the_capability_of_each_event_type()
    {
        (PluginCapability Required, Func<IEventBus, IDisposable> Subscribe)[] cases =
        [
            (PluginCapability.SessionEvents, v => v.Subscribe<SessionStarted>((_, _) => ValueTask.CompletedTask)),
            (PluginCapability.SessionEvents, v => v.Subscribe<SessionEnded>((_, _) => ValueTask.CompletedTask)),
            (PluginCapability.ProcessEvents, v => v.Subscribe<ProcessBlocked>((_, _) => ValueTask.CompletedTask)),
            (PluginCapability.WindowEvents, v => v.Subscribe<ActiveWindowChanged>((_, _) => ValueTask.CompletedTask)),
            (PluginCapability.IdleEvents, v => v.Subscribe<UserIdleChanged>((_, _) => ValueTask.CompletedTask)),
            (PluginCapability.CustomEvents, v => v.Subscribe<CustomPluginEvent>((_, _) => ValueTask.CompletedTask)),
        ];

        foreach ((PluginCapability required, Func<IEventBus, IDisposable> subscribe) in cases)
        {
            IEventBus none = View(Plugin("com.example.none"), out _);
            IEventBus granted = View(Plugin("com.example.granted", required), out _);

            Assert.Equal(required, Assert.Throws<PluginCapabilityException>(() => subscribe(none)).Capability);
            subscribe(granted).Dispose();
        }
    }


    [Fact]
    public async Task Window_title_is_stripped_without_title_capability()
    {
        IEventBus plain = View(Plugin("com.example.plain", PluginCapability.WindowEvents), out _);
        IEventBus titled = View(Plugin("com.example.titled", PluginCapability.WindowEvents, PluginCapability.WindowTitles), out _);
        Task<ActiveWindowChanged> plainNext = Next<ActiveWindowChanged>(plain);
        Task<ActiveWindowChanged> titledNext = Next<ActiveWindowChanged>(titled);

        await _bus.PublishAsync(Window("Secret doc"), Ct);

        Assert.Null((await plainNext.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Window.Title);
        Assert.Equal("Secret doc", (await titledNext.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Window.Title);
        Assert.Equal(7, (await plainNext).Window.ProcessId);
    }

    [Fact]
    public async Task Custom_event_round_trip_uses_plugin_topic()
    {
        PluginInfo publisher = Plugin("com.example.pub", PluginCapability.CustomEvents);
        IEventBus pub = View(publisher, out _);
        IEventBus sub = View(Plugin("com.example.sub", PluginCapability.CustomEvents), out _);
        var tcs = new TaskCompletionSource<CustomPluginEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        sub.SubscribeCustom("com.example.pub/focus.changed", (e, _) =>
        {
            tcs.TrySetResult(e);
            return ValueTask.CompletedTask;
        });
        var other = new List<CustomPluginEvent>();
        sub.SubscribeCustom("com.example.pub/other", (e, _) =>
        {
            other.Add(e);
            return ValueTask.CompletedTask;
        });

        await pub.PublishCustomAsync("focus.changed", """{"a":1}""", Ct);

        Assert.Equal(
            new CustomPluginEvent("com.example.pub/focus.changed", "com.example.pub", """{"a":1}""", At),
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await _bus.StopAsync(Ct);
        Assert.Empty(other);
    }

    [Fact]
    public async Task Custom_apis_require_custom_events_capability()
    {
        IEventBus view = View(Plugin("com.example.none", PluginCapability.SessionEvents), out _);

        Assert.Equal(PluginCapability.CustomEvents, Assert.Throws<PluginCapabilityException>(() => view.SubscribeCustom("t", Noop)).Capability);
        Assert.Equal(
            PluginCapability.CustomEvents,
            (await Assert.ThrowsAsync<PluginCapabilityException>(async () => await view.PublishCustomAsync("e", "{}", Ct))).Capability);
    }

    private static ValueTask Noop(CustomPluginEvent _, CancellationToken __) => ValueTask.CompletedTask;

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("ünï")]
    public async Task Invalid_event_name_is_rejected(string name)
    {
        IEventBus view = View(Plugin("com.example.c", PluginCapability.CustomEvents), out _);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await view.PublishCustomAsync(name, "{}", Ct));

        Assert.Equal("eventName", ex.ParamName);
    }

    [Fact]
    public async Task Event_name_length_limit_is_64()
    {
        IEventBus view = View(Plugin("com.example.c", PluginCapability.CustomEvents), out _);

        await view.PublishCustomAsync(new string('a', 64), "{}", Ct);
        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await view.PublishCustomAsync(new string('a', 65), "{}", Ct));

        Assert.Equal("eventName", ex.ParamName);
    }

    [Fact]
    public async Task Invalid_payload_is_rejected()
    {
        IEventBus view = View(Plugin("com.example.c", PluginCapability.CustomEvents), out _);

        var syntax = await Assert.ThrowsAsync<ArgumentException>(async () => await view.PublishCustomAsync("e", "{nope", Ct));
        string bigString = "\"" + new string('x', BlockerSdk.MaxPayloadBytes - 1) + "\"";
        var tooBig = await Assert.ThrowsAsync<ArgumentException>(async () => await view.PublishCustomAsync("e", bigString, Ct));
        await view.PublishCustomAsync("e", "\"" + new string('x', BlockerSdk.MaxPayloadBytes - 2) + "\"", Ct);

        Assert.Equal("payloadJson", syntax.ParamName);
        Assert.StartsWith("Payload is not valid JSON", syntax.Message, StringComparison.Ordinal);
        Assert.Equal("payloadJson", tooBig.ParamName);
        Assert.StartsWith("Payload exceeds 65536 bytes.", tooBig.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payload_size_is_measured_in_utf8_bytes()
    {
        IEventBus view = View(Plugin("com.example.c", PluginCapability.CustomEvents), out _);
        string payload = "\"" + new string('é', BlockerSdk.MaxPayloadBytes / 2) + "\"";

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await view.PublishCustomAsync("e", payload, Ct));

        Assert.Equal("payloadJson", ex.ParamName);
    }

    [Fact]
    public async Task Slow_plugin_does_not_block_fast_plugin()
    {
        IEventBus slow = View(Plugin("com.example.slow", PluginCapability.SessionEvents), out PluginCircuitBreaker slowBreaker);
        IEventBus fast = View(Plugin("com.example.fast", PluginCapability.SessionEvents), out _);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.Subscribe<SessionStarted>((_, _) =>
        {
            slowStarted.TrySetResult();
            return new ValueTask(never.Task);
        });
        Task<SessionStarted> fastDone = Next<SessionStarted>(fast);

        await _bus.PublishAsync(Started(), Ct);

        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(Started(), await fastDone.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(never.Task.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(BlockerSdk.CallbackBudgetMilliseconds));
        await _bus.StopAsync(Ct);
        Assert.Equal(BreakerState.Closed, slowBreaker.State);
    }

    [Fact]
    public async Task Repeatedly_failing_plugin_is_faulted_and_then_skipped()
    {
        PluginInfo bad = Plugin("com.example.bad", PluginCapability.SessionEvents);
        IEventBus badView = View(bad, out PluginCircuitBreaker breaker);
        IEventBus good = View(Plugin("com.example.good", PluginCapability.SessionEvents), out _);
        int badCalls = 0;
        badView.Subscribe<SessionStarted>((_, _) =>
        {
            badCalls++;
            throw new InvalidOperationException("handler failed");
        });
        int goodCalls = 0;
        good.Subscribe<SessionStarted>((_, _) =>
        {
            Interlocked.Increment(ref goodCalls);
            return ValueTask.CompletedTask;
        });

        for (int i = 0; i < 8; i++)
        {
            await _bus.PublishAsync(Started(i + 1), Ct);
        }

        await _bus.StopAsync(Ct);

        Assert.Equal(BreakerState.Faulted, breaker.State);
        Assert.Equal(5, badCalls);
        Assert.Equal(8, goodCalls);
    }

    [Fact]
    public async Task Disposing_registration_unsubscribes()
    {
        IEventBus view = View(Plugin("com.example.a", PluginCapability.SessionEvents), out _);
        var received = new List<int>();
        IDisposable sub = view.Subscribe<SessionStarted>((e, _) =>
        {
            received.Add((int)e.PlannedDuration.TotalMinutes);
            return ValueTask.CompletedTask;
        });
        Task<SessionStarted> sentinel = Next<SessionStarted>(view);

        await _bus.PublishAsync(Started(1), Ct);
        await sentinel.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sub.Dispose();
        sub.Dispose();
        await _bus.PublishAsync(Started(2), Ct);
        await _bus.StopAsync(Ct);

        Assert.Equal([1], received);
    }

    [Fact]
    public async Task RemovePlugin_removes_all_its_subscriptions_only()
    {
        PluginInfo a = Plugin("com.example.a", PluginCapability.SessionEvents, PluginCapability.IdleEvents);
        IEventBus viewA = View(a, out _);
        IEventBus viewB = View(Plugin("com.example.b", PluginCapability.SessionEvents), out _);
        int aCalls = 0;
        viewA.Subscribe<SessionStarted>((_, _) => { aCalls++; return ValueTask.CompletedTask; });
        viewA.Subscribe<UserIdleChanged>((_, _) => { aCalls++; return ValueTask.CompletedTask; });
        int bCalls = 0;
        viewB.Subscribe<SessionStarted>((_, _) => { bCalls++; return ValueTask.CompletedTask; });

        _bus.RemovePlugin(a.Id);
        await _bus.PublishAsync(Started(), Ct);
        await _bus.PublishAsync(new UserIdleChanged(true, TimeSpan.FromMinutes(5), At), Ct);
        await _bus.StopAsync(Ct);

        Assert.Equal(0, aCalls);
        Assert.Equal(1, bCalls);
    }

    [Fact]
    public async Task Full_queue_drops_oldest_and_counts()
    {
        await using var bus = new HostEventBus(_time, NullLogger<HostEventBus>.Instance, capacity: 2);
        PluginInfo p = Plugin("com.example.a", PluginCapability.SessionEvents);
        IEventBus view = bus.CreatePluginView(p, Breaker(p));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var minutes = new List<int>();
        view.Subscribe<SessionStarted>(async (e, _) =>
        {
            minutes.Add((int)e.PlannedDuration.TotalMinutes);
            firstStarted.TrySetResult();
            await gate.Task;
        });

        await bus.PublishAsync(Started(1), Ct);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await bus.PublishAsync(Started(2), Ct);
        await bus.PublishAsync(Started(3), Ct);
        Assert.Equal(0, bus.DroppedEvents);
        await bus.PublishAsync(Started(4), Ct);
        await bus.PublishAsync(Started(5), Ct);
        Assert.Equal(2, bus.DroppedEvents);

        gate.SetResult();
        await bus.StopAsync(Ct);

        Assert.Equal([1, 4, 5], minutes);
        Assert.Equal(2, bus.DroppedEvents);
    }

    [Fact]
    public async Task StopAsync_drains_queued_events()
    {
        IEventBus view = View(Plugin("com.example.a", PluginCapability.SessionEvents), out _);
        int count = 0;
        view.Subscribe<SessionStarted>((_, _) => { count++; return ValueTask.CompletedTask; });

        for (int i = 0; i < 100; i++)
        {
            await _bus.PublishAsync(Started(), Ct);
        }

        await _bus.StopAsync(Ct);

        Assert.Equal(100, count);
        Assert.Equal(0, _bus.DroppedEvents);
    }

    [Fact]
    public async Task Publish_after_dispose_is_ignored_without_throwing()
    {
        var bus = new HostEventBus(_time, NullLogger<HostEventBus>.Instance);
        PluginInfo p = Plugin("com.example.a", PluginCapability.SessionEvents);
        IEventBus view = bus.CreatePluginView(p, Breaker(p));
        int count = 0;
        view.Subscribe<SessionStarted>((_, _) => { count++; return ValueTask.CompletedTask; });

        await bus.DisposeAsync();
        await bus.PublishAsync(Started(), Ct);
        await bus.DisposeAsync();

        Assert.Equal(0, count);
        Assert.Equal(0, bus.DroppedEvents);
    }

    [Fact]
    public async Task Publish_cancelled_token_throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _bus.PublishAsync(Started(), cts.Token));
    }

    [Fact]
    public async Task Cancelling_stop_abandons_hung_handlers()
    {
        IEventBus view = View(Plugin("com.example.a", PluginCapability.SessionEvents), out _);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Subscribe<SessionStarted>((_, _) =>
        {
            started.TrySetResult();
            return new ValueTask(never.Task);
        });
        await _bus.PublishAsync(Started(), Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var cts = new CancellationTokenSource();

        Task stop = _bus.StopAsync(cts.Token);
        Assert.False(stop.IsCompleted);
        await cts.CancelAsync();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(stop.IsCompletedSuccessfully);
    }

    [Fact]
    public void View_arguments_are_validated()
    {
        PluginInfo p = Plugin("com.example.a", PluginCapability.SessionEvents);

        Assert.Throws<ArgumentNullException>(() => _bus.CreatePluginView(null!, Breaker(p)));
        Assert.Throws<ArgumentNullException>(() => _bus.CreatePluginView(p, null!));
        Assert.Throws<ArgumentNullException>(() => View(p, out _).Subscribe<SessionStarted>(null!));
    }
}
