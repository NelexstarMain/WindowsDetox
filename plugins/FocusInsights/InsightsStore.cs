// Recording side of Focus Insights: compact, bounded, persisted state.
//
// Everything is kept as per-day aggregates (focus seconds, outcome counts, blocked attempts, 2-hour buckets, app counts)
// plus a short list of recent sessions. Aggregates are updated when an event arrives, so rendering only has to combine
// at most 90 small records and never touches storage. Storage writes are throttled for the (potentially frequent)
// blocked-process events and immediate for session events.

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Events;
using Blocker.Sdk.Storage;

namespace FocusInsights;

/// <summary>Limits that keep the stored data small.</summary>
public static class InsightsLimits
{
    /// <summary>Days of history that are kept (today included).</summary>
    public const int RetentionDays = 90;

    /// <summary>Number of recent sessions that are kept for the table.</summary>
    public const int MaxRecentSessions = 30;

    /// <summary>Distinct app names that are counted per day; further names still count as blocked attempts.</summary>
    public const int MaxAppsPerDay = 30;

    /// <summary>Longest stored app name.</summary>
    public const int MaxAppNameLength = 40;

    /// <summary>Two-hour buckets per day.</summary>
    public const int HourBuckets = 12;

    /// <summary>Hard cap of day records, a safety net against corrupt data.</summary>
    public const int MaxDayRecords = 120;
}

/// <summary>One finished session. Short JSON names keep the stored data compact.</summary>
public sealed class SessionRecord
{
    /// <summary>Start as Unix seconds.</summary>
    [JsonPropertyName("t")]
    public long Start { get; set; }

    /// <summary>Planned length in seconds.</summary>
    [JsonPropertyName("p")]
    public int Planned { get; set; }

    /// <summary>Actual length in seconds.</summary>
    [JsonPropertyName("a")]
    public int Actual { get; set; }

    /// <summary>End reason: <c>c</c> completed, <c>b</c> break-glass, <c>x</c> other.</summary>
    [JsonPropertyName("r")]
    public string Reason { get; set; } = SessionOutcome.Completed;
}

/// <summary>Aggregates of one local calendar day.</summary>
public sealed class DayRecord
{
    /// <summary>Local day number (<see cref="DateOnly.DayNumber"/>).</summary>
    [JsonPropertyName("d")]
    public int Day { get; set; }

    /// <summary>Seconds spent in sessions that started on this day.</summary>
    [JsonPropertyName("f")]
    public int FocusSeconds { get; set; }

    /// <summary>Sessions that ran to their planned end.</summary>
    [JsonPropertyName("c")]
    public int Completed { get; set; }

    /// <summary>Sessions ended early with break-glass.</summary>
    [JsonPropertyName("b")]
    public int BreakGlass { get; set; }

    /// <summary>Sessions that ended for any other reason.</summary>
    [JsonPropertyName("x")]
    public int OtherEnds { get; set; }

    /// <summary>Blocked attempts.</summary>
    [JsonPropertyName("n")]
    public int Blocked { get; set; }

    /// <summary>Emergency passes used.</summary>
    [JsonPropertyName("e")]
    public int Passes { get; set; }

    /// <summary>Blocked attempts per two-hour bucket (12 values), or <see langword="null"/> when there were none.</summary>
    [JsonPropertyName("h")]
    public int[]? Hours { get; set; }

    /// <summary>Blocked attempts per lower-case app name, or <see langword="null"/>.</summary>
    [JsonPropertyName("a")]
    public Dictionary<string, int>? Apps { get; set; }
}

/// <summary>The session that is running right now (so a restart does not lose it).</summary>
public sealed class PendingSession
{
    /// <summary>Session id in "N" format.</summary>
    [JsonPropertyName("i")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Start as Unix seconds.</summary>
    [JsonPropertyName("t")]
    public long Start { get; set; }

    /// <summary>Planned length in seconds.</summary>
    [JsonPropertyName("p")]
    public int Planned { get; set; }
}

/// <summary>The persisted document.</summary>
public sealed class InsightsData
{
    /// <summary>The schema version this build reads and writes. Newer documents are shown but never overwritten.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Recent sessions, oldest first.</summary>
    [JsonPropertyName("s")]
    public List<SessionRecord> Sessions { get; set; } = [];

    /// <summary>Day aggregates, oldest first.</summary>
    [JsonPropertyName("d")]
    public List<DayRecord> Days { get; set; } = [];

    /// <summary>Running session, if any.</summary>
    [JsonPropertyName("p")]
    public PendingSession? Pending { get; set; }
}

/// <summary>Codes of the session outcomes.</summary>
public static class SessionOutcome
{
    /// <summary>The planned duration elapsed.</summary>
    public const string Completed = "c";

    /// <summary>Ended with break-glass.</summary>
    public const string EndedEarly = "b";

    /// <summary>Any other end.</summary>
    public const string Other = "x";

    /// <summary>Maps an SDK end reason to a code.</summary>
    public static string From(SessionEndReason reason) => reason switch
    {
        SessionEndReason.Expired => Completed,
        SessionEndReason.BreakGlass => EndedEarly,
        _ => Other,
    };

    /// <summary>Returns whether <paramref name="code"/> is a known code.</summary>
    public static bool IsValid(string? code) => code is Completed or EndedEarly or Other;
}

/// <summary>
/// Thread-safe, write-through (throttled for blocked attempts) cache of the recorded data.
/// </summary>
public sealed class InsightsStore
{
    /// <summary>Storage key of the data document.</summary>
    public const string DataKey = "data";

    /// <summary>Storage key of the privacy setting.</summary>
    public const string RecordNamesKey = "record-names";

    /// <summary>Storage key of the raw text of a document that could not be parsed, kept for manual recovery.</summary>
    public const string UnreadableKey = "data-unreadable";

    /// <summary>Longest unreadable text that is kept; it bounds how much quota the copy can use.</summary>
    public const int MaxUnreadableChars = 64 * 1024;

    /// <summary>Minimum time between storage writes triggered by blocked attempts.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private const int MaxSessionSeconds = 7 * 24 * 3600;

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private InsightsData _data = new();
    private bool _recordNames = true;
    private bool _dirty;
    private bool _readOnly;
    private ITimer? _flushTimer;
    private bool _flushedOnce;
    private long _lastFlush;
    private long _version;
    private long _written;

    /// <summary>Creates the store on top of the plugin storage.</summary>
    public InsightsStore(IPluginStorage storage, TimeProvider time, IPluginLogger logger)
    {
        _storage = storage;
        Time = time;
        _logger = logger;
    }

    /// <summary>Raised after every change so contributions can ask for a new render.</summary>
    public event Action? Changed;

    /// <summary>The plugin clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>Time zone used to assign events to days and hours.</summary>
    public TimeZoneInfo LocalTimeZone => Time.LocalTimeZone;

    /// <summary>The current local day number.</summary>
    public int CurrentDay => DayOf(Time.GetUtcNow());

    /// <summary>Whether blocked app names are recorded.</summary>
    public bool RecordNames
    {
        get
        {
            lock (_gate)
            {
                return _recordNames;
            }
        }
    }

    /// <summary>
    /// Loads persisted data. Corrupt data starts empty after a copy was kept under <see cref="UnreadableKey"/>; data written
    /// by a newer format version is shown but the store never writes again (read-only), so a downgrade cannot destroy it.
    /// </summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        InsightsData? loaded = null;
        bool readOnly = false;
        string? json = await _storage.ReadAsync(DataKey, cancellationToken);
        if (json is not null)
        {
            try
            {
                loaded = JsonSerializer.Deserialize<InsightsData>(json, JsonOptions);
                if (loaded is { Version: > InsightsData.CurrentVersion })
                {
                    readOnly = true;
                    _logger.Warn($"Recorded data uses format version {loaded.Version}, newer than {InsightsData.CurrentVersion}; it is shown read-only and changes are not saved.");
                }
            }
            catch (JsonException ex)
            {
                _logger.Warn($"Recorded data is unreadable and was ignored: {ex.Message}");
                await KeepUnreadableCopyAsync(json, cancellationToken);
            }
        }

        string? names = await _storage.ReadAsync(RecordNamesKey, cancellationToken);
        lock (_gate)
        {
            _readOnly = readOnly;
            _data = Sanitize(loaded, CurrentDay);
            if (bool.TryParse(names, out bool enabled))
            {
                _recordNames = enabled;
            }
        }
    }

    /// <summary>Remembers a started session.</summary>
    public async ValueTask OnSessionStartedAsync(SessionStarted started, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _data.Pending = new PendingSession
            {
                Id = started.SessionId.ToString("N"),
                Start = started.OccurredAt.ToUnixTimeSeconds(),
                Planned = ClampSeconds(started.PlannedDuration),
            };
        }

        await PersistAsync(cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>
    /// Records a finished session and returns it. Ends without a matching start (plugin installed mid-session) are ignored
    /// and return <see langword="null"/>.
    /// </summary>
    public async ValueTask<SessionRecord?> OnSessionEndedAsync(SessionEnded ended, CancellationToken cancellationToken)
    {
        bool recorded;
        SessionRecord? record = null;
        lock (_gate)
        {
            int newestBefore = NewestDay();
            PendingSession? pending = _data.Pending;
            recorded = pending is not null && pending.Id == ended.SessionId.ToString("N");
            if (pending is not null && recorded)
            {
                int actual = ClampSeconds(ended.OccurredAt - DateTimeOffset.FromUnixTimeSeconds(pending.Start));
                string outcome = SessionOutcome.From(ended.Reason);
                record = new SessionRecord { Start = pending.Start, Planned = pending.Planned, Actual = actual, Reason = outcome };
                _data.Sessions.Add(record);
                DayRecord day = GetDay(DayOf(DateTimeOffset.FromUnixTimeSeconds(pending.Start)));
                day.FocusSeconds += actual;
                switch (outcome)
                {
                    case SessionOutcome.Completed:
                        day.Completed++;
                        break;
                    case SessionOutcome.EndedEarly:
                        day.BreakGlass++;
                        break;
                    default:
                        day.OtherEnds++;
                        break;
                }

                _data.Pending = null;
                Prune(newestBefore);
            }
        }

        if (recorded)
        {
            await PersistAsync(cancellationToken);
            Changed?.Invoke();
        }

        return record;
    }

    /// <summary>Counts a blocked attempt.</summary>
    public async ValueTask OnProcessBlockedAsync(ProcessBlocked blocked, CancellationToken cancellationToken)
    {
        string? name = CleanName(blocked.Process.ImageName);
        lock (_gate)
        {
            int newestBefore = NewestDay();
            TimeZoneInfo zone = Time.LocalTimeZone;
            DateTimeOffset local = TimeZoneInfo.ConvertTime(blocked.OccurredAt, zone);
            DayRecord day = GetDay(DateOnly.FromDateTime(local.DateTime).DayNumber);
            day.Blocked++;
            day.Hours ??= new int[InsightsLimits.HourBuckets];
            day.Hours[local.Hour / 2]++;
            if (_recordNames && name is not null)
            {
                day.Apps ??= new Dictionary<string, int>(StringComparer.Ordinal);
                if (day.Apps.ContainsKey(name) || day.Apps.Count < InsightsLimits.MaxAppsPerDay)
                {
                    day.Apps[name] = day.Apps.GetValueOrDefault(name) + 1;
                }
            }

            MarkDirty();
            Prune(newestBefore);
        }

        await FlushIfDueAsync(cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>Counts an emergency pass.</summary>
    public async ValueTask OnEmergencyPassAsync(EmergencyPassStarted pass, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            int newestBefore = NewestDay();
            GetDay(DayOf(pass.OccurredAt)).Passes++;
            Prune(newestBefore);
        }

        await PersistAsync(cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>Changes and persists the privacy setting. Names already recorded stay until the data is cleared.</summary>
    public async ValueTask SetRecordNamesAsync(bool value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _recordNames = value;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // Read the latest value inside the gate so overlapping changes can never leave an older value on disk.
            bool latest = RecordNames;
            if (!_readOnly)
            {
                await _storage.WriteAsync(RecordNamesKey, latest ? "true" : "false", cancellationToken);
            }
        }
        catch (IOException ex)
        {
            _logger.Warn($"The privacy setting could not be saved: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Deletes everything that was recorded. The privacy setting is kept.</summary>
    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        long version;
        lock (_gate)
        {
            _data = new InsightsData();
            _dirty = false;
            DisposeFlushTimer();
            version = ++_version;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!_readOnly)
            {
                await _storage.DeleteAsync(DataKey, cancellationToken);
                _written = version;
            }
        }
        finally
        {
            _writeGate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Writes pending changes to storage.</summary>
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

    /// <summary>Combines the stored aggregates for the current day. Cheap: at most 90 day records.</summary>
    public InsightsView GetView()
    {
        int today = CurrentDay;
        lock (_gate)
        {
            return InsightsAggregator.Build(_data.Days, _data.Sessions, today);
        }
    }

    /// <summary>Returns a printable, lower-case, length-limited app name, or <see langword="null"/> when nothing is left.</summary>
    public static string? CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (!char.IsControl(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length > InsightsLimits.MaxAppNameLength)
        {
            cleaned = cleaned[..InsightsLimits.MaxAppNameLength];
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static int ClampSeconds(TimeSpan span) => (int)Math.Clamp(span.TotalSeconds, 0, MaxSessionSeconds);

    private int DayOf(DateTimeOffset moment) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, Time.LocalTimeZone).DateTime).DayNumber;

    // Callers hold _gate. Finds or inserts the record of a day, keeping the list ordered by day.
    private DayRecord GetDay(int dayNumber)
    {
        List<DayRecord> days = _data.Days;
        int index = days.Count;
        while (index > 0 && days[index - 1].Day > dayNumber)
        {
            index--;
        }

        if (index > 0 && days[index - 1].Day == dayNumber)
        {
            return days[index - 1];
        }

        var created = new DayRecord { Day = dayNumber };
        days.Insert(index, created);
        return created;
    }

    // A clock that jumps far ahead (dead CMOS battery, bad sync) must not age out the whole history. When today is more than a
    // full window past the newest day stored before this change, keep the window that ends at that newest day instead; once a
    // record exists for the new day the gap is gone and normal pruning resumes.
    private static int OldestKeptDay(int newestStoredDay, int today) =>
        (today - newestStoredDay > InsightsLimits.RetentionDays ? newestStoredDay : today) - InsightsLimits.RetentionDays + 1;

    // Callers hold _gate. The newest stored day, read before a change adds a record for the (possibly wrong) current day.
    private int NewestDay() => _data.Days.Count == 0 ? CurrentDay : _data.Days[^1].Day;

    // Callers hold _gate. Every change after a snapshot makes the version differ, so that snapshot cannot clear the flag.
    private void MarkDirty()
    {
        _dirty = true;
        _version++;
    }

    // Callers hold _gate.
    private void DisposeFlushTimer()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
    }

    // Callers hold _gate. Retention: 90 days, 30 recent sessions, 120 day records.
    private void Prune(int newestStoredDay)
    {
        int oldest = OldestKeptDay(newestStoredDay, CurrentDay);
        _data.Days.RemoveAll(d => d.Day < oldest);
        if (_data.Days.Count > InsightsLimits.MaxDayRecords)
        {
            _data.Days.RemoveRange(0, _data.Days.Count - InsightsLimits.MaxDayRecords);
        }

        _data.Sessions.RemoveAll(s => DayOf(DateTimeOffset.FromUnixTimeSeconds(s.Start)) < oldest);
        if (_data.Sessions.Count > InsightsLimits.MaxRecentSessions)
        {
            _data.Sessions.RemoveRange(0, _data.Sessions.Count - InsightsLimits.MaxRecentSessions);
        }
    }

    private async ValueTask FlushIfDueAsync(CancellationToken cancellationToken)
    {
        bool due;
        lock (_gate)
        {
            due = _dirty && (!_flushedOnce || Time.GetElapsedTime(_lastFlush) >= FlushInterval);
            if (_dirty && !due && !_readOnly && _flushTimer is null)
            {
                // Throttled: make sure the attempt still reaches storage when the interval ends, even if no event follows.
                _flushTimer = Time.CreateTimer(OnFlushTimer, null, FlushInterval - Time.GetElapsedTime(_lastFlush), Timeout.InfiniteTimeSpan);
            }
        }

        if (due)
        {
            await PersistAsync(cancellationToken);
        }
    }

    private void OnFlushTimer(object? state)
    {
        lock (_gate)
        {
            DisposeFlushTimer();
        }

        _ = FlushAsync(CancellationToken.None).AsTask();
    }

    private async ValueTask PersistAsync(CancellationToken cancellationToken)
    {
        string json;
        long version;
        lock (_gate)
        {
            if (_readOnly)
            {
                return;
            }

            json = JsonSerializer.Serialize(_data, JsonOptions);
            version = ++_version;
            _flushedOnce = true;
            _lastFlush = Time.GetTimestamp();
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (version > _written)
            {
                await _storage.WriteAsync(DataKey, json, cancellationToken);
                _written = version;
                lock (_gate)
                {
                    if (version == _version)
                    {
                        _dirty = false;
                        DisposeFlushTimer();
                    }
                }
            }
        }
        catch (IOException ex)
        {
            _logger.Warn($"Recorded data could not be saved: {ex.Message}");
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

    // Keeps the first unreadable document only: later corruption must not eat the quota again or replace the original evidence.
    private async ValueTask KeepUnreadableCopyAsync(string raw, CancellationToken cancellationToken)
    {
        try
        {
            if (await _storage.ReadAsync(UnreadableKey, cancellationToken) is null)
            {
                await _storage.WriteAsync(UnreadableKey, raw.Length > MaxUnreadableChars ? raw[..MaxUnreadableChars] : raw, cancellationToken);
            }
        }
        catch (IOException ex)
        {
            _logger.Warn($"A copy of the unreadable recorded data could not be kept: {ex.Message}");
        }
    }

    // Loaded data is untrusted: drop or repair anything that violates the invariants the rest of the code relies on.
    private static InsightsData Sanitize(InsightsData? loaded, int today)
    {
        if (loaded is null)
        {
            return new InsightsData();
        }

        int oldest = OldestKeptDay(
            (loaded.Days ?? []).Where(d => d is not null && d.Day <= DateOnly.MaxValue.DayNumber).Select(d => d.Day).DefaultIfEmpty(today).Max(),
            today);
        List<DayRecord> days = [];
        foreach (DayRecord day in (loaded.Days ?? []).Where(d => d is not null && d.Day >= oldest && d.Day <= DateOnly.MaxValue.DayNumber).OrderBy(d => d.Day))
        {
            if (days.Count > 0 && days[^1].Day == day.Day)
            {
                continue;
            }

            day.FocusSeconds = Math.Max(0, day.FocusSeconds);
            day.Completed = Math.Max(0, day.Completed);
            day.BreakGlass = Math.Max(0, day.BreakGlass);
            day.OtherEnds = Math.Max(0, day.OtherEnds);
            day.Blocked = Math.Max(0, day.Blocked);
            day.Passes = Math.Max(0, day.Passes);
            day.Hours = day.Hours is { Length: InsightsLimits.HourBuckets } ? [.. day.Hours.Select(h => Math.Max(0, h))] : null;
            day.Apps = CleanApps(day.Apps);
            days.Add(day);
        }

        List<SessionRecord> sessions =
        [
            .. (loaded.Sessions ?? [])
                .Where(s => s is not null && SessionOutcome.IsValid(s.Reason) && s.Start > 0)
                .TakeLast(InsightsLimits.MaxRecentSessions),
        ];
        foreach (SessionRecord session in sessions)
        {
            session.Planned = Math.Clamp(session.Planned, 0, MaxSessionSeconds);
            session.Actual = Math.Clamp(session.Actual, 0, MaxSessionSeconds);
        }

        PendingSession? pending = loaded.Pending is { Id.Length: > 0, Planned: >= 0, Start: > 0 } p ? p : null;
        return new InsightsData { Days = [.. days.TakeLast(InsightsLimits.MaxDayRecords)], Sessions = sessions, Pending = pending };
    }

    private static Dictionary<string, int>? CleanApps(Dictionary<string, int>? apps)
    {
        if (apps is null)
        {
            return null;
        }

        Dictionary<string, int> cleaned = new(StringComparer.Ordinal);
        foreach ((string name, int count) in apps)
        {
            if (count > 0 && CleanName(name) is { } clean && clean == name && cleaned.Count < InsightsLimits.MaxAppsPerDay)
            {
                cleaned[clean] = count;
            }
        }

        return cleaned.Count == 0 ? null : cleaned;
    }
}
