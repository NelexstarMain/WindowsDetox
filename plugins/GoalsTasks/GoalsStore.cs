// Storage side of Goals & Tasks: a small, bounded, persisted task list per local day.
//
// Only the last 7 days are kept (today included), at most 50 tasks per day. Every change is written through immediately
// (changes are rare and tiny); rendering reads the in-memory copy and never touches storage.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Storage;

namespace GoalsTasks;

/// <summary>Limits that keep the stored data small.</summary>
public static class GoalsLimits
{
    /// <summary>Days of history that are kept (today included).</summary>
    public const int RetentionDays = 7;

    /// <summary>Most tasks one day can hold.</summary>
    public const int MaxTasksPerDay = 50;

    /// <summary>Longest task text.</summary>
    public const int MaxTaskLength = 120;

    /// <summary>Smallest daily goal.</summary>
    public const int MinGoal = 1;

    /// <summary>Largest daily goal.</summary>
    public const int MaxGoal = 20;

    /// <summary>Daily goal until the user changes it.</summary>
    public const int InitialGoal = 3;
}

/// <summary>One task. Short JSON names keep the stored data compact.</summary>
public sealed class TaskItem
{
    /// <summary>Unique id (never reused).</summary>
    [JsonPropertyName("i")]
    public int Id { get; set; }

    /// <summary>Task text.</summary>
    [JsonPropertyName("x")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Whether the task is done.</summary>
    [JsonPropertyName("k")]
    public bool Done { get; set; }
}

/// <summary>The tasks and goal of one local calendar day.</summary>
public sealed class DayRecord
{
    /// <summary>Local day number (<see cref="DateOnly.DayNumber"/>).</summary>
    [JsonPropertyName("d")]
    public int Day { get; set; }

    /// <summary>Goal chosen for this day, or 0 for the default goal.</summary>
    [JsonPropertyName("g")]
    public int Goal { get; set; }

    /// <summary>Tasks in the order they were added.</summary>
    [JsonPropertyName("t")]
    public List<TaskItem> Tasks { get; set; } = [];
}

/// <summary>The persisted document.</summary>
public sealed class GoalsData
{
    /// <summary>Schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    /// <summary>Id of the next task.</summary>
    [JsonPropertyName("n")]
    public int NextId { get; set; } = 1;

    /// <summary>Day records, oldest first.</summary>
    [JsonPropertyName("d")]
    public List<DayRecord> Days { get; set; } = [];
}

/// <summary>Thread-safe, write-through cache of the task data.</summary>
public sealed class GoalsStore
{
    /// <summary>Storage key of the data document.</summary>
    public const string DataKey = "data";

    /// <summary>Storage key of the default daily goal.</summary>
    public const string DefaultGoalKey = "default-goal";

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private GoalsData _data = new();
    private int _defaultGoal = GoalsLimits.InitialGoal;
    private bool _dirty;
    private long _version;
    private long _written;

    /// <summary>Creates the store on top of the plugin storage.</summary>
    public GoalsStore(IPluginStorage storage, TimeProvider time, IPluginLogger logger)
    {
        _storage = storage;
        Time = time;
        _logger = logger;
    }

    /// <summary>Raised after every change so contributions can ask for a new render.</summary>
    public event Action? Changed;

    /// <summary>The plugin clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>The current local day number.</summary>
    public int CurrentDay => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Time.GetUtcNow(), Time.LocalTimeZone).DateTime).DayNumber;

    /// <summary>The goal used for days without their own goal.</summary>
    public int DefaultGoal
    {
        get
        {
            lock (_gate)
            {
                return _defaultGoal;
            }
        }
    }

    /// <summary>Loads persisted data. Missing or corrupt data starts empty.</summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        GoalsData? loaded = null;
        string? json = await _storage.ReadAsync(DataKey, cancellationToken);
        if (json is not null)
        {
            try
            {
                loaded = JsonSerializer.Deserialize<GoalsData>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.Warn($"Stored tasks are unreadable and were ignored: {ex.Message}");
            }
        }

        string? goal = await _storage.ReadAsync(DefaultGoalKey, cancellationToken);
        lock (_gate)
        {
            _data = Sanitize(loaded, CurrentDay);
            if (int.TryParse(goal, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                _defaultGoal = Math.Clamp(value, GoalsLimits.MinGoal, GoalsLimits.MaxGoal);
            }
        }
    }

    /// <summary>Adds a task for today. Returns <see langword="false"/> when the text is empty or today is full.</summary>
    public ValueTask<bool> AddTaskAsync(string? text, CancellationToken cancellationToken)
    {
        string? clean = CleanText(text);
        return ChangeAsync(
            (data, today) =>
            {
                DayRecord day = GetDay(data, today);
                if (clean is null || day.Tasks.Count >= GoalsLimits.MaxTasksPerDay)
                {
                    return false;
                }

                day.Tasks.Add(new TaskItem { Id = data.NextId++, Text = clean });
                return true;
            },
            cancellationToken);
    }

    /// <summary>Marks a task of today done or open. Returns <see langword="false"/> when nothing changed.</summary>
    public ValueTask<bool> SetDoneAsync(int id, bool done, CancellationToken cancellationToken) =>
        ChangeAsync(
            (data, today) =>
            {
                if (FindToday(data, today, id) is not { } task || task.Done == done)
                {
                    return false;
                }

                task.Done = done;
                return true;
            },
            cancellationToken);

    /// <summary>Deletes a task of today.</summary>
    public ValueTask<bool> DeleteAsync(int id, CancellationToken cancellationToken) =>
        ChangeAsync((data, today) => GetDay(data, today).Tasks.RemoveAll(t => t.Id == id) > 0, cancellationToken);

    /// <summary>Deletes the done tasks of today.</summary>
    public ValueTask<bool> ClearCompletedAsync(CancellationToken cancellationToken) =>
        ChangeAsync((data, today) => GetDay(data, today).Tasks.RemoveAll(t => t.Done) > 0, cancellationToken);

    /// <summary>Sets today's goal (limited to the allowed range).</summary>
    public ValueTask<bool> SetTodayGoalAsync(int goal, CancellationToken cancellationToken)
    {
        int clamped = Math.Clamp(goal, GoalsLimits.MinGoal, GoalsLimits.MaxGoal);
        return ChangeAsync(
            (data, today) =>
            {
                DayRecord day = GetDay(data, today);
                if ((day.Goal > 0 ? day.Goal : _defaultGoal) == clamped)
                {
                    return false;
                }

                day.Goal = clamped;
                return true;
            },
            cancellationToken);
    }

    /// <summary>Removes all tasks of today and its own goal.</summary>
    public ValueTask<bool> ResetTodayAsync(CancellationToken cancellationToken) =>
        ChangeAsync((data, today) => data.Days.RemoveAll(d => d.Day == today) > 0, cancellationToken);

    /// <summary>Changes and persists the default daily goal (limited to the allowed range).</summary>
    public async ValueTask SetDefaultGoalAsync(int goal, CancellationToken cancellationToken)
    {
        int clamped = Math.Clamp(goal, GoalsLimits.MinGoal, GoalsLimits.MaxGoal);
        lock (_gate)
        {
            _defaultGoal = clamped;
        }

        await _storage.WriteAsync(DefaultGoalKey, clamped.ToString(CultureInfo.InvariantCulture), cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>Writes pending changes that could not be saved earlier.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        bool dirty;
        lock (_gate)
        {
            dirty = _dirty;
        }

        if (dirty)
        {
            await PersistAsync(cancellationToken);
        }
    }

    /// <summary>Snapshot of today and the last seven days.</summary>
    public GoalsView GetView()
    {
        int today = CurrentDay;
        lock (_gate)
        {
            return GoalsAggregator.Build(_data.Days, today, _defaultGoal);
        }
    }

    /// <summary>Returns the task text without control characters, trimmed and limited in length, or <see langword="null"/> when nothing is left.</summary>
    public static string? CleanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length > GoalsLimits.MaxTaskLength)
        {
            int length = char.IsHighSurrogate(cleaned[GoalsLimits.MaxTaskLength - 1]) ? GoalsLimits.MaxTaskLength - 1 : GoalsLimits.MaxTaskLength;
            cleaned = cleaned[..length].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    // Finds or creates the record of a day, keeping the list ordered by day.
    private static DayRecord GetDay(GoalsData data, int dayNumber)
    {
        if (data.Days.Find(d => d.Day == dayNumber) is { } existing)
        {
            return existing;
        }

        var created = new DayRecord { Day = dayNumber };
        data.Days.Add(created);
        data.Days.Sort((a, b) => a.Day.CompareTo(b.Day));
        return created;
    }

    private static TaskItem? FindToday(GoalsData data, int today, int id) =>
        data.Days.Find(d => d.Day == today)?.Tasks.Find(t => t.Id == id);

    // Runs a mutation under the lock; when it reports a change the data is pruned, saved and observers are notified.
    private async ValueTask<bool> ChangeAsync(Func<GoalsData, int, bool> change, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            int today = CurrentDay;
            if (!change(_data, today))
            {
                // A new day may have been created just to look; keep the list free of empty records.
                _data.Days.RemoveAll(d => d.Day == today && d.Goal == 0 && d.Tasks.Count == 0);
                return false;
            }

            _data.Days.RemoveAll(d => d.Day < today - GoalsLimits.RetentionDays + 1);
        }

        await PersistAsync(cancellationToken);
        Changed?.Invoke();
        return true;
    }

    private async ValueTask PersistAsync(CancellationToken cancellationToken)
    {
        string json;
        long version;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_data, JsonOptions);
            version = ++_version;
            _dirty = false;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (version > _written)
            {
                await _storage.WriteAsync(DataKey, json, cancellationToken);
                _written = version;
            }
        }
        catch (PluginStorageException ex)
        {
            _logger.Warn($"Tasks could not be saved: {ex.Message}");
            lock (_gate)
            {
                _dirty = true;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // Loaded data is untrusted: drop or repair anything that violates the invariants the rest of the code relies on.
    private static GoalsData Sanitize(GoalsData? loaded, int today)
    {
        if (loaded is null)
        {
            return new GoalsData();
        }

        int oldest = today - GoalsLimits.RetentionDays + 1;
        HashSet<int> ids = [];
        List<DayRecord> days = [];
        foreach (DayRecord day in (loaded.Days ?? []).Where(d => d is not null && d.Day >= oldest && d.Day <= DateOnly.MaxValue.DayNumber).OrderBy(d => d.Day))
        {
            if (days.Count > 0 && days[^1].Day == day.Day)
            {
                continue;
            }

            List<TaskItem> tasks = [];
            foreach (TaskItem task in day.Tasks ?? [])
            {
                if (task is not null && task.Id > 0 && CleanText(task.Text) is { } text && tasks.Count < GoalsLimits.MaxTasksPerDay && ids.Add(task.Id))
                {
                    tasks.Add(new TaskItem { Id = task.Id, Text = text, Done = task.Done });
                }
            }

            days.Add(new DayRecord { Day = day.Day, Goal = day.Goal < GoalsLimits.MinGoal ? 0 : Math.Min(day.Goal, GoalsLimits.MaxGoal), Tasks = tasks });
        }

        return new GoalsData { Days = days, NextId = Math.Max(Math.Max(loaded.NextId, 1), ids.DefaultIfEmpty(0).Max() + 1) };
    }
}
