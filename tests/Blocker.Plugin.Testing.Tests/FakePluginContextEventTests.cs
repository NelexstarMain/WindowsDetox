using Blocker.Sdk.Events;
using Blocker.Sdk.Model;

namespace Blocker.Plugin.Testing.Tests;

public sealed class FakePluginContextEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<PluginCapability, Func<IEventBus, IDisposable>> CoreSubscriptions => new()
    {
        { PluginCapability.SessionEvents, bus => bus.Subscribe<SessionStarted>((_, _) => ValueTask.CompletedTask) },
        { PluginCapability.SessionEvents, bus => bus.Subscribe<SessionEnded>((_, _) => ValueTask.CompletedTask) },
        { PluginCapability.SessionEvents, bus => bus.Subscribe<EmergencyPassStarted>((_, _) => ValueTask.CompletedTask) },
        { PluginCapability.SessionEvents, bus => bus.Subscribe<EmergencyPassEnded>((_, _) => ValueTask.CompletedTask) },
        { PluginCapability.ProcessEvents, bus => bus.Subscribe<ProcessBlocked>((_, _) => ValueTask.CompletedTask) },
        { PluginCapability.WindowEvents, bus => bus.Subscribe<ActiveWindowChanged>((_, _) => ValueTask.CompletedTask) },
        { PluginCapability.IdleEvents, bus => bus.Subscribe<UserIdleChanged>((_, _) => ValueTask.CompletedTask) },
    };

    [Fact]
    public void Constructor_WithoutCapabilities_GrantsAll()
    {
        var context = new FakePluginContext();

        Assert.Equal("com.example.test-plugin", context.Plugin.Id.Value);
        Assert.Equal(Enum.GetValues<PluginCapability>().Length, context.Plugin.Capabilities.Count);
        Assert.Equal(Now, context.Time.GetUtcNow());
        Assert.Same(context.FakeStorage, context.Storage);
        Assert.Same(context.FakeSession, context.Session);
        Assert.Same(context.FakeLogger, context.Logger);
    }

    [Fact]
    public void Constructor_WithCapabilities_GrantsOnlyThose()
    {
        var context = new FakePluginContext("com.example.other", PluginCapability.Tick, PluginCapability.Ipc);

        Assert.Equal("com.example.other", context.Plugin.Id.Value);
        Assert.Equal([PluginCapability.Tick, PluginCapability.Ipc], context.Plugin.Capabilities.Order());
    }

    [Fact]
    public void Constructor_InvalidPluginId_Throws() =>
        Assert.Throws<ArgumentException>(() => new FakePluginContext("Not Valid"));

    [Theory]
    [MemberData(nameof(CoreSubscriptions))]
    public void Subscribe_WithRequiredCapability_Succeeds(PluginCapability required, Func<IEventBus, IDisposable> subscribe)
    {
        var context = new FakePluginContext("com.example.test-plugin", required);

        using IDisposable registration = subscribe(context.Events);

        Assert.NotNull(registration);
    }

    [Theory]
    [MemberData(nameof(CoreSubscriptions))]
    public void Subscribe_WithoutRequiredCapability_ThrowsCapabilityException(PluginCapability required, Func<IEventBus, IDisposable> subscribe)
    {
        PluginCapability other = required == PluginCapability.Network ? PluginCapability.Tick : PluginCapability.Network;
        var context = new FakePluginContext("com.example.test-plugin", other);

        var ex = Assert.Throws<PluginCapabilityException>(() => subscribe(context.Events));

        Assert.Equal(required, ex.Capability);
    }

    [Fact]
    public async Task PublishAsync_DeliversSequentiallyInSubscriptionOrder()
    {
        var context = new FakePluginContext();
        List<string> log = [];
        context.Events.Subscribe<SessionStarted>(async (e, _) =>
        {
            log.Add("first:start");
            await Task.Yield();
            log.Add("first:end");
        });
        context.Events.Subscribe<SessionStarted>((e, _) =>
        {
            log.Add($"second:{e.PlannedDuration.TotalMinutes}");
            return ValueTask.CompletedTask;
        });
        context.Events.Subscribe<SessionEnded>((_, _) =>
        {
            log.Add("wrong");
            return ValueTask.CompletedTask;
        });

        await context.PublishAsync(new SessionStarted(Guid.Empty, TimeSpan.FromMinutes(30), Now), TestContext.Current.CancellationToken);

        Assert.Equal(["first:start", "first:end", "second:30"], log);
    }

    [Fact]
    public async Task PublishAsync_AfterDispose_DoesNotDeliver()
    {
        var context = new FakePluginContext();
        int calls = 0;
        IDisposable registration = context.Events.Subscribe<ProcessBlocked>((_, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });
        var blocked = new ProcessBlocked(PluginTestData.Process(), BlockReason.BypassTool, null, Now);

        await context.PublishAsync(blocked, TestContext.Current.CancellationToken);
        registration.Dispose();
        registration.Dispose();
        await context.PublishAsync(blocked, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PublishAsync_HandlerException_Propagates()
    {
        var context = new FakePluginContext();
        context.Events.Subscribe<UserIdleChanged>((_, _) => throw new InvalidOperationException("handler failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.PublishAsync(new UserIdleChanged(true, TimeSpan.FromMinutes(1), Now), TestContext.Current.CancellationToken));

        Assert.Equal("handler failed", ex.Message);
    }

    [Fact]
    public async Task PublishCustomAsync_UsesPluginIdTopicAndFakeClock()
    {
        var context = new FakePluginContext("com.example.sender");
        List<CustomPluginEvent> topicEvents = [];
        List<CustomPluginEvent> otherEvents = [];
        List<CustomPluginEvent> typedEvents = [];
        context.Events.SubscribeCustom("com.example.sender/my.event", (e, _) => { topicEvents.Add(e); return ValueTask.CompletedTask; });
        context.Events.SubscribeCustom("com.example.sender/other", (e, _) => { otherEvents.Add(e); return ValueTask.CompletedTask; });
        context.Events.Subscribe<CustomPluginEvent>((e, _) => { typedEvents.Add(e); return ValueTask.CompletedTask; });
        context.FakeTime.Advance(TimeSpan.FromMinutes(2));

        await context.Events.PublishCustomAsync("my.event", """{"n":1}""", TestContext.Current.CancellationToken);

        var expected = new CustomPluginEvent("com.example.sender/my.event", "com.example.sender", """{"n":1}""", Now.AddMinutes(2));
        Assert.Equal([expected], topicEvents);
        Assert.Equal([expected], typedEvents);
        Assert.Empty(otherEvents);
    }

    [Fact]
    public async Task CustomEvents_WithoutCapability_Throw()
    {
        var context = new FakePluginContext("com.example.test-plugin", PluginCapability.Tick);

        var publish = await Assert.ThrowsAsync<PluginCapabilityException>(async () => await context.Events.PublishCustomAsync("e", "{}", TestContext.Current.CancellationToken));
        var subscribe = Assert.Throws<PluginCapabilityException>(() => context.Events.SubscribeCustom("t", (_, _) => ValueTask.CompletedTask));
        var typed = Assert.Throws<PluginCapabilityException>(() => context.Events.Subscribe<CustomPluginEvent>((_, _) => ValueTask.CompletedTask));

        Assert.All([publish, subscribe, typed], ex => Assert.Equal(PluginCapability.CustomEvents, ex.Capability));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("ümlaut")]
    public async Task PublishCustomAsync_InvalidName_Throws(string name)
    {
        var context = new FakePluginContext();

        await Assert.ThrowsAsync<ArgumentException>(async () => await context.Events.PublishCustomAsync(name, "{}", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishCustomAsync_NameLengthBoundary()
    {
        var context = new FakePluginContext();

        await context.Events.PublishCustomAsync(new string('a', 64), "{}", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(async () => await context.Events.PublishCustomAsync(new string('a', 65), "{}", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishCustomAsync_PayloadMustBeJsonWithinLimit()
    {
        var context = new FakePluginContext();
        string atLimit = "\"" + new string('x', BlockerSdk.MaxPayloadBytes - 2) + "\"";
        string overLimit = "\"" + new string('x', BlockerSdk.MaxPayloadBytes - 1) + "\"";
        string multibyteOver = "\"" + new string('é', BlockerSdk.MaxPayloadBytes / 2) + "\"";

        await context.Events.PublishCustomAsync("ok", atLimit, TestContext.Current.CancellationToken);
        var json = await Assert.ThrowsAsync<ArgumentException>(async () => await context.Events.PublishCustomAsync("bad", "{", TestContext.Current.CancellationToken));
        var size = await Assert.ThrowsAsync<ArgumentException>(async () => await context.Events.PublishCustomAsync("big", overLimit, TestContext.Current.CancellationToken));
        var bytes = await Assert.ThrowsAsync<ArgumentException>(async () => await context.Events.PublishCustomAsync("big", multibyteOver, TestContext.Current.CancellationToken));

        Assert.StartsWith("Payload is not valid JSON", json.Message);
        Assert.StartsWith("Payload exceeds 65536 bytes.", size.Message);
        Assert.StartsWith("Payload exceeds 65536 bytes.", bytes.Message);
    }

    [Fact]
    public async Task PublishAsync_WindowEvent_ReachesTypedSubscriber()
    {
        var context = new FakePluginContext("com.example.test-plugin", PluginCapability.WindowEvents);
        ActiveWindowChanged? received = null;
        context.Events.Subscribe<ActiveWindowChanged>((e, _) => { received = e; return ValueTask.CompletedTask; });
        var changed = new ActiveWindowChanged(new WindowDescriptor(1, "a.exe", @"C:\a.exe", null, null), Now);

        await context.PublishAsync(changed, TestContext.Current.CancellationToken);

        Assert.Same(changed, received);
    }
}
