using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Blocker.Core.Events;
using Blocker.Core.PluginRuntime;
using Blocker.Core.Tests.Plugins;
using Blocker.Plugin.Templates;
using Blocker.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.PluginRuntime;

/// <summary>Plugin sources and ZIPs used by the runtime tests. Every plugin compiles under the real API policy.</summary>
internal static class RuntimePlugins
{
    public const string StarterId = "com.example.focus-counter";

    private const string Usings = "using Blocker.Sdk;\nusing Blocker.Sdk.Extensions;\nusing Blocker.Sdk.Model;\nusing Blocker.UI.Sdk;\n";

    /// <summary>A dashboard widget whose render always throws.</summary>
    public const string ThrowingWidget = Usings + """
        namespace T;
        public sealed class P : IBlockerPlugin
        {
            public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
            {
                context.Extensions.Add(new W());
                return ValueTask.CompletedTask;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }

        public sealed class W : IDashboardWidgetPlugin
        {
            public UiWidgetSize Size => UiWidgetSize.Medium;

            public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("boom");
        }
        """;

    /// <summary>Blocks every process named chrome.exe.</summary>
    public const string BlockingInterceptor = Usings + """
        namespace T;
        public sealed class P : IBlockerPlugin, IProcessInterceptor
        {
            public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
            {
                context.Extensions.Add(this);
                return ValueTask.CompletedTask;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public ValueTask<InterceptDecision> OnProcessStartingAsync(ProcessDescriptor process, CancellationToken cancellationToken) =>
                ValueTask.FromResult(process.ImageName == "chrome.exe" ? InterceptDecision.Block("No browsers now.") : InterceptDecision.Abstain);
        }
        """;

    /// <summary>An interceptor that never answers.</summary>
    public const string HangingInterceptor = Usings + """
        namespace T;
        public sealed class P : IBlockerPlugin, IProcessInterceptor
        {
            public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
            {
                context.Extensions.Add(this);
                return ValueTask.CompletedTask;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public async ValueTask<InterceptDecision> OnProcessStartingAsync(ProcessDescriptor process, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return InterceptDecision.Abstain;
            }
        }
        """;

    /// <summary>Vetoes uninstalling; a second contributor throws.</summary>
    public const string LockContributors = Usings + """
        namespace T;
        public sealed class P : IBlockerPlugin
        {
            public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
            {
                context.Extensions.Add(new Veto());
                context.Extensions.Add(new Broken());
                return ValueTask.CompletedTask;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }

        public sealed class Veto : ILockPolicyContributor
        {
            public ValueTask<LockVote> EvaluateAsync(LockAction action, CancellationToken cancellationToken) =>
                ValueTask.FromResult(action == LockAction.UninstallApplication ? LockVote.Veto("Stay strong.") : LockVote.Allow);
        }

        public sealed class Broken : ILockPolicyContributor
        {
            public ValueTask<LockVote> EvaluateAsync(LockAction action, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("boom");
        }
        """;

    /// <summary>Counts ticks and answers IPC calls <c>ticks</c> and <c>echo</c>.</summary>
    public const string TickAndIpc = Usings + """
        namespace T;
        public sealed class P : IBlockerPlugin, ITickListener
        {
            private int _ticks;

            public TimeSpan Interval => TimeSpan.FromSeconds(1);

            public ValueTask OnTickAsync(TickContext context, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _ticks);
                return ValueTask.CompletedTask;
            }

            public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
            {
                context.Extensions.Add(this);
                context.Ipc.Register("ticks", (json, ct) => ValueTask.FromResult("{\"ticks\":" + Volatile.Read(ref _ticks) + "}"));
                context.Ipc.Register("echo", (json, ct) => ValueTask.FromResult(json));
                return ValueTask.CompletedTask;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }
        """;

    /// <summary>Subscribes to a session event through a generic helper without declaring the capability, so only the runtime check can catch it.</summary>
    public const string UndeclaredSubscription = Usings + """
        using Blocker.Sdk.Events;
        namespace T;
        public sealed class P : IBlockerPlugin
        {
            public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
            {
                Subscribe<SessionStarted>(context.Events);
                return ValueTask.CompletedTask;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            private static void Subscribe<TEvent>(IEventBus bus)
                where TEvent : class, IBlockerEvent => bus.Subscribe<TEvent>((e, ct) => ValueTask.CompletedTask);
        }
        """;

    /// <summary>Builds a ZIP with a manifest and one source file.</summary>
    public static byte[] Zip(string id, string[] capabilities, string source)
    {
        string manifest = $$"""
            {
              "id": "{{id}}",
              "name": "Test plugin",
              "version": "1.0.0",
              "author": "Tester",
              "sdkVersion": "1.0",
              "entryType": "T.P",
              "capabilities": [{{string.Join(", ", capabilities.Select(c => $"\"{c}\""))}}]
            }
            """;
        using MemoryStream zip = PluginTestSupport.Zip(("plugin.json", manifest), ("Plugin.cs", source));
        return zip.ToArray();
    }

    /// <summary>The embedded starter template as a ZIP.</summary>
    public static async Task<byte[]> StarterZipAsync()
    {
        using var stream = new MemoryStream();
        await PluginStarterTemplate.WriteZipAsync(stream, CancellationToken.None);
        return stream.ToArray();
    }

    /// <summary>The starter template with one assertion changed so that its own test fails.</summary>
    public static async Task<byte[]> FailingStarterZipAsync()
    {
        Dictionary<string, string> files = await PluginTestSupport.StarterFilesAsync();
        files["PluginTests.cs"] = files["PluginTests.cs"].Replace(
            "Assert.Equal(\"3\", await CounterValueAsync(ctx));", "Assert.Equal(\"4\", await CounterValueAsync(ctx));", StringComparison.Ordinal);
        using MemoryStream zip = PluginTestSupport.ZipOf(files);
        return zip.ToArray();
    }

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>An in-memory store, a fake clock, a host bus and a runtime wired together.</summary>
internal sealed class RuntimeHarness : IAsyncDisposable
{
    public RuntimeHarness(PluginRuntimeOptions? options = null)
    {
        Fs = new MockFileSystem();
        Store = new PluginStore(Fs, "/plugins", "/data");
        Time = new FakeTimeProvider();
        Clock = new VerifiedTimeProvider(Time);
        Bus = new HostEventBus(Clock, NullLogger<HostEventBus>.Instance);
        Runtime = new PluginHostRuntime(Store, Bus, Clock, NullLogger<PluginHostRuntime>.Instance, options);
    }

    public MockFileSystem Fs { get; }

    public PluginStore Store { get; }

    public FakeTimeProvider Time { get; }

    public VerifiedTimeProvider Clock { get; }

    public HostEventBus Bus { get; }

    public PluginHostRuntime Runtime { get; }

    public async Task InstallAsync(string id, byte[] zip, bool enabled = true) =>
        await Store.InstallAsync(new PluginId(id), zip, enabled, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        await Bus.DisposeAsync();
    }
}

/// <summary>Cooperative waiting without sleeping.</summary>
internal static class Spin
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static async Task UntilAsync(Func<bool> condition)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(Patience);
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(Patience);
        while (!await condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    /// <summary>Advances the fake clock in small steps until <paramref name="task"/> completes.</summary>
    public static async Task AdvanceUntilAsync(FakeTimeProvider time, Task task, TimeSpan step)
    {
        await UntilAsync(() =>
        {
            if (!task.IsCompleted)
            {
                time.Advance(step);
            }

            return task.IsCompleted;
        });
        await task;
    }

    /// <summary>Advances the fake clock in small steps until <paramref name="task"/> completes, then returns its result.</summary>
    public static async Task<T> AdvanceUntilAsync<T>(FakeTimeProvider time, Task<T> task, TimeSpan step)
    {
        await UntilAsync(() =>
        {
            if (!task.IsCompleted)
            {
                time.Advance(step);
            }

            return task.IsCompleted;
        });
        return await task;
    }
}
