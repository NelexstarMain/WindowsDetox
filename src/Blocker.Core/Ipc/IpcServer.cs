using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Blocker.Contracts;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Ipc;

/// <summary>Limits of an <see cref="IpcServer"/>.</summary>
public sealed record IpcServerOptions
{
    /// <summary>Gets the maximum number of simultaneous connections; further ones are dropped.</summary>
    public int MaxConnections { get; init; } = 32;

    /// <summary>Gets the sustained request rate allowed per connection (requests per second).</summary>
    public double RequestsPerSecond { get; init; } = 50;

    /// <summary>Gets the burst size of the per-connection token bucket.</summary>
    public int RateBurst { get; init; } = 100;
}

/// <summary>A connected client as seen by the server.</summary>
public sealed class IpcPeer
{
    private readonly IpcConnection _connection;
    private volatile bool _subscribed;

    internal IpcPeer(Guid id, CallerIdentity identity, IpcConnection connection, TokenBucket limiter)
    {
        Id = id;
        Identity = identity;
        _connection = connection;
        Limiter = limiter;
    }

    /// <summary>Gets the connection id (server-local).</summary>
    public Guid Id { get; }

    /// <summary>Gets the identity established by the server.</summary>
    public CallerIdentity Identity { get; }

    /// <summary>Gets whether the peer called <see cref="IpcMethods.EventsSubscribe"/>.</summary>
    public bool IsSubscribed => _subscribed;

    /// <summary>Gets a task that completes when the peer disconnected.</summary>
    public Task Disconnected => _connection.Completion;

    internal TokenBucket Limiter { get; }

    /// <summary>Sends a server-to-client request (for example <see cref="IpcMethods.AgentCloseProcess"/>) and waits for the answer.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Parameters, or <see langword="null"/>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result element.</returns>
    public ValueTask<JsonElement> InvokeAsync(string method, JsonElement? parameters, TimeSpan timeout, CancellationToken cancellationToken) =>
        _connection.InvokeAsync(method, parameters, timeout, cancellationToken);

    /// <summary>Sends a notification to the peer.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Parameters, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the message is written.</returns>
    public ValueTask NotifyAsync(string method, JsonElement? parameters, CancellationToken cancellationToken) =>
        _connection.NotifyAsync(method, parameters, cancellationToken);

    internal void Subscribe() => _subscribed = true;
}

/// <summary>What a handler learns about the call.</summary>
/// <param name="Peer">The calling peer.</param>
/// <param name="Method">Wire method name.</param>
/// <param name="RequestId">Request id; <see langword="null"/> for notifications.</param>
public sealed record IpcRequestContext(IpcPeer Peer, string Method, long? RequestId)
{
    /// <summary>Gets the caller identity.</summary>
    public CallerIdentity Identity => Peer.Identity;
}

/// <summary>
/// Named-pipe JSON server. Accepts connections through an <see cref="IPipeListenerFactory"/>, establishes the caller's role with an
/// <see cref="ICallerIdentityResolver"/>, and dispatches to registered handlers after enforcing <see cref="IpcMethodPolicy"/>
/// (role, session gate, payload size) and a per-connection rate limit. Clients only ever receive generic error messages;
/// details are logged.
/// </summary>
public sealed partial class IpcServer : IAsyncDisposable
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly string _pipeName;
    private readonly IPipeListenerFactory _listeners;
    private readonly ICallerIdentityResolver _identities;
    private readonly Func<bool> _isSessionActive;
    private readonly ILogger<IpcServer> _logger;
    private readonly IpcServerOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Func<IpcRequestContext, JsonElement, CancellationToken, ValueTask<JsonElement>>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, IpcPeer> _peers = new();
    private readonly ConcurrentDictionary<Task, byte> _connectionTasks = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _acceptLoop = Task.CompletedTask;
    private int _connectionCount;
    private int _started;
    private int _disposed;

    /// <summary>Creates a server; call <see cref="Start"/> to listen.</summary>
    /// <param name="pipeName">Pipe name without prefix.</param>
    /// <param name="listeners">Creates listening instances.</param>
    /// <param name="identities">Establishes caller roles.</param>
    /// <param name="isSessionActive">Returns whether a session runs (the "not allowed during session" gate).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Limits; defaults when <see langword="null"/>.</param>
    /// <param name="time">Time source; the system clock when <see langword="null"/>.</param>
    public IpcServer(
        string pipeName,
        IPipeListenerFactory listeners,
        ICallerIdentityResolver identities,
        Func<bool> isSessionActive,
        ILogger<IpcServer> logger,
        IpcServerOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(isSessionActive);
        ArgumentNullException.ThrowIfNull(logger);
        _pipeName = pipeName;
        _listeners = listeners;
        _identities = identities;
        _isSessionActive = isSessionActive;
        _logger = logger;
        _options = options ?? new IpcServerOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets a task that completes when the accept loop has ended (on stop, or after a fatal listener failure).</summary>
    public Task Completion => _acceptLoop;

    /// <summary>Gets the currently connected peers.</summary>
    public IReadOnlyCollection<IpcPeer> Peers => _peers.Values.ToArray();

    /// <summary>Registers a raw handler. The method must exist in <see cref="IpcMethodPolicy"/> (fixed methods or <c>plugin.{id}.{method}</c>).</summary>
    /// <param name="method">Wire method name.</param>
    /// <param name="handler">Receives the parameters (an empty object when absent) and returns a JSON object.</param>
    /// <returns>A registration; dispose to remove the handler.</returns>
    /// <exception cref="ArgumentException">The method is unknown or already registered.</exception>
    public IDisposable Register(string method, Func<IpcRequestContext, JsonElement, CancellationToken, ValueTask<JsonElement>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IpcMethodPolicy.TryGet(method, out _))
        {
            throw new ArgumentException($"'{method}' is not part of the IPC contract.", nameof(method));
        }

        if (!_handlers.TryAdd(method, handler))
        {
            throw new ArgumentException($"'{method}' is already registered.", nameof(method));
        }

        return new Registration(() => _handlers.TryRemove(new KeyValuePair<string, Func<IpcRequestContext, JsonElement, CancellationToken, ValueTask<JsonElement>>>(method, handler)));
    }

    /// <summary>Registers a typed handler; parameters are parsed strictly and a malformed request becomes <see cref="IpcErrorCode.InvalidRequest"/>.</summary>
    /// <typeparam name="TRequest">Request DTO registered in <see cref="IpcJsonContext"/>.</typeparam>
    /// <typeparam name="TResponse">Response DTO registered in <see cref="IpcJsonContext"/>.</typeparam>
    /// <param name="method">Wire method name.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>A registration; dispose to remove the handler.</returns>
    public IDisposable Register<TRequest, TResponse>(string method, Func<IpcRequestContext, TRequest, CancellationToken, ValueTask<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(method, async (context, parameters, cancellationToken) =>
        {
            TRequest request;
            try
            {
                request = IpcJson.FromElement<TRequest>(parameters);
            }
            catch (JsonException)
            {
                throw new IpcException(IpcErrorCode.InvalidRequest, "Invalid parameters.");
            }

            var response = await handler(context, request, cancellationToken).ConfigureAwait(false);
            return IpcJson.ToElement(response);
        });
    }

    /// <summary>Creates the first pipe instance (throws when it cannot be created exclusively) and starts accepting connections.</summary>
    /// <exception cref="IOException">The pipe name is already in use.</exception>
    /// <exception cref="UnauthorizedAccessException">The pipe could not be created with the required security.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        var first = _listeners.Create(_pipeName, isFirstInstance: true);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(first, _stop.Token));
    }

    /// <summary>Sends a notification to every peer that called <see cref="IpcMethods.EventsSubscribe"/>.</summary>
    /// <param name="method">Notification name, for example <see cref="IpcMethods.EventSessionChanged"/>.</param>
    /// <param name="parameters">Parameters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when all peers were served; failures of single peers are logged.</returns>
    public async Task PublishAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var tasks = _peers.Values.Where(p => p.IsSubscribed).Select(p => NotifyPeerAsync(p, method, parameters, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Typed overload of <see cref="PublishAsync(string, JsonElement, CancellationToken)"/>.</summary>
    /// <typeparam name="T">DTO registered in <see cref="IpcJsonContext"/>.</typeparam>
    /// <param name="method">Notification name.</param>
    /// <param name="payload">Payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when all peers were served.</returns>
    public Task PublishAsync<T>(string method, T payload, CancellationToken cancellationToken) =>
        PublishAsync(method, IpcJson.ToElement(payload), cancellationToken);

    /// <summary>Stops listening, disconnects all peers and waits for running handlers.</summary>
    /// <returns>A task that completes when the server has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _acceptLoop.ConfigureAwait(false);
        await Task.WhenAll(_connectionTasks.Keys).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task NotifyPeerAsync(IpcPeer peer, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        try
        {
            await peer.NotifyAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LogPublishFailed(_logger, peer.Id, ex.GetType().Name);
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream first, CancellationToken token)
    {
        var listener = first;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await listener.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // The client vanished between connect and accept; nothing to serve, listen again.
                    LogAcceptFailed(_logger, ex);
                    await listener.DisposeAsync().ConfigureAwait(false);
                    listener = _listeners.Create(_pipeName, isFirstInstance: false);
                    continue;
                }

                var connected = listener;
                try
                {
                    listener = _listeners.Create(_pipeName, isFirstInstance: false);
                }
                catch (Exception)
                {
                    await connected.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                Track(HandleConnectionAsync(connected, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogListenerStopped(_logger);
        }
        catch (Exception ex)
        {
            LogListenerFailed(_logger, ex);
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Track(Task task)
    {
        _connectionTasks[task] = 0;
        _ = task.ContinueWith(
            t => _connectionTasks.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        IpcConnection? connection = null;
        IpcPeer? peer = null;
        try
        {
            if (Interlocked.Increment(ref _connectionCount) > _options.MaxConnections)
            {
                LogTooManyConnections(_logger, _options.MaxConnections);
                return;
            }

            var identity = _identities.Resolve(pipe);
            if (identity is null || identity.Roles == IpcCallerRole.None)
            {
                LogConnectionRefused(_logger, identity?.ProcessId);
                return;
            }

            connection = new IpcConnection(pipe, IpcMethodPolicy.MaxFrameBytesFor(identity.Roles), _time, _logger);
            peer = new IpcPeer(Guid.NewGuid(), identity, connection, new TokenBucket(_options.RequestsPerSecond, _options.RateBurst, _time));
            var current = peer;
            connection.Inbound = (message, frameBytes, ct) => HandleInboundAsync(current, message, frameBytes, ct);
            _peers[peer.Id] = peer;
            connection.Start();
            await connection.Completion.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogListenerStopped(_logger);
        }
        catch (Exception ex)
        {
            LogConnectionFailed(_logger, ex);
        }
        finally
        {
            Interlocked.Decrement(ref _connectionCount);
            if (peer is not null)
            {
                _peers.TryRemove(peer.Id, out _);
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<IpcEnvelope?> HandleInboundAsync(IpcPeer peer, IpcEnvelope message, int frameBytes, CancellationToken token)
    {
        var id = message.Id;
        IpcEnvelope? Fail(IpcErrorCode code, string text) => id is null ? null : IpcEnvelope.Failure(id, code, text);

        if (message.Method is not { Length: > 0 and <= IpcMethods.MaxMethodLength } method)
        {
            return Fail(IpcErrorCode.InvalidRequest, "Invalid request.");
        }

        if (!peer.Limiter.TryTake())
        {
            LogRateLimited(_logger, peer.Id, method);
            return Fail(IpcErrorCode.RateLimited, "Too many requests.");
        }

        if (!IpcMethodPolicy.TryGet(method, out var rule))
        {
            return Fail(IpcErrorCode.NotFound, "Unknown method.");
        }

        if (!rule.Allows(peer.Identity.Roles))
        {
            LogUnauthorized(_logger, method, peer.Identity.Roles, peer.Identity.ProcessId);
            return Fail(IpcErrorCode.Unauthorized, "Not authorized.");
        }

        try
        {
            if (!rule.AllowedDuringSession && _isSessionActive())
            {
                return Fail(IpcErrorCode.NotAllowedDuringSession, "Not allowed while a session is running.");
            }

            if (frameBytes > rule.MaxPayloadBytes)
            {
                return Fail(IpcErrorCode.PayloadTooLarge, "Message too large.");
            }

            _handlers.TryGetValue(method, out var handler);
            if (handler is null && method != IpcMethods.EventsSubscribe)
            {
                return Fail(IpcErrorCode.NotFound, "Unknown method.");
            }

            var result = EmptyObject;
            if (handler is not null)
            {
                var context = new IpcRequestContext(peer, method, id);
                var returned = await handler(context, message.Params ?? EmptyObject, token).ConfigureAwait(false);
                if (returned.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    result = returned;
                }
            }

            if (method == IpcMethods.EventsSubscribe)
            {
                peer.Subscribe();
            }

            return id is { } requestId ? IpcEnvelope.Success(requestId, result) : null;
        }
        catch (IpcException ex)
        {
            return Fail(ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, ex, method);
            return Fail(IpcErrorCode.Internal, "Internal error.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipe accept failed; listening again.")]
    private static partial void LogAcceptFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC listener stopped.")]
    private static partial void LogListenerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "IPC listener failed and is no longer accepting connections.")]
    private static partial void LogListenerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection dropped: more than {Max} simultaneous connections.")]
    private static partial void LogTooManyConnections(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection refused: caller identity not accepted (pid {ProcessId}).")]
    private static partial void LogConnectionRefused(ILogger logger, int? processId);

    [LoggerMessage(Level = LogLevel.Error, Message = "IPC connection failed.")]
    private static partial void LogConnectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Peer {PeerId} exceeded its rate limit calling {Method}.")]
    private static partial void LogRateLimited(ILogger logger, Guid peerId, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unauthorized call to {Method} by roles {Roles} (pid {ProcessId}).")]
    private static partial void LogUnauthorized(ILogger logger, string method, IpcCallerRole roles, int? processId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handler of {Method} failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string method);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Publishing to peer {PeerId} failed ({Reason}).")]
    private static partial void LogPublishFailed(ILogger logger, Guid peerId, string reason);

    private sealed class Registration(Func<bool> remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
