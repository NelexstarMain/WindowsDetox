// Runs what CalendarScheduler decides. The tick callback only decides (cheap, in memory) and hands the work to a background task, because
// starting a session is a call into the service that may take seconds and a tick has a 50 ms budget. One piece of work runs at a time.

using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Notifications;
using Blocker.Sdk.Sessions;

namespace CalendarPlanner;

/// <summary>Tick listener that starts planned sessions. Requires "tick", "session.start" and "notify".</summary>
public sealed class CalendarAutoStarter : ITickListener
{
    /// <summary>Attempts per occurrence before it is recorded as failed.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Pause between two attempts.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RecentMiss = TimeSpan.FromHours(6);

    private readonly CalendarStore _store;
    private readonly ISessionLauncher _sessions;
    private readonly IPluginNotifier _notifier;
    private readonly IPluginLogger _logger;
    private readonly HashSet<string> _reminded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Attempts, DateTimeOffset RetryAt)> _attempts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private int _busy;
    private Task _work = Task.CompletedTask;

    /// <summary>Creates the starter.</summary>
    public CalendarAutoStarter(CalendarStore store, ISessionLauncher sessions, IPluginNotifier notifier, IPluginLogger logger)
    {
        _store = store;
        _sessions = sessions;
        _notifier = notifier;
        _logger = logger;
    }

    /// <inheritdoc/>
    public TimeSpan Interval => TimeSpan.FromSeconds(15);

    /// <summary>Gets the work started by the latest tick (completed when nothing runs); tests await it.</summary>
    public Task Work
    {
        get
        {
            lock (_gate)
            {
                return _work;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask OnTickAsync(TickContext context, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _busy) != 0)
        {
            return ValueTask.CompletedTask;
        }

        HashSet<string> recorded = [.. _store.History.Select(r => r.Key)];
        HashSet<string> reminded;
        lock (_gate)
        {
            reminded = [.. _reminded];
        }

        IReadOnlyList<SchedulerAction> actions = CalendarScheduler.Decide(context.Now, _store.Zone, _store.Entries, _store.Settings, context.Session.IsSessionRunning, recorded, reminded);
        actions = [.. actions.Where(a => a.Kind != SchedulerActionKind.Start || IsDueForRetry(a.Occurrence.Key, context.Now))];
        if (actions.Count == 0 || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Task work = Task.Run(() => RunGuardedAsync(actions, context.Now));
        lock (_gate)
        {
            _work = work;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Stops background work that has not started yet and waits for what runs.</summary>
    public async ValueTask StopAsync()
    {
        await _stop.CancelAsync();
        await Work;
    }

    private bool IsDueForRetry(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            return !_attempts.TryGetValue(key, out (int Attempts, DateTimeOffset RetryAt) state) || now >= state.RetryAt;
        }
    }

    private async Task RunGuardedAsync(IReadOnlyList<SchedulerAction> actions, DateTimeOffset now)
    {
        try
        {
            await RunAsync(actions, now, _stop.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn($"The calendar could not run its planned sessions: {ex.GetType().Name}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            _logger.Log(PluginLogLevel.Debug, "The calendar stopped before its work was done.");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task RunAsync(IReadOnlyList<SchedulerAction> actions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        CalendarSettings settings = _store.Settings;
        foreach (SchedulerAction action in actions)
        {
            string title = action.Occurrence.Title;
            string key = action.Occurrence.Key;
            switch (action.Kind)
            {
                case SchedulerActionKind.Remind:
                    lock (_gate)
                    {
                        _ = _reminded.Add(key);
                    }

                    int minutes = Math.Max(1, (int)Math.Ceiling((action.StartUtc - now).TotalMinutes));
                    _ = await _notifier.ShowAsync($"\"{title}\" starts in {minutes} min. It cannot be shortened once it begins.", cancellationToken);
                    break;

                case SchedulerActionKind.Miss:
                    if (await _store.RecordAsync(key, SessionOutcome.Missed, action.StartUtc.ToUnixTimeSeconds(), cancellationToken) && now - action.StartUtc < RecentMiss)
                    {
                        string at = CalTime.Clock(action.Occurrence.StartMinute, settings.Use24Hour);
                        _ = await _notifier.ShowAsync($"Missed \"{title}\" ({at}): the PC was off or another session was running.", cancellationToken);
                    }

                    break;

                case SchedulerActionKind.Start:
                    await StartAsync(action, now, cancellationToken);
                    break;
            }
        }
    }

    private async Task StartAsync(SchedulerAction action, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string key = action.Occurrence.Key;
        long planned = action.StartUtc.ToUnixTimeSeconds();

        // Written first: whatever happens next, this occurrence is never started a second time.
        if (!await _store.RecordAsync(key, SessionOutcome.Started, planned, cancellationToken))
        {
            return;
        }

        SessionStartResult result = await _sessions.StartAsync(TimeSpan.FromMinutes(action.Minutes), action.Occurrence.Title, cancellationToken);
        if (result.Started)
        {
            lock (_gate)
            {
                _ = _attempts.Remove(key);
            }

            return;
        }

        int attempts;
        lock (_gate)
        {
            attempts = (_attempts.TryGetValue(key, out (int Attempts, DateTimeOffset RetryAt) previous) ? previous.Attempts : 0) + 1;
            _attempts[key] = (attempts, now + RetryDelay);
        }

        await _store.UnrecordAsync(key, cancellationToken);
        if (attempts >= MaxAttempts)
        {
            _ = await _store.RecordAsync(key, SessionOutcome.Failed, planned, cancellationToken);
            _ = await _notifier.ShowAsync($"Could not start \"{action.Occurrence.Title}\": {result.Error ?? "the service refused."}", cancellationToken);
        }
    }
}
