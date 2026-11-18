using System.Collections.Concurrent;
using System.Text.Json;
using Blocker.Contracts;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Ipc;

/// <summary>
/// Handles one inbound request or notification. Return the response for a request and <see langword="null"/> for a notification.
/// Implementations must not throw; the connection answers unexpected exceptions with a generic <see cref="IpcErrorCode.Internal"/> error.
/// </summary>
/// <param name="message">The request or notification.</param>
/// <param name="frameBytes">Size of the received frame in bytes.</param>
/// <param name="cancellationToken">Cancelled when the connection closes.</param>
/// <returns>The response, or <see langword="null"/>.</returns>
public delegate ValueTask<IpcEnvelope?> IpcInboundHandler(IpcEnvelope message, int frameBytes, CancellationToken cancellationToken);

/// <summary>
/// Bidirectional JSON message connection over any stream: correlates responses to requests by id (with timeouts), delivers
/// inbound requests and notifications to <see cref="Inbound"/> concurrently, and serializes concurrent writes. Owns the stream.
/// </summary>
public sealed partial class IpcConnection : IAsyncDisposable
{
    /// <summary>Requests of one peer handled concurrently; further ones are answered with <see cref="IpcErrorCode.RateLimited"/>.</summary>
    public const int MaxInFlightRequests = 16;

    private readonly Stream _stream;
    private readonly int _maxInboundFrameBytes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private readonly ConcurrentDictionary<Task, byte> _dispatches = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private int _inFlight;
    private int _started;
    private int _disposed;
    private IpcError? _peerError;

    /// <summary>Creates a connection. Call <see cref="Start"/> after setting <see cref="Inbound"/>.</summary>
    /// <param name="stream">Connected duplex stream; owned by the connection.</param>
    /// <param name="maxInboundFrameBytes">Largest accepted inbound frame.</param>
    /// <param name="time">Time source for request timeouts.</param>
    /// <param name="logger">Logger.</param>
    public IpcConnection(Stream stream, int maxInboundFrameBytes, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInboundFrameBytes);
        _stream = stream;
        _maxInboundFrameBytes = maxInboundFrameBytes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets or sets the handler for inbound requests and notifications; set before <see cref="Start"/>.</summary>
    public IpcInboundHandler? Inbound { get; set; }

    /// <summary>Gets a task that completes (never faults) when the connection has ended.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Gets whether the read loop is running.</summary>
    public bool IsConnected => _started == 1 && !_completion.Task.IsCompleted;

    /// <summary>Starts reading. Calling it more than once has no effect.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(RunAsync);
        }
    }

    /// <summary>Sends a request and waits for its response.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Parameters, or <see langword="null"/>.</param>
    /// <param name="timeout">How long to wait for the response.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result element.</returns>
    /// <exception cref="IpcException">The peer answered with an error.</exception>
    /// <exception cref="TimeoutException">No response arrived in time.</exception>
    /// <exception cref="IOException">The connection closed.</exception>
    public async ValueTask<JsonElement> InvokeAsync(string method, JsonElement? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        if (_completion.Task.IsCompleted)
        {
            _pending.TryRemove(id, out _);
            throw ClosedException();
        }

        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, _stop.Token);
        try
        {
            await SendAsync(IpcEnvelope.Request(id, method, parameters), linked.Token).ConfigureAwait(false);
            var response = await pending.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            if (response.Error is { } error)
            {
                throw new IpcException(error.Code, error.Message);
            }

            return response.Result ?? throw new IpcException(IpcErrorCode.InvalidRequest, "Response without result.");
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response to '{method}' within {timeout.TotalSeconds:0.#} s.");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw ClosedException();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification (no response).</summary>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Parameters, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the message is written.</returns>
    public ValueTask NotifyAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        return SendAsync(IpcEnvelope.Notification(method, parameters), cancellationToken);
    }

    /// <summary>Closes the connection, fails pending calls and waits for running handlers.</summary>
    /// <returns>A task that completes when everything has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        if (_started == 1)
        {
            await _completion.Task.ConfigureAwait(false);
        }
        else
        {
            _completion.TrySetResult();
        }

        await Task.WhenAll(_dispatches.Keys).ConfigureAwait(false);
        _writeLock.Dispose();
        _stop.Dispose();
    }

    private static IOException ClosedException() => new("The IPC connection is closed.");

    private async ValueTask SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJsonContext.Default.IpcEnvelope);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(_stream, bytes, IpcLimits.MaxMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadFrameAsync(_stream, _maxInboundFrameBytes, token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (!await HandleFrameAsync(frame, token).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (FrameTooLargeException ex)
        {
            LogFrameRejected(_logger, ex.Length, ex.MaxFrameBytes);
            await TrySendFatalAsync(IpcErrorCode.PayloadTooLarge, "Message too large.").ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            LogProtocolViolation(_logger, ex);
            await TrySendFatalAsync(IpcErrorCode.InvalidRequest, "Invalid request.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            LogConnectionEnded(_logger, ex.GetType().Name);
        }
        finally
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            FailPending();
            _completion.TrySetResult();
        }
    }

    /// <summary>Processes one frame; returns false when the connection must close.</summary>
    private async ValueTask<bool> HandleFrameAsync(byte[] frame, CancellationToken token)
    {
        IpcEnvelope? message;
        try
        {
            message = JsonSerializer.Deserialize(frame, IpcJsonContext.Default.IpcEnvelope);
        }
        catch (JsonException ex)
        {
            LogProtocolViolation(_logger, ex);
            await TrySendFatalAsync(IpcErrorCode.InvalidRequest, "Invalid request.").ConfigureAwait(false);
            return false;
        }

        switch (message?.Kind)
        {
            case IpcMessageKind.Response:
                if (message.Id is null)
                {
                    _peerError = message.Error;
                    FailPending();
                }
                else if (_pending.TryRemove(message.Id.Value, out var waiter))
                {
                    waiter.TrySetResult(message);
                }
                else
                {
                    LogUnmatchedResponse(_logger, message.Id.Value);
                }

                return true;

            case IpcMessageKind.Request or IpcMessageKind.Notification:
                Dispatch(message, frame.Length, token);
                return true;

            default:
                LogProtocolViolation(_logger, null);
                await TrySendFatalAsync(IpcErrorCode.InvalidRequest, "Invalid request.").ConfigureAwait(false);
                return false;
        }
    }

    private void Dispatch(IpcEnvelope message, int frameBytes, CancellationToken token)
    {
        var overloaded = Interlocked.Increment(ref _inFlight) > MaxInFlightRequests;
        var task = Task.Run(() => DispatchAsync(message, frameBytes, overloaded, token), CancellationToken.None);
        _dispatches[task] = 0;
        _ = task.ContinueWith(
            t => _dispatches.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DispatchAsync(IpcEnvelope message, int frameBytes, bool overloaded, CancellationToken token)
    {
        try
        {
            IpcEnvelope? response;
            if (overloaded)
            {
                response = message.Id is { } overloadedId ? IpcEnvelope.Failure(overloadedId, IpcErrorCode.RateLimited, "Too many concurrent requests.") : null;
            }
            else if (Inbound is not { } inbound)
            {
                response = message.Id is { } unknownId ? IpcEnvelope.Failure(unknownId, IpcErrorCode.NotFound, "Unknown method.") : null;
            }
            else
            {
                response = await inbound(message, frameBytes, token).ConfigureAwait(false);
            }

            if (response is not null && message.Id is not null)
            {
                await SendResponseAsync(response, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            LogDispatchAborted(_logger, ex.GetType().Name);
        }
        catch (Exception ex)
        {
            LogHandlerFault(_logger, ex);
            await TrySendInternalErrorAsync(message.Id, token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async ValueTask SendResponseAsync(IpcEnvelope response, CancellationToken token)
    {
        try
        {
            await SendAsync(response, token).ConfigureAwait(false);
        }
        catch (FrameTooLargeException ex)
        {
            LogResponseTooLarge(_logger, ex.Length);
            await SendAsync(IpcEnvelope.Failure(response.Id, IpcErrorCode.Internal, "Internal error."), token).ConfigureAwait(false);
        }
    }

    private async ValueTask TrySendInternalErrorAsync(long? id, CancellationToken token)
    {
        if (id is null)
        {
            return;
        }

        try
        {
            await SendAsync(IpcEnvelope.Failure(id, IpcErrorCode.Internal, "Internal error."), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            LogDispatchAborted(_logger, ex.GetType().Name);
        }
    }

    private async ValueTask TrySendFatalAsync(IpcErrorCode code, string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), _time);
            await SendAsync(IpcEnvelope.Failure(null, code, message), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            LogDispatchAborted(_logger, ex.GetType().Name);
        }
    }

    private void FailPending()
    {
        foreach (var (id, waiter) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                waiter.TrySetException(_peerError is { } error ? new IpcException(error.Code, error.Message) : ClosedException());
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "IPC frame of {Length} bytes rejected (limit {Limit}).")]
    private static partial void LogFrameRejected(ILogger logger, long length, int limit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IPC protocol violation; closing the connection.")]
    private static partial void LogProtocolViolation(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "IPC connection ended ({Reason}).")]
    private static partial void LogConnectionEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "IPC response for unknown request id {Id} ignored.")]
    private static partial void LogUnmatchedResponse(ILogger logger, long id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "IPC dispatch aborted ({Reason}).")]
    private static partial void LogDispatchAborted(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "IPC inbound handler failed.")]
    private static partial void LogHandlerFault(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "IPC response of {Length} bytes is too large; sent an internal error instead.")]
    private static partial void LogResponseTooLarge(ILogger logger, long length);
}
