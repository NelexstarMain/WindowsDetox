using System.Globalization;
using System.Runtime.Versioning;
using Blocker.Core.Ports;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// Real-time process start/stop notifications from the ETW provider <c>Microsoft-Windows-Kernel-Process</c> (keyword 0x10 = WINEVENT_KEYWORD_PROCESS).
/// Requires SYSTEM or Administrator (ETW session creation). Events are raised synchronously on the single ETW processing thread, so handlers must be cheap.
/// </summary>
/// <remarks>
/// The provider reports <c>CreateTime</c> as a FILETIME that TraceEvent converts to a local <see cref="DateTime"/>, which is ambiguous in the
/// repeated hour at the end of daylight saving time. The start-time key therefore comes from <c>GetProcessTimes</c> (see
/// <see cref="ProcessStartTimeResolver"/>); the payload is only the fallback.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class EtwProcessMonitor : IProcessMonitor
{
    /// <summary>Name of the real-time ETW session.</summary>
    public const string SessionName = "Blocker-ProcessMonitor";

    private const ulong ProcessKeyword = 0x10;
    private const int ProcessStartEventId = 1;
    private const int ProcessStopEventId = 2;
    private static readonly Guid KernelProcessProvider = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");

    private readonly NtPathConverter _paths;
    private readonly ProcessStartTimeResolver _startTimes = new(ProcessNative.TryGetStartTicks);
    private readonly Lock _gate = new();
    private TraceEventSession? _session;
    private Task _processing = Task.CompletedTask;
    private Exception? _fault;
    private Exception? _lastHandlerError;
    private bool _disposed;

    /// <summary>Creates a monitor using the real DOS device table for path conversion.</summary>
    public EtwProcessMonitor()
        : this(DosDevices.CreateConverter())
    {
    }

    internal EtwProcessMonitor(NtPathConverter paths) => _paths = paths;

    /// <inheritdoc/>
    public event EventHandler<ProcessStartedEventArgs>? ProcessStarted;

    /// <inheritdoc/>
    public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    /// <inheritdoc/>
    /// <remarks>Completes when the ETW processing loop ended (disposal, or the session was stopped externally). Check <see cref="Fault"/> afterwards.</remarks>
    public Task Completion => _processing;

    /// <inheritdoc/>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Most recent exception thrown by an event handler (handlers are isolated so one bad subscriber cannot stop monitoring).</summary>
    public Exception? LastHandlerError => Volatile.Read(ref _lastHandlerError);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                if (!_processing.IsCompleted)
                {
                    throw new InvalidOperationException("The process monitor is already started.");
                }

                // The loop ended (a fault, or someone stopped the session): replace the dead session.
                _session.Dispose();
                _session = null;
                Volatile.Write(ref _fault, null);
            }

            using (var stale = TraceEventSession.GetActiveSession(SessionName))
            {
                stale?.Stop();
            }

            var session = new TraceEventSession(SessionName);
            try
            {
                session.EnableProvider(KernelProcessProvider, TraceEventLevel.Informational, ProcessKeyword);
                var parser = new RegisteredTraceEventParser(session.Source);
                parser.All += OnEvent;
            }
            catch
            {
                session.Dispose();
                throw;
            }

            _session = session;
            _processing = Task.Factory.StartNew(() => RunLoop(session), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProcessInfo> EnumerateRunning() => ProcessNative.EnumerateRunning();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        TraceEventSession? session;
        Task processing;
        lock (_gate)
        {
            _disposed = true;
            session = _session;
            _session = null;
            processing = _processing;
        }

        if (session is null)
        {
            return;
        }

        try
        {
            session.Stop();
            await processing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
    }

    private void RunLoop(TraceEventSession session)
    {
        try
        {
            session.Source.Process();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Volatile.Write(ref _fault, ex);
        }
    }

    private void OnEvent(TraceEvent data)
    {
        if (data.ProviderGuid != KernelProcessProvider)
        {
            return;
        }

        switch ((int)data.ID)
        {
            case ProcessStartEventId:
                int started = ReadInt(data, "ProcessID");
                Raise(ProcessStarted, new ProcessStartedEventArgs(new ProcessInfo(
                    started,
                    ReadInt(data, "ParentProcessID"),
                    ReadInt(data, "SessionID"),
                    _paths.Convert(data.PayloadByName("ImageName") as string ?? string.Empty) ?? string.Empty,
                    _startTimes.ResolveStart(started, ReadFileTime(data, "CreateTime")),
                    data.ProcessID))); // the event header names the process that issued the create, which the new process cannot choose
                break;
            case ProcessStopEventId:
                int exited = ReadInt(data, "ProcessID");
                Raise(ProcessExited, new ProcessExitedEventArgs(exited, _startTimes.ResolveExit(exited, ReadFileTime(data, "CreateTime"))));
                break;
        }
    }

    private void Raise<T>(EventHandler<T>? handlers, T args)
        where T : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Volatile.Write(ref _lastHandlerError, ex);
            }
        }
    }

    private static int ReadInt(TraceEvent data, string name) =>
        data.PayloadByName(name) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    private static long ReadFileTime(TraceEvent data, string name) => data.PayloadByName(name) switch
    {
        DateTime { Kind: DateTimeKind.Utc } utc => utc.ToFileTimeUtc(),
        DateTime local => local.ToFileTime(),
        { } other => Convert.ToInt64(other, CultureInfo.InvariantCulture),
        null => 0,
    };
}
