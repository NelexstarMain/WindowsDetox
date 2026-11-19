// Storage side of the Calendar plugin: bounded, sanitized, write-through documents ("data", "history", "settings") with an in-memory
// copy that rendering reads. Entries are copy-on-write: a published entry object is never modified, so a render in progress cannot
// see a half-applied edit. Mutations run one at a time.

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Storage;

namespace CalendarPlanner;

/// <summary>Thread-safe, write-through cache of the calendar.</summary>
public sealed class CalendarStore
{
    /// <summary>Storage key of the entries document.</summary>
    public const string DataKey = "data";

    /// <summary>Storage key of the history document.</summary>
    public const string HistoryKey = "history";

    /// <summary>Storage key of the settings document.</summary>
    public const string SettingsKey = "settings";

    /// <summary>Suffix of the key under which an unreadable document is kept.</summary>
    public const string CorruptSuffix = "-unreadable";

    private const int MaxKeptCorruptChars = 200_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private IReadOnlyList<CalendarEntry> _entries = [];
    private IReadOnlyList<HistoryRecord> _history = [];
    private CalendarSettings _settings = new();
    private int _nextId = 1;
    private bool _dirtyData;
    private bool _dirtyHistory;
    private bool _dirtySettings;

    /// <summary>Creates the store on top of the plugin storage.</summary>
    public CalendarStore(IPluginStorage storage, TimeProvider time, IPluginLogger logger)
    {
        _storage = storage;
        Time = time;
        _logger = logger;
    }

    /// <summary>Raised after every change so contributions can ask for a new render.</summary>
    public event Action? Changed;

    /// <summary>The plugin clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>The time zone entries are interpreted in.</summary>
    public TimeZoneInfo Zone => Time.LocalTimeZone;

    /// <summary>Whether a stored document could not be read at start-up and was replaced by an empty one (the unreadable text is kept in storage).</summary>
    public bool RecoveredFromCorruption { get; private set; }

    /// <summary>All entries. The objects must not be modified.</summary>
    public IReadOnlyList<CalendarEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries;
            }
        }
    }

    /// <summary>The settings (a private copy).</summary>
    public CalendarSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings.Sanitized();
            }
        }
    }

    /// <summary>The history records, oldest first.</summary>
    public IReadOnlyList<HistoryRecord> History
    {
        get
        {
            lock (_gate)
            {
                return _history;
            }
        }
    }

    /// <summary>The current local day number.</summary>
    public int Today => CalTime.DayOf(Time.GetUtcNow(), Zone);

    /// <summary>Finds an entry.</summary>
    public CalendarEntry? Find(int id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>Loads persisted data. Missing data starts empty; unreadable data starts empty too and is kept under a separate key.</summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        CalendarData? data = await ReadAsync<CalendarData>(DataKey, cancellationToken);
        CalendarHistory? history = await ReadAsync<CalendarHistory>(HistoryKey, cancellationToken);
        CalendarSettings? settings = await ReadAsync<CalendarSettings>(SettingsKey, cancellationToken);
        lock (_gate)
        {
            if (data is not null)
            {
                (_entries, _nextId) = Sanitize(data);
            }

            if (history is not null)
            {
                _history = SanitizeHistory(history.Records, Time.GetUtcNow().ToUnixTimeSeconds());
            }

            if (settings is not null)
            {
                _settings = settings.Sanitized();
            }
        }
    }

    /// <summary>Adds an entry. The id and creation time are assigned here.</summary>
    /// <returns>The stored entry, or <see langword="null"/> when the calendar is full.</returns>
    public async ValueTask<CalendarEntry?> AddAsync(CalendarEntry draft, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            CalendarEntry? added = null;
            lock (_gate)
            {
                if (_entries.Count < CalendarLimits.MaxEntries)
                {
                    added = draft.Clone();
                    added.Id = _nextId++;
                    added.Created = Time.GetUtcNow().ToUnixTimeSeconds();
                    added.Title = CleanTitle(added.Title);
                    _entries = [.. _entries, added];
                    _dirtyData = true;
                }
            }

            if (added is not null)
            {
                await PersistAsync(cancellationToken);
            }

            return added;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>Replaces a whole series with <paramref name="updated"/> (same id). Edits of single occurrences are dropped; skips and ticks that are no longer series days are dropped.</summary>
    public ValueTask<bool> ReplaceAsync(CalendarEntry updated, CancellationToken cancellationToken) =>
        MutateAsync(
            updated.Id,
            _ =>
            {
                CalendarEntry copy = updated.Clone();
                copy.Title = CleanTitle(copy.Title);
                copy.Overrides = [];
                copy.Skipped = [.. copy.Skipped.Where(d => CalendarRecurrence.IsSeriesDay(copy, d)).Distinct().Take(CalendarLimits.MaxSkips)];
                copy.DoneDays = [.. copy.DoneDays.Where(d => CalendarRecurrence.IsSeriesDay(copy, d)).Distinct().Take(CalendarLimits.MaxDone)];
                return copy;
            },
            cancellationToken);

    /// <summary>Edits one occurrence of a series.</summary>
    public ValueTask<bool> OverrideAsync(int id, OccurrenceOverride change, CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            entry =>
            {
                if (!CalendarRecurrence.IsSeriesDay(entry, change.OriginalDay) || entry.Skipped.Contains(change.OriginalDay)
                    || (entry.Overrides.Count >= CalendarLimits.MaxOverrides && !entry.Overrides.Any(o => o.OriginalDay == change.OriginalDay)))
                {
                    return null;
                }

                CalendarEntry copy = entry.Clone();
                _ = copy.Overrides.RemoveAll(o => o.OriginalDay == change.OriginalDay);
                copy.Overrides.Add(new OccurrenceOverride
                {
                    OriginalDay = change.OriginalDay,
                    Day = Math.Clamp(change.Day, MinDay, MaxDay),
                    Minute = Math.Clamp(change.Minute, -1, 1439),
                    Duration = Math.Clamp(change.Duration, 0, CalendarLimits.MaxTaskMinutes),
                    Title = CleanTitle(change.Title),
                });
                return copy;
            },
            cancellationToken);

    /// <summary>Skips one occurrence of a series; a one-time entry is deleted instead.</summary>
    public async ValueTask<bool> SkipAsync(int id, int day, CancellationToken cancellationToken)
    {
        CalendarEntry? entry = Find(id);
        if (entry is null)
        {
            return false;
        }

        if (entry.Repeat == RepeatKind.Once)
        {
            return await DeleteAsync(id, cancellationToken);
        }

        return await MutateAsync(
            id,
            current =>
            {
                if (!CalendarRecurrence.IsSeriesDay(current, day) || current.Skipped.Contains(day) || current.Skipped.Count >= CalendarLimits.MaxSkips)
                {
                    return null;
                }

                CalendarEntry copy = current.Clone();
                copy.Skipped.Add(day);
                _ = copy.Overrides.RemoveAll(o => o.OriginalDay == day);
                return copy;
            },
            cancellationToken);
    }

    /// <summary>Ticks or unticks one occurrence.</summary>
    public ValueTask<bool> SetDoneAsync(int id, int originalDay, bool done, CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            entry =>
            {
                if (entry.Kind != EntryKind.Task || !CalendarRecurrence.IsSeriesDay(entry, originalDay) || entry.DoneDays.Contains(originalDay) == done)
                {
                    return null;
                }

                CalendarEntry copy = entry.Clone();
                if (done)
                {
                    copy.DoneDays.Add(originalDay);
                    if (copy.DoneDays.Count > CalendarLimits.MaxDone)
                    {
                        copy.DoneDays.RemoveAt(0);
                    }
                }
                else
                {
                    _ = copy.DoneDays.Remove(originalDay);
                }

                return copy;
            },
            cancellationToken);

    /// <summary>Deletes an entry (the whole series).</summary>
    public async ValueTask<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            bool removed;
            lock (_gate)
            {
                int before = _entries.Count;
                _entries = [.. _entries.Where(e => e.Id != id)];
                removed = _entries.Count != before;
                _dirtyData |= removed;
            }

            if (removed)
            {
                await PersistAsync(cancellationToken);
            }

            return removed;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>Deletes entries that lie completely before <paramref name="today"/>: one-time entries on earlier days and series that ended earlier.</summary>
    /// <returns>The number of entries removed.</returns>
    public async ValueTask<int> ClearPastAsync(int today, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            int removed;
            lock (_gate)
            {
                CalendarEntry[] keep = [.. _entries.Where(e => !IsPast(e, today))];
                removed = _entries.Count - keep.Length;
                _entries = keep;
                _dirtyData |= removed > 0;
            }

            if (removed > 0)
            {
                await PersistAsync(cancellationToken);
            }

            return removed;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>Whether nothing of <paramref name="entry"/> happens on or after <paramref name="today"/>.</summary>
    public static bool IsPast(CalendarEntry entry, int today)
    {
        if (entry.Overrides.Any(o => o.Day >= today && !entry.Skipped.Contains(o.OriginalDay)))
        {
            return false;
        }

        return entry.Repeat switch
        {
            RepeatKind.Once => entry.StartDay < today,
            _ when entry.End == EndKind.Never => false,
            _ => !CalendarRecurrence.BaseDays(entry, today, today + (CalendarLimits.MaxCount * 7 * CalendarLimits.MaxInterval)).Any(),
        };
    }

    /// <summary>Replaces the settings.</summary>
    public async ValueTask SetSettingsAsync(CalendarSettings settings, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                _settings = settings.Sanitized();
                _dirtySettings = true;
            }

            await PersistAsync(cancellationToken);
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>Remembers what happened to an occurrence. Returns <see langword="false"/> when it already has a record, which is what stops a second start.</summary>
    public async ValueTask<bool> RecordAsync(string key, SessionOutcome outcome, long startUnix, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_history.Any(r => r.Key == key))
                {
                    return false;
                }

                IEnumerable<HistoryRecord> all = [.. _history, new HistoryRecord { Key = key, Outcome = outcome, StartUnix = startUnix }];
                _history = SanitizeHistory(all, Time.GetUtcNow().ToUnixTimeSeconds());
                _dirtyHistory = true;
            }

            await PersistAsync(cancellationToken);
            return true;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>Forgets the record of an occurrence (used when a start was refused for a passing reason and may be tried again).</summary>
    public async ValueTask UnrecordAsync(string key, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            bool removed;
            lock (_gate)
            {
                int before = _history.Count;
                _history = [.. _history.Where(r => r.Key != key)];
                removed = _history.Count != before;
                _dirtyHistory |= removed;
            }

            if (removed)
            {
                await PersistAsync(cancellationToken);
            }
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>Writes anything that could not be written before.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteDirtyAsync(cancellationToken);
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>Cleans a title: control characters become spaces, whitespace is trimmed and the length is limited. An empty title becomes "Untitled".</summary>
    public static string CleanTitle(string? title)
    {
        string text = string.Concat((title ?? string.Empty).Select(static c => char.IsControl(c) ? ' ' : c)).Trim();
        if (text.Length > CalendarLimits.MaxTitleLength)
        {
            text = text[..CalendarLimits.MaxTitleLength].TrimEnd();
        }

        return text.Length == 0 ? "Untitled" : text;
    }

    private static int MinDay => new DateOnly(CalendarLimits.MinYear, 1, 1).DayNumber;

    private static int MaxDay => new DateOnly(CalendarLimits.MaxYear, 12, 31).DayNumber;

    private async ValueTask<bool> MutateAsync(int id, Func<CalendarEntry, CalendarEntry?> change, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            bool changed = false;
            lock (_gate)
            {
                CalendarEntry? current = _entries.FirstOrDefault(e => e.Id == id);
                CalendarEntry? updated = current is null ? null : change(current);
                if (current is not null && updated is not null)
                {
                    _entries = [.. _entries.Select(e => e.Id == id ? updated : e)];
                    _dirtyData = true;
                    changed = true;
                }
            }

            if (changed)
            {
                await PersistAsync(cancellationToken);
            }

            return changed;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    private async ValueTask PersistAsync(CancellationToken cancellationToken)
    {
        await WriteDirtyAsync(cancellationToken);
        Changed?.Invoke();
    }

    private async ValueTask WriteDirtyAsync(CancellationToken cancellationToken)
    {
        string? data = null;
        string? history = null;
        string? settings = null;
        lock (_gate)
        {
            if (_dirtyData)
            {
                data = JsonSerializer.Serialize(new CalendarData { NextId = _nextId, Entries = [.. _entries] }, JsonOptions);
            }

            if (_dirtyHistory)
            {
                history = JsonSerializer.Serialize(new CalendarHistory { Records = [.. _history] }, JsonOptions);
            }

            if (_dirtySettings)
            {
                settings = JsonSerializer.Serialize(_settings, JsonOptions);
            }
        }

        if (data is not null && await TryWriteAsync(DataKey, data, cancellationToken))
        {
            lock (_gate)
            {
                _dirtyData = false;
            }
        }

        if (history is not null && await TryWriteAsync(HistoryKey, history, cancellationToken))
        {
            lock (_gate)
            {
                _dirtyHistory = false;
            }
        }

        if (settings is not null && await TryWriteAsync(SettingsKey, settings, cancellationToken))
        {
            lock (_gate)
            {
                _dirtySettings = false;
            }
        }
    }

    private async ValueTask<bool> TryWriteAsync(string key, string json, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.WriteAsync(key, json, cancellationToken);
            return true;
        }
        catch (PluginStorageException ex)
        {
            _logger.Warn($"The calendar could not be saved ({key}): {ex.Message}");
            return false;
        }
    }

    private async ValueTask<T?> ReadAsync<T>(string key, CancellationToken cancellationToken)
        where T : class
    {
        string? json = await _storage.ReadAsync(key, cancellationToken);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException("The document is empty.");
        }
        catch (JsonException ex)
        {
            RecoveredFromCorruption = true;
            _logger.Warn($"The stored calendar document '{key}' is unreadable and was replaced by an empty one: {ex.Message}");
            if (json.Length <= MaxKeptCorruptChars)
            {
                _ = await TryWriteAsync(key + CorruptSuffix, json, cancellationToken);
            }

            return null;
        }
    }

    private static IReadOnlyList<HistoryRecord> SanitizeHistory(IEnumerable<HistoryRecord> records, long nowUnix)
    {
        long oldest = nowUnix - (CalendarLimits.HistoryDays * 86400L);
        return
        [
            .. records
                .Where(r => r is not null && r.Key is { Length: > 0 and <= 32 } && Enum.IsDefined(r.Outcome) && r.StartUnix >= oldest)
                .GroupBy(r => r.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(r => r.StartUnix)
                .TakeLast(CalendarLimits.MaxHistory),
        ];
    }

    private static (IReadOnlyList<CalendarEntry> Entries, int NextId) Sanitize(CalendarData data)
    {
        List<CalendarEntry> clean = [];
        HashSet<int> ids = [];
        foreach (CalendarEntry? raw in (data.Entries ?? []).Take(CalendarLimits.MaxEntries))
        {
            if (raw is null || raw.Id <= 0 || !ids.Add(raw.Id) || !Enum.IsDefined(raw.Kind) || !Enum.IsDefined(raw.Repeat) || !Enum.IsDefined(raw.End)
                || raw.StartDay < MinDay || raw.StartDay > MaxDay)
            {
                continue;
            }

            bool session = raw.Kind == EntryKind.Session;
            if (session && raw.StartMinute is < 0 or > 1439)
            {
                continue;
            }

            var entry = new CalendarEntry
            {
                Id = raw.Id,
                Kind = raw.Kind,
                Title = CleanTitle(raw.Title),
                StartDay = raw.StartDay,
                StartMinute = Math.Clamp(raw.StartMinute, -1, 1439),
                Duration = session
                    ? Math.Clamp(raw.Duration, CalendarLimits.MinSessionMinutes, CalendarLimits.MaxSessionMinutes)
                    : Math.Clamp(raw.Duration, 0, CalendarLimits.MaxTaskMinutes),
                Repeat = raw.Repeat,
                Interval = Math.Clamp(raw.Interval, 1, CalendarLimits.MaxInterval),
                WeekdayMask = raw.WeekdayMask & 0x7F,
                End = raw.Repeat == RepeatKind.Once ? EndKind.Never : raw.End,
                UntilDay = Math.Clamp(raw.UntilDay, raw.StartDay, MaxDay),
                Count = Math.Clamp(raw.Count, 1, CalendarLimits.MaxCount),
                Color = ((raw.Color % CalendarLimits.ColorCount) + CalendarLimits.ColorCount) % CalendarLimits.ColorCount,
                Created = Math.Max(0, raw.Created),
                Skipped = [.. (raw.Skipped ?? []).Distinct().Take(CalendarLimits.MaxSkips)],
                DoneDays = [.. (raw.DoneDays ?? []).Distinct().Take(CalendarLimits.MaxDone)],
            };
            entry.Overrides =
            [
                .. (raw.Overrides ?? [])
                    .Where(o => o is not null && o.Day >= MinDay && o.Day <= MaxDay)
                    .GroupBy(o => o.OriginalDay)
                    .Select(g => g.Last())
                    .Take(CalendarLimits.MaxOverrides)
                    .Select(o => new OccurrenceOverride
                    {
                        OriginalDay = o.OriginalDay,
                        Day = o.Day,
                        Minute = session ? Math.Clamp(o.Minute, 0, 1439) : Math.Clamp(o.Minute, -1, 1439),
                        Duration = session
                            ? Math.Clamp(o.Duration, CalendarLimits.MinSessionMinutes, CalendarLimits.MaxSessionMinutes)
                            : Math.Clamp(o.Duration, 0, CalendarLimits.MaxTaskMinutes),
                        Title = CleanTitle(o.Title),
                    }),
            ];
            clean.Add(entry);
        }

        int next = Math.Max(Math.Max(1, data.NextId), clean.Count == 0 ? 1 : clean.Max(e => e.Id) + 1);
        return (clean, next);
    }
}
