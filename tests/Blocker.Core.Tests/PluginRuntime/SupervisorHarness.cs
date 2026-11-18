using System.Collections.Concurrent;
using System.IO.Abstractions;
using Blocker.Contracts;
using Blocker.Core.Events;
using Blocker.Core.Ipc;
using Blocker.Core.PluginRuntime;
using Blocker.Core.Ports;
using Blocker.Core.Tests.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocker.Core.Tests.PluginRuntime;

/// <summary>What a fake host process knows about its launch.</summary>
internal sealed record HostLaunch(string Pipe, string Token, CancellationToken Stop);

/// <summary>A restricted process whose "executable" is a delegate; killing it cancels the delegate.</summary>
internal sealed class FakeHostProcess(int id) : IRestrictedProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();

    public int ProcessId { get; } = id;

    public bool Killed { get; private set; }

    public CancellationToken Lifetime => _lifetime.Token;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        Killed = true;
        _lifetime.Cancel();
    }

    public void Exit() => _exit.TrySetResult();

    public void Dispose()
    {
        // Nothing to release: the fake owns no OS handle, and its token source is cancelled by Kill.
        Disposed = true;
    }

    public bool Disposed { get; private set; }
}

/// <summary>Runs the given host body in-process for every launch, with the token from the command line.</summary>
internal sealed class FakeHostLauncher(Func<HostLaunch, Task> body) : IRestrictedProcessLauncher
{
    private readonly ConcurrentQueue<FakeHostProcess> _processes = new();
    private readonly ConcurrentQueue<Exception> _errors = new();

    public IReadOnlyList<FakeHostProcess> Processes => [.. _processes];

    public IReadOnlyList<Exception> Errors => [.. _errors];

    public IRestrictedProcess Launch(string executablePath, string arguments)
    {
        string[] args = arguments.Split(' ');
        Assert.True(PluginHostArguments.TryParse(args, out PluginHostArguments? parsed, out string? error), error);
        var process = new FakeHostProcess(1000 + _processes.Count);
        _processes.Enqueue(process);
        var launch = new HostLaunch(parsed.PipeName, parsed.Token!, process.Lifetime);
        _ = Task.Run(async () =>
        {
            try
            {
                await body(launch);
            }
            catch (OperationCanceledException) when (process.Lifetime.IsCancellationRequested)
            {
                process.Exit();
            }
            catch (Exception ex)
            {
                _errors.Enqueue(ex);
            }
            finally
            {
                process.Exit();
            }
        });
        return process;
    }
}

/// <summary>Host bodies for supervisor tests.</summary>
internal static class HostBodies
{
    /// <summary>The real host logic (runtime plus endpoint) over the given in-memory store.</summary>
    public static Func<HostLaunch, Task> Real(IFileSystem fileSystem) => async launch =>
    {
        var clock = new VerifiedTimeProvider(TimeProvider.System);
        await using var bus = new HostEventBus(clock, NullLogger<HostEventBus>.Instance);
        var store = new PluginStore(fileSystem, "/plugins", "/data");
        await using var runtime = new PluginHostRuntime(store, bus, clock, NullLogger<PluginHostRuntime>.Instance);
        await using var client = new IpcClient(launch.Pipe, NullLogger<IpcClient>.Instance);
        await new PluginHostEndpoint(client, runtime, NullLogger<PluginHostEndpoint>.Instance, launch.Token).RunAsync(launch.Stop);
    };

    /// <summary>Connects, registers, optionally installs handlers, then runs <paramref name="serve"/> until the process is killed.</summary>
    public static Func<HostLaunch, Task> Scripted(Action<IpcClient>? handlers = null, Func<IpcClient, CancellationToken, Task>? serve = null) => async launch =>
    {
        await using var client = new IpcClient(launch.Pipe, NullLogger<IpcClient>.Instance);
        handlers?.Invoke(client);
        await client.ConnectAsync(launch.Stop);
        PluginHostRegisterResponse reply = await client.InvokeAsync<PluginHostRegisterRequest, PluginHostRegisterResponse>(
            IpcMethods.PluginHostRegister, new PluginHostRegisterRequest(PluginHostProtocol.Version, launch.Token), launch.Stop);
        if (reply.Accepted && serve is not null)
        {
            await serve(client, launch.Stop);
        }
    };

    public static void Answer<TRequest, TResponse>(IpcClient client, string method, Func<TRequest, CancellationToken, Task<TResponse>> handler) =>
        client.Register(method, async (parameters, ct) => IpcJson.ToElement(await handler(IpcJson.FromElement<TRequest>(parameters), ct)));
}

/// <summary>A plugin-host pipe server, a fake launcher and a supervisor wired together.</summary>
internal sealed class SupervisorHarness : IAsyncDisposable
{
    public SupervisorHarness(Func<HostLaunch, Task> body, TimeProvider time, Func<PluginHostSupervisorOptions, PluginHostSupervisorOptions>? configure = null)
    {
        Ipc = new IpcHarness(IpcCallerRole.PluginHost, serverTime: time).Started();
        Launcher = new FakeHostLauncher(body);
        var options = new PluginHostSupervisorOptions
        {
            HostExecutablePath = "host.exe",
            PipeName = Ipc.PipeName,
            PluginsDirectory = "/plugins",
            DataDirectory = "/data",
        };
        Supervisor = new PluginHostSupervisor(Ipc.Server, Launcher, time, NullLogger<PluginHostSupervisor>.Instance, configure?.Invoke(options) ?? options);
    }

    public IpcHarness Ipc { get; }

    public FakeHostLauncher Launcher { get; }

    public PluginHostSupervisor Supervisor { get; }

    public async ValueTask DisposeAsync()
    {
        await Supervisor.DisposeAsync();
        await Ipc.DisposeAsync();
        Assert.Empty(Launcher.Errors);
    }
}
