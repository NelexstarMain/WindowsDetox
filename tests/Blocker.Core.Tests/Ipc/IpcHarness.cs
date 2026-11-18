using System.IO.Pipes;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Tests.Ipc;

/// <summary>Identity resolver whose answer tests can change per connection.</summary>
internal sealed class FakeIdentityResolver(IpcCallerRole roles) : ICallerIdentityResolver
{
    public IpcCallerRole Roles { get; set; } = roles;

    public bool Refuse { get; set; }

    public CallerIdentity? Resolve(NamedPipeServerStream pipe) =>
        Refuse ? null : new CallerIdentity(Roles, ProcessId: 4242, ImagePath: "fake.exe");
}

/// <summary>Logger that remembers messages and exceptions.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}

/// <summary>A server on a unique pipe name plus factory methods for clients.</summary>
internal sealed class IpcHarness : IAsyncDisposable
{
    private readonly List<IpcClient> _clients = [];

    public IpcHarness(
        IpcCallerRole roles,
        IpcServerOptions? options = null,
        TimeProvider? serverTime = null,
        string? pipeName = null)
    {
        PipeName = pipeName ?? $"blk-{Guid.NewGuid():N}";
        Identity = new FakeIdentityResolver(roles);
        Logger = new CapturingLogger<IpcServer>();
        Server = new IpcServer(PipeName, new DefaultPipeListenerFactory(), Identity, () => SessionActive, Logger, options, serverTime);
    }

    public string PipeName { get; }

    public FakeIdentityResolver Identity { get; }

    public CapturingLogger<IpcServer> Logger { get; }

    public IpcServer Server { get; }

    public bool SessionActive { get; set; }

    public IpcHarness Started()
    {
        Server.Start();
        return this;
    }

    public IpcClient NewClient(IpcClientOptions? options = null, TimeProvider? time = null)
    {
        var client = new IpcClient(PipeName, new CapturingLogger<IpcClient>(), options, time);
        _clients.Add(client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        await Server.DisposeAsync();
    }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static SessionStatusDto Status() => SessionStatusDto.From(Blocker.Sdk.Session.SessionSnapshot.Idle);
}
