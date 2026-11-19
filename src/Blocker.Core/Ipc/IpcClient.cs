using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Blocker.Contracts;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Ipc;

/// <summary>Timeouts and back-off of an <see cref="IpcClient"/>.</summary>
public sealed record IpcClientOptions
{
    /// <summary>Gets how long a connect attempt waits for the server.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how long a request waits for its response.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the first delay of <see cref="IpcClient.RunAsync"/> between connection attempts.</summary>
    public TimeSpan ReconnectDelayMin { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the largest delay between connection attempts (the delay doubles up to this value).</summary>
    public TimeSpan ReconnectDelayMax { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A notification pushed by the server.</summary>
/// <param name="method">Notification name.</param>
/// <param name="parameters">Parameters.</param>
public sealed class IpcNotificationEventArgs(string method, JsonElement parameters) : EventArgs
{
    /// <summary>Gets the notification name (see <see cref="IpcMethods"/>).</summary>
    public string Method { get; } = method;

    /// <summary>Gets the parameters.</summary>
    public JsonElement Parameters { get; } = parameters;
}

/// <summary>
/// Client of the Blocker pipe. Connects lazily, offers typed calls, raises server notifications, answers server-to-client requests
/// registered with <see cref="Register"/>, and can keep itself connected with <see cref="RunAsync"/>. Thread-safe.
/// </summary>
public sealed partial class IpcClient : IAsyncDisposable
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly string _pipeName;
    private readonly ILogger<IpcClient> _logger;
    private readonly IpcClientOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>> _handlers = new(StringComparer.Ordinal);
    private IpcConnection? _connection;
    private int _disposed;

    /// <summary>Creates a client; nothing connects until <see cref="ConnectAsync"/>, a call or <see cref="RunAsync"/>.</summary>
    /// <param name="pipeName">Pipe name without prefix.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Timeouts; defaults when <see langword="null"/>.</param>
    /// <param name="time">Time source; the system clock when <see langword="null"/>.</param>
    public IpcClient(string pipeName, ILogger<IpcClient> logger, IpcClientOptions? options = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(logger);
        _pipeName = pipeName;
        _logger = logger;
        _options = options ?? new IpcClientOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised for every notification pushed by the server, on a thread-pool thread.</summary>
    public event EventHandler<IpcNotificationEventArgs>? NotificationReceived;

    /// <summary>Gets whether a connection is established.</summary>
    public bool IsConnected => _connection is { IsConnected: true };

    /// <summary>Registers a handler for a server-to-client request such as <see cref="IpcMethods.AgentCloseProcess"/>.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="handler">Receives the parameters and returns a JSON object.</param>
    /// <returns>A registration; dispose to remove the handler.</returns>
    /// <exception cref="ArgumentException">The method is already registered.</exception>
    public IDisposable Register(string method, Func<JsonElement, CancellationToken, ValueTask<JsonElement>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(method, handler))
        {
            throw new ArgumentException($"'{method}' is already registered.", nameof(method));
        }

        return new Registration(() => _handlers.TryRemove(new KeyValuePair<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>>(method, handler)));
    }

    /// <summary>Connects (no-op when already connected).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when connected.</returns>
    /// <exception cref="TimeoutException">The server did not accept within <see cref="IpcClientOptions.ConnectTimeout"/>.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _ = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until the current connection ends; completes immediately when not connected. Does not reconnect.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the connection is gone.</returns>
    public Task WaitForDisconnectAsync(CancellationToken cancellationToken) =>
        _connection is { } connection ? connection.Completion.WaitAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>Sends a request, connecting first when necessary.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Parameters, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result element.</returns>
    /// <exception cref="IpcException">The server answered with an error.</exception>
    public async ValueTask<JsonElement> InvokeAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Scanning installed apps walks the disk and can take well over the usual budget on a real machine.
        var timeout = method == Blocker.Contracts.IpcMethods.AllowlistScanApps ? TimeSpan.FromMinutes(2) : _options.RequestTimeout;
        return await connection.InvokeAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Typed request; both types must be registered in <see cref="IpcJsonContext"/>.</summary>
    /// <typeparam name="TRequest">Request DTO.</typeparam>
    /// <typeparam name="TResponse">Response DTO.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    /// <exception cref="IpcException">The server answered with an error or an unparsable result.</exception>
    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(method, IpcJson.ToElement(request), cancellationToken).ConfigureAwait(false);
        try
        {
            return IpcJson.FromElement<TResponse>(result);
        }
        catch (JsonException)
        {
            throw new IpcException(IpcErrorCode.InvalidRequest, "Unexpected response.");
        }
    }

    /// <summary>Typed request without parameters.</summary>
    /// <typeparam name="TResponse">Response DTO.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    public ValueTask<TResponse> InvokeAsync<TResponse>(string method, CancellationToken cancellationToken) =>
        InvokeAsync<EmptyDto, TResponse>(method, new EmptyDto(), cancellationToken);

    /// <summary>Sends a notification, connecting first when necessary.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Parameters, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the message is written.</returns>
    public async ValueTask NotifyAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.NotifyAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the client connected until cancelled: connects, runs <paramref name="onConnected"/> (for example to register an agent
    /// or re-subscribe), waits for the connection to end, then reconnects with exponential back-off.
    /// </summary>
    /// <param name="onConnected">Called after every successful connect; a failure drops the connection and retries.</param>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when cancelled.</returns>
    public async Task RunAsync(Func<CancellationToken, Task> onConnected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onConnected);
        var delay = _options.ReconnectDelayMin;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
                delay = _options.ReconnectDelayMin;
                await onConnected(cancellationToken).ConfigureAwait(false);
                await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                LogDisconnected(_logger);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or IpcException or UnauthorizedAccessException)
            {
                LogConnectFailed(_logger, ex.GetType().Name, delay);
            }

            await DropConnectionAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectDelayMax.Ticks));
        }
    }

    /// <summary>Disconnects and releases the client.</summary>
    /// <returns>A task that completes when the connection has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await DropConnectionAsync().ConfigureAwait(false);
        _connectGate.Dispose();
    }

    private static NamedPipeClientStream CreateStream(string pipeName)
    {
        // Identification level: the server may inspect the caller's token but can never act as the caller.
        if (OperatingSystem.IsWindows())
        {
            // Explicit rights without FILE_CREATE_PIPE_INSTANCE (which GENERIC_WRITE would imply), matching the server ACL.
            return new NamedPipeClientStream(
                ".",
                pipeName,
                PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.Synchronize,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification,
                HandleInheritability.None);
        }

        return new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    }

    private async ValueTask<IpcConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (_connection is { IsConnected: true } existing)
        {
            return existing;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed == 1, this);
            if (_connection is { IsConnected: true } current)
            {
                return current;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            var stream = CreateStream(_pipeName);
            try
            {
                await stream.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var connection = new IpcConnection(stream, IpcLimits.MaxMessageBytes, _time, _logger)
            {
                Inbound = HandleInboundAsync,
            };
            connection.Start();
            _connection = connection;
            return connection;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async ValueTask DropConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<IpcEnvelope?> HandleInboundAsync(IpcEnvelope message, int frameBytes, CancellationToken cancellationToken)
    {
        var id = message.Id;
        var method = message.Method ?? string.Empty;
        var parameters = message.Params ?? EmptyObject;
        if (id is null)
        {
            RaiseNotification(method, parameters);
            return null;
        }

        if (!_handlers.TryGetValue(method, out var handler))
        {
            return IpcEnvelope.Failure(id, IpcErrorCode.NotFound, "Unknown method.");
        }

        try
        {
            var result = await handler(parameters, cancellationToken).ConfigureAwait(false);
            return IpcEnvelope.Success(id.Value, result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? EmptyObject : result);
        }
        catch (IpcException ex)
        {
            return IpcEnvelope.Failure(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, ex, method);
            return IpcEnvelope.Failure(id, IpcErrorCode.Internal, "Internal error.");
        }
    }

    private void RaiseNotification(string method, JsonElement parameters)
    {
        try
        {
            NotificationReceived?.Invoke(this, new IpcNotificationEventArgs(method, parameters));
        }
        catch (Exception ex)
        {
            LogSubscriberFailed(_logger, ex, method);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC connection lost; reconnecting.")]
    private static partial void LogDisconnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "IPC connect failed ({Reason}); retrying in {Delay}.")]
    private static partial void LogConnectFailed(ILogger logger, string reason, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Client handler of {Method} failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string method);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification subscriber for {Method} failed.")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception, string method);

    private sealed class Registration(Func<bool> remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
