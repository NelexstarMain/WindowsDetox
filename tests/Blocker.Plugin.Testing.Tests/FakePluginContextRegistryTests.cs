using Blocker.Sdk.Extensions;
using Blocker.Sdk.Session;

namespace Blocker.Plugin.Testing.Tests;

public sealed class FakePluginContextRegistryTests
{
    [Fact]
    public void Extensions_Add_WithCapability_RegistersAndDisposeRemoves()
    {
        var context = new FakePluginContext("com.example.test-plugin", PluginCapability.Tick);
        var ticker = new RecordingTickListener("t", TimeSpan.FromSeconds(1), []);

        IDisposable registration = context.Extensions.Add(ticker);

        Assert.Equal([ticker], context.RegisteredExtensions);
        Assert.Same(ticker, context.GetExtension<ITickListener>());
        registration.Dispose();
        Assert.Empty(context.RegisteredExtensions);
    }

    [Fact]
    public void Extensions_Add_ReportsFirstMissingCapability()
    {
        var context = new FakePluginContext("com.example.test-plugin", PluginCapability.Ipc);

        var ex = Assert.Throws<PluginCapabilityException>(() => context.Extensions.Add(new InterceptingTicker()));

        Assert.Equal(PluginCapability.ProcessInterception, ex.Capability);
        Assert.Empty(context.RegisteredExtensions);
    }

    [Fact]
    public void Extensions_Add_ReportsRemainingMissingCapability()
    {
        var context = new FakePluginContext("com.example.test-plugin", PluginCapability.ProcessInterception);

        var ex = Assert.Throws<PluginCapabilityException>(() => context.Extensions.Add(new InterceptingTicker()));

        Assert.Equal(PluginCapability.Tick, ex.Capability);
    }

    [Fact]
    public void Extensions_Add_WithoutKnownInterface_ThrowsArgumentException()
    {
        var context = new FakePluginContext();

        Assert.Throws<ArgumentException>(() => context.Extensions.Add(new UselessExtension()));
        Assert.Throws<ArgumentException>(() => context.Extensions.Add(new BareContribution()));
    }

    [Fact]
    public void GetExtension_RequiresExactlyOneMatch()
    {
        var context = new FakePluginContext();
        Assert.Equal(
            "Expected exactly one registered extension of type 'ITickListener' but found 0.",
            Assert.Throws<InvalidOperationException>(context.GetExtension<ITickListener>).Message);

        context.Extensions.Add(new RecordingTickListener("a", TimeSpan.FromSeconds(1), []));
        context.Extensions.Add(new RecordingTickListener("b", TimeSpan.FromSeconds(1), []));

        Assert.Equal(
            "Expected exactly one registered extension of type 'ITickListener' but found 2.",
            Assert.Throws<InvalidOperationException>(context.GetExtension<ITickListener>).Message);
    }

    [Fact]
    public async Task Ipc_RegisterAndInvoke_ReturnsHandlerResponse()
    {
        var context = new FakePluginContext();
        string? seen = null;
        context.Ipc.Register("getCount", (payload, _) =>
        {
            seen = payload;
            return ValueTask.FromResult("""{"count":3}""");
        });
        context.Ipc.Register("alpha", (_, _) => ValueTask.FromResult("{}"));

        string response = await context.InvokeIpcAsync("getCount", """{"id":7}""", TestContext.Current.CancellationToken);

        Assert.Equal("""{"count":3}""", response);
        Assert.Equal("""{"id":7}""", seen);
        Assert.Equal(["alpha", "getCount"], context.RegisteredIpcMethods);
    }

    [Fact]
    public void Ipc_Register_WithoutCapability_Throws()
    {
        var context = new FakePluginContext("com.example.test-plugin", PluginCapability.Tick);

        var ex = Assert.Throws<PluginCapabilityException>(() => context.Ipc.Register("x", (_, _) => ValueTask.FromResult("{}")));

        Assert.Equal(PluginCapability.Ipc, ex.Capability);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("1abc")]
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    public void Ipc_Register_InvalidName_Throws(string method)
    {
        var context = new FakePluginContext();

        Assert.Throws<ArgumentException>(() => context.Ipc.Register(method, (_, _) => ValueTask.FromResult("{}")));
    }

    [Fact]
    public void Ipc_Register_NameLengthBoundary()
    {
        var context = new FakePluginContext();

        context.Ipc.Register(new string('a', 64), (_, _) => ValueTask.FromResult("{}"));
        Assert.Throws<ArgumentException>(() => context.Ipc.Register(new string('a', 65), (_, _) => ValueTask.FromResult("{}")));
    }

    [Fact]
    public async Task Ipc_DuplicateRejected_DisposeAllowsReRegistration()
    {
        var context = new FakePluginContext();
        IDisposable first = context.Ipc.Register("ping", (_, _) => ValueTask.FromResult("1"));

        Assert.Throws<ArgumentException>(() => context.Ipc.Register("ping", (_, _) => ValueTask.FromResult("2")));
        first.Dispose();
        Assert.Empty(context.RegisteredIpcMethods);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await context.InvokeIpcAsync("ping", "{}", TestContext.Current.CancellationToken));
        context.Ipc.Register("ping", (_, _) => ValueTask.FromResult("{}"));

        Assert.Equal(["ping"], context.RegisteredIpcMethods);
    }

    [Fact]
    public async Task Ipc_Invoke_ValidatesPayloadAndResponse()
    {
        var context = new FakePluginContext();
        context.Ipc.Register("echo", (payload, _) => ValueTask.FromResult(payload));
        context.Ipc.Register("broken", (_, _) => ValueTask.FromResult("not json"));

        await Assert.ThrowsAsync<ArgumentException>(async () => await context.InvokeIpcAsync("echo", "{", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await context.InvokeIpcAsync("echo", "\"" + new string('x', BlockerSdk.MaxPayloadBytes) + "\"", TestContext.Current.CancellationToken));
        var response = await Assert.ThrowsAsync<InvalidOperationException>(async () => await context.InvokeIpcAsync("broken", "{}", TestContext.Current.CancellationToken));

        Assert.StartsWith("IPC method 'broken' returned an invalid response.", response.Message);
    }

    [Fact]
    public async Task TickAsync_FiresOncePerElapsedInterval_AndKeepsRemainder()
    {
        var context = new FakePluginContext();
        List<string> log = [];
        var listener = new RecordingTickListener("t", TimeSpan.FromSeconds(10), log);
        context.Extensions.Add(listener);
        context.FakeSession.Current = PluginTestData.ActiveSession(TimeSpan.FromMinutes(5));

        await context.TickAsync(TimeSpan.FromSeconds(35), TestContext.Current.CancellationToken);
        Assert.Equal(["t@09:00:10", "t@09:00:20", "t@09:00:30"], log);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 0, 35, TimeSpan.Zero), context.FakeTime.GetUtcNow());

        await context.TickAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(3, log.Count);
        await context.TickAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(["t@09:00:10", "t@09:00:20", "t@09:00:30", "t@09:00:40"], log);
        Assert.All(listener.Contexts, c => Assert.Equal(SessionPhase.Active, c.Session.Phase));
    }

    [Fact]
    public async Task TickAsync_RaisesSubSecondIntervalsToOneSecond()
    {
        var context = new FakePluginContext();
        List<string> log = [];
        context.Extensions.Add(new RecordingTickListener("fast", TimeSpan.FromMilliseconds(100), log));

        await context.TickAsync(TimeSpan.FromMilliseconds(3500), TestContext.Current.CancellationToken);

        Assert.Equal(["fast@09:00:01", "fast@09:00:02", "fast@09:00:03"], log);
    }

    [Fact]
    public async Task TickAsync_InterleavesListenersChronologically()
    {
        var context = new FakePluginContext();
        List<string> log = [];
        context.Extensions.Add(new RecordingTickListener("a", TimeSpan.FromSeconds(2), log));
        context.Extensions.Add(new RecordingTickListener("b", TimeSpan.FromSeconds(3), log));

        await context.TickAsync(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);

        Assert.Equal(["a@09:00:02", "b@09:00:03", "a@09:00:04", "a@09:00:06", "b@09:00:06"], log);
    }

    [Fact]
    public async Task TickAsync_SkipsRemovedListenersAndRejectsNegativeAdvance()
    {
        var context = new FakePluginContext();
        List<string> log = [];
        IDisposable registration = context.Extensions.Add(new RecordingTickListener("gone", TimeSpan.FromSeconds(1), log));
        registration.Dispose();

        await context.TickAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(log);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await context.TickAsync(TimeSpan.FromSeconds(-1), TestContext.Current.CancellationToken));
    }
}
