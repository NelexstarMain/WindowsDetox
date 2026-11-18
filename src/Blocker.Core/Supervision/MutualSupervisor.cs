using Microsoft.Extensions.Logging;

namespace Blocker.Core.Supervision;

/// <summary>Run state of a Windows service as far as the supervisor cares.</summary>
public enum ServiceRunState
{
    /// <summary>The service is not installed.</summary>
    NotInstalled,

    /// <summary>The service is stopped.</summary>
    Stopped,

    /// <summary>The service is starting.</summary>
    StartPending,

    /// <summary>The service is running.</summary>
    Running,

    /// <summary>The service is stopping, paused or in another transitional state.</summary>
    Other,
}

/// <summary>Service control port (the SCM on Windows).</summary>
public interface IServiceControl
{
    /// <summary>Returns the run state of <paramref name="serviceName"/>.</summary>
    /// <param name="serviceName">Service name.</param>
    /// <returns>The state.</returns>
    ServiceRunState QueryStatus(string serviceName);

    /// <summary>Asks the SCM to start the service (returns without waiting for it to be running).</summary>
    /// <param name="serviceName">Service name.</param>
    void Start(string serviceName);

    /// <summary>Returns the process id of the running service, or <see langword="null"/> when it has none.</summary>
    /// <param name="serviceName">Service name.</param>
    /// <returns>The process id.</returns>
    int? GetProcessId(string serviceName);
}

/// <summary>Waits for a process to end without polling (a process handle wait on Windows).</summary>
public interface IProcessWaiter
{
    /// <summary>Completes when the process exited; completes immediately when it does not exist (anymore).</summary>
    /// <param name="processId">Process id.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>A task that completes when the process is gone.</returns>
    Task WaitForExitAsync(int processId, CancellationToken cancellationToken);
}

/// <summary>Timing and back-off of a <see cref="MutualSupervisor"/>.</summary>
public sealed record MutualSupervisorOptions
{
    /// <summary>Gets how often the supervisor re-checks while it has nothing to do (not supervising, or the partner is starting).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the number of failed starts within <see cref="FailureWindow"/> after which restarts back off.</summary>
    public int FailedStartsBeforeBackoff { get; init; } = 10;

    /// <summary>Gets the window of <see cref="FailedStartsBeforeBackoff"/>.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the first back-off delay; it doubles with every further failure.</summary>
    public TimeSpan BackoffMin { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the largest back-off delay.</summary>
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long a started partner must run before its exit stops counting as a failed start (crash loop detection).</summary>
    public TimeSpan MinStableRuntime { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Keeps the partner service (the service supervises the watchdog and the watchdog supervises the service) alive while
/// <c>shouldSupervise</c> says so: starts it at once when it is not running, otherwise waits on its process handle and restarts it
/// the moment it exits (well under a second). Restarts are immediate; only after
/// <see cref="MutualSupervisorOptions.FailedStartsBeforeBackoff"/> failed starts within a minute does the delay grow exponentially.
/// A start counts as failed when it throws or the partner dies before <see cref="MutualSupervisorOptions.MinStableRuntime"/>.
/// </summary>
public sealed partial class MutualSupervisor
{
    private const int MaxImmediateRechecks = 3;

    private readonly string _partner;
    private readonly IServiceControl _control;
    private readonly IProcessWaiter _waiter;
    private readonly Func<CancellationToken, ValueTask<bool>> _shouldSupervise;
    private readonly TimeProvider _time;
    private readonly ILogger<MutualSupervisor> _logger;
    private readonly MutualSupervisorOptions _options;
    private readonly Queue<long> _failedStarts = new();

    /// <summary>Creates the supervisor; call <see cref="RunAsync"/> to run it.</summary>
    /// <param name="partnerServiceName">Service to keep alive.</param>
    /// <param name="control">Service control port.</param>
    /// <param name="waiter">Process wait port.</param>
    /// <param name="shouldSupervise">Whether the partner must be kept alive right now (a session is active); evaluated before every action.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Timing; defaults when <see langword="null"/>.</param>
    public MutualSupervisor(
        string partnerServiceName,
        IServiceControl control,
        IProcessWaiter waiter,
        Func<CancellationToken, ValueTask<bool>> shouldSupervise,
        TimeProvider time,
        ILogger<MutualSupervisor> logger,
        MutualSupervisorOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partnerServiceName);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(waiter);
        ArgumentNullException.ThrowIfNull(shouldSupervise);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _partner = partnerServiceName;
        _control = control;
        _waiter = waiter;
        _shouldSupervise = shouldSupervise;
        _time = time;
        _logger = logger;
        _options = options ?? new MutualSupervisorOptions();
    }

    /// <summary>Supervises until cancelled.</summary>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when cancelled.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        long? startedAt = null;
        int withoutProcess = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await _shouldSupervise(cancellationToken).ConfigureAwait(false))
                {
                    startedAt = null;
                    await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                switch (_control.QueryStatus(_partner))
                {
                    case ServiceRunState.Running when _control.GetProcessId(_partner) is { } pid:
                        withoutProcess = 0;
                        await _waiter.WaitForExitAsync(pid, cancellationToken).ConfigureAwait(false);
                        if (startedAt is { } at && _time.GetElapsedTime(at) < _options.MinStableRuntime)
                        {
                            RecordFailedStart();
                        }

                        startedAt = null;
                        LogPartnerExited(_logger, _partner);
                        break;
                    case ServiceRunState.Stopped or ServiceRunState.NotInstalled:
                        await StartPartnerAsync(cancellationToken).ConfigureAwait(false);
                        startedAt = _time.GetTimestamp();
                        break;
                    case ServiceRunState.Running when withoutProcess++ < MaxImmediateRechecks:
                        // The partner stopped between the two queries; look again at once instead of sleeping.
                        break;
                    default:
                        await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogStopped(_logger);
        }
    }

    private async Task StartPartnerAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = BackoffDelay();
        if (delay > TimeSpan.Zero)
        {
            LogBackoff(_logger, _partner, delay);
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _control.Start(_partner);
            LogStarted(_logger, _partner);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or TimeoutException)
        {
            RecordFailedStart();
            LogStartFailed(_logger, ex, _partner);
        }
    }

    private void RecordFailedStart()
    {
        long now = _time.GetTimestamp();
        _failedStarts.Enqueue(now);
        Prune(now);
    }

    private void Prune(long now)
    {
        while (_failedStarts.Count > 0 && _time.GetElapsedTime(_failedStarts.Peek(), now) >= _options.FailureWindow)
        {
            _failedStarts.Dequeue();
        }
    }

    private TimeSpan BackoffDelay()
    {
        Prune(_time.GetTimestamp());
        int excess = _failedStarts.Count - _options.FailedStartsBeforeBackoff;
        return excess < 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Math.Min(_options.BackoffMin.Ticks << Math.Min(excess, 20), _options.BackoffMax.Ticks));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Partner service {Service} exited; restarting.")]
    private static partial void LogPartnerExited(ILogger logger, string service);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started partner service {Service}.")]
    private static partial void LogStarted(ILogger logger, string service);

    [LoggerMessage(Level = LogLevel.Error, Message = "Starting partner service {Service} failed.")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string service);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Too many failed starts of {Service}; backing off {Delay}.")]
    private static partial void LogBackoff(ILogger logger, string service, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Supervision stopped.")]
    private static partial void LogStopped(ILogger logger);
}
