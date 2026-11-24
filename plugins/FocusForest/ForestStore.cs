// Game state of the Focus Forest: coins, unlocked species, inventory and the objects standing on the growing field.
//
// A completed session earns coins and plants one tree or bush; a session ended with break-glass plants a withered one.
// The state is small and bounded (at most 16 x 16 objects), kept in memory and written through to plugin storage after
// every change. Selection state (which object is being moved, which decoration is being placed) is never persisted.
//
// Positions come from ForestWorld: a new object is placed by the layout, a manual move pins the object where the user put it,
// and the path tiles are derived from the objects (never stored). A running session shows as a growing sapling.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Events;
using Blocker.Sdk.Storage;

namespace FocusForest;

/// <summary>Limits of the forest field and its economy.</summary>
public static class ForestLimits
{
    /// <summary>Edge length of the field of a new forest.</summary>
    public const int StartSize = 6;

    /// <summary>Largest field edge.</summary>
    public const int MaxSize = 16;

    /// <summary>How much the edge grows when the field fills up; objects stay centred.</summary>
    public const int GrowStep = 2;

    /// <summary>Hard cap of stored objects; in practice the paths and the free share of a field allow fewer.</summary>
    public const int MaxObjects = MaxSize * MaxSize;

    /// <summary>Coins charged for removing a withered plant.</summary>
    public const int WitheredRemovalFee = 10;

    /// <summary>Species planted until another one is chosen.</summary>
    public const string DefaultSpecies = "oak";

    /// <summary>Upper bound of the coin balance, a safety net against corrupt data and overflow.</summary>
    public const int MaxCoins = 1_000_000_000;

    /// <summary>Most pieces of one decoration kept in the inventory.</summary>
    public const int MaxInventoryPerItem = 99;

    /// <summary>Longest session length that is stored for a plant.</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    /// <summary>Extra coins per consecutive day of completed sessions, in percent of the session's minutes.</summary>
    public const int StreakBonusStepPercent = 10;

    /// <summary>Largest streak bonus, in percent.</summary>
    public const int StreakBonusMaxPercent = 50;
}

/// <summary>One plant or decoration standing on the field. Short JSON names keep the stored data compact.</summary>
public sealed class ForestObject
{
    /// <summary>Unique id within the forest.</summary>
    [JsonPropertyName("i")]
    public int Id { get; set; }

    /// <summary>Catalogue id of the species.</summary>
    [JsonPropertyName("k")]
    public string Species { get; set; } = string.Empty;

    /// <summary>The <see cref="ForestStage"/> as a number (ignored for decorations).</summary>
    [JsonPropertyName("g")]
    public int Stage { get; set; }

    /// <summary>Zero-based tile column.</summary>
    [JsonPropertyName("c")]
    public int Column { get; set; }

    /// <summary>Zero-based tile row.</summary>
    [JsonPropertyName("r")]
    public int Row { get; set; }

    /// <summary>When it was planted or placed, as Unix seconds.</summary>
    [JsonPropertyName("t")]
    public long Planted { get; set; }

    /// <summary>Length of the session that planted it, in minutes (0 for decorations).</summary>
    [JsonPropertyName("m")]
    public int Minutes { get; set; }

    /// <summary>The user put it here: the layout never moves it and paths route around it.</summary>
    [JsonPropertyName("pn")]
    public bool Pinned { get; set; }

    /// <summary>A sapling that stands for the running session; it grows up when the session completes.</summary>
    [JsonPropertyName("gw")]
    public bool Growing { get; set; }

    /// <summary>The stage as an enum.</summary>
    [JsonIgnore]
    public ForestStage StageValue => (ForestStage)Stage;

    internal ForestObject Clone() => (ForestObject)MemberwiseClone();
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

    /// <summary>Id of the growing sapling that stands for this session (0: none).</summary>
    [JsonPropertyName("o")]
    public int PlantId { get; set; }
}

/// <summary>The persisted document.</summary>
public sealed class ForestData
{
    /// <summary>The schema version this build reads and writes. Newer documents are shown but never overwritten.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Coin balance.</summary>
    [JsonPropertyName("c")]
    public int Coins { get; set; }

    /// <summary>Unlocked tree and bush ids.</summary>
    [JsonPropertyName("u")]
    public List<string> Unlocked { get; set; } = [];

    /// <summary>The species the next session plants.</summary>
    [JsonPropertyName("s")]
    public string Chosen { get; set; } = ForestLimits.DefaultSpecies;

    /// <summary>Id given to the next object.</summary>
    [JsonPropertyName("n")]
    public int NextId { get; set; } = 1;

    /// <summary>Field edge length.</summary>
    [JsonPropertyName("z")]
    public int Size { get; set; } = ForestLimits.StartSize;

    /// <summary>Bought decorations that are not placed yet, by catalogue id.</summary>
    [JsonPropertyName("b")]
    public Dictionary<string, int> Inventory { get; set; } = [];

    /// <summary>Everything standing on the field.</summary>
    [JsonPropertyName("o")]
    public List<ForestObject> Objects { get; set; } = [];

    /// <summary>Running session, if any.</summary>
    [JsonPropertyName("p")]
    public PendingSession? Pending { get; set; }

    /// <summary>Consecutive days with a completed session, up to <see cref="LastDay"/>.</summary>
    [JsonPropertyName("sk")]
    public int Streak { get; set; }

    /// <summary>Local day number of the last completed session (0: none).</summary>
    [JsonPropertyName("ld")]
    public int LastDay { get; set; }
}

/// <summary>An immutable snapshot of the forest for rendering.</summary>
public sealed record ForestView(
    int Coins,
    string Chosen,
    IReadOnlySet<string> Unlocked,
    IReadOnlyDictionary<string, int> Inventory,
    int Size,
    IReadOnlyList<ForestObject> Objects,
    int? SelectedObject,
    string? PlacingDecoration)
{
    /// <summary>The path tiles of the village, derived from the objects.</summary>
    public IReadOnlyList<ForestPathTile> Paths { get; init; } = [];

    /// <summary>Consecutive days with a completed session as of the last one.</summary>
    public int Streak { get; init; }

    /// <summary>Whether a click on an empty tile would do something.</summary>
    public bool IsPicking => SelectedObject is not null || PlacingDecoration is not null;
}

/// <summary>Thread-safe, write-through cache of the forest.</summary>
public sealed class ForestStore
{
    /// <summary>Storage key of the forest document.</summary>
    public const string DataKey = "forest";

    /// <summary>Storage key of the raw text of a document that could not be parsed, kept for manual recovery.</summary>
    public const string UnreadableKey = "forest-unreadable";

    /// <summary>Longest unreadable text that is kept; it bounds how much quota the copy can use.</summary>
    public const int MaxUnreadableChars = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ForestData _data = Fresh();
    private IReadOnlyList<ForestPathTile> _paths = [];
    private bool _readOnly;
    private int? _selectedObject;
    private string? _placingDecoration;

    /// <summary>Creates the store on top of the plugin storage.</summary>
    public ForestStore(IPluginStorage storage, TimeProvider time, IPluginLogger logger)
    {
        _storage = storage;
        Time = time;
        _logger = logger;
    }

    /// <summary>Raised after every change so contributions can ask for a new render.</summary>
    public event Action? Changed;

    /// <summary>The plugin clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>Quarter turns (0-3) of an object, derived from its id so the field looks varied but never changes.</summary>
    public static int RotationOf(int id) => (int)(((uint)id * 2654435761u) >> 30);

    /// <summary>Every tile of a square field, nearest to the centre first; ties go to the upper row, then the left column.</summary>
    public static IEnumerable<(int Column, int Row)> TilesFromCentre(int size)
    {
        double centre = (size - 1) / 2.0;
        return Enumerable.Range(0, size * size)
            .Select(i => (Column: i % size, Row: i / size))
            .OrderBy(t => ((t.Column - centre) * (t.Column - centre)) + ((t.Row - centre) * (t.Row - centre)));
    }

    /// <summary>
    /// Loads persisted data. Corrupt data starts a fresh forest after a copy was kept under <see cref="UnreadableKey"/>; data
    /// written by a newer format version is shown but the store never writes again, so a downgrade cannot destroy it.
    /// </summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        ForestData? loaded = null;
        bool readOnly = false;
        string? json = await _storage.ReadAsync(DataKey, cancellationToken);
        if (json is not null)
        {
            try
            {
                loaded = JsonSerializer.Deserialize<ForestData>(json, JsonOptions);
                if (loaded is { Version: > ForestData.CurrentVersion })
                {
                    readOnly = true;
                    _logger.Warn($"Forest data uses format version {loaded.Version}, newer than {ForestData.CurrentVersion}; it is shown read-only and changes are not saved.");
                }
            }
            catch (JsonException ex)
            {
                _logger.Warn($"Forest data is unreadable and was ignored: {ex.Message}");
                await KeepUnreadableCopyAsync(json, cancellationToken);
            }
        }

        lock (_gate)
        {
            _readOnly = readOnly;
            _data = Sanitize(loaded);
            _paths = [];
            TryLayout(keepUnpinned: true);
        }
    }

    /// <summary>Snapshot for rendering.</summary>
    public ForestView GetView()
    {
        lock (_gate)
        {
            return new ForestView(
                _data.Coins,
                _data.Chosen,
                new HashSet<string>(_data.Unlocked, StringComparer.Ordinal),
                new Dictionary<string, int>(_data.Inventory, StringComparer.Ordinal),
                _data.Size,
                [.. _data.Objects.Select(o => o.Clone())],
                _selectedObject,
                _placingDecoration)
            {
                Paths = _paths,
                Streak = _data.Streak,
            };
        }
    }

    /// <summary>Remembers a started session and plants a growing sapling of the chosen species for it.</summary>
    public async ValueTask OnSessionStartedAsync(SessionStarted started, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _data.Objects.RemoveAll(o => o.Growing);   // a start without an end leaves its sapling behind
            _data.Pending = new PendingSession
            {
                Id = started.SessionId.ToString("N"),
                Start = started.OccurredAt.ToUnixTimeSeconds(),
                Planned = ClampSeconds(started.PlannedDuration),
            };
            PlantGrowingSapling();
        }

        await PersistAsync(cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies a finished session: running to its planned end earns one coin per minute (plus the streak bonus) and grows the sapling to
    /// the stage of its length, break-glass withers it, anything else takes it away. Ends without a matching start (plugin installed
    /// mid-session) are ignored.
    /// </summary>
    public async ValueTask OnSessionEndedAsync(SessionEnded ended, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_data.Pending is not { } pending || pending.Id != ended.SessionId.ToString("N"))
            {
                return;
            }

            _data.Pending = null;
            ForestObject? sapling = _data.Objects.FirstOrDefault(o => o.Growing && o.Id == pending.PlantId);
            if (ended.Reason is SessionEndReason.Expired or SessionEndReason.BreakGlass)
            {
                int minutes = Math.Clamp(ClampSeconds(ended.OccurredAt - DateTimeOffset.FromUnixTimeSeconds(pending.Start)) / 60, 0, ForestLimits.MaxMinutes);
                bool completed = ended.Reason == SessionEndReason.Expired;
                if (completed)
                {
                    _data.Coins = (int)Math.Min((long)_data.Coins + EarnedCoins(minutes, ended.OccurredAt), ForestLimits.MaxCoins);
                }

                ForestStage stage = completed ? ForestCatalog.StageFor(minutes) : ForestStage.Withered;
                if (sapling is not null)
                {
                    sapling.Growing = false;
                    sapling.Stage = (int)stage;
                    sapling.Minutes = minutes;
                }
                else if (!TryAdd(new ForestObject { Species = _data.Chosen, Stage = (int)stage, Planted = pending.Start, Minutes = minutes, Column = -1, Row = -1 }))
                {
                    _logger.Warn("The forest is full; this session's plant was not placed.");
                }
            }
            else if (sapling is not null)
            {
                _data.Objects.Remove(sapling);
            }
        }

        await PersistAsync(cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>Starts a fresh forest: coins, unlocks, inventory and every object are dropped; a running session stays tracked.</summary>
    public async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _data = new ForestData { Unlocked = [.. FreeSpecies()], Pending = _data.Pending };
            _paths = [];
            _selectedObject = null;
            _placingDecoration = null;
            PlantGrowingSapling();
        }

        await PersistAsync(cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>Unlocks a tree or bush for its price. Returns whether it happened.</summary>
    public async ValueTask<bool> UnlockAsync(string speciesId, CancellationToken cancellationToken)
    {
        bool done;
        lock (_gate)
        {
            ForestSpecies? species = ForestCatalog.Find(speciesId);
            done = species is { Kind: not ForestKind.Decoration } && !_data.Unlocked.Contains(species.Id) && TrySpend(species.Price);
            if (done)
            {
                _data.Unlocked.Add(speciesId);
            }
        }

        return await FinishAsync(done, cancellationToken);
    }

    /// <summary>Chooses an unlocked tree or bush as the species of the next planting.</summary>
    public async ValueTask<bool> SelectSpeciesAsync(string speciesId, CancellationToken cancellationToken)
    {
        bool done;
        lock (_gate)
        {
            done = _data.Unlocked.Contains(speciesId) && _data.Chosen != speciesId;
            if (done)
            {
                _data.Chosen = speciesId;
            }
        }

        return await FinishAsync(done, cancellationToken);
    }

    /// <summary>Buys one piece of a decoration into the inventory.</summary>
    public async ValueTask<bool> BuyDecorationAsync(string speciesId, CancellationToken cancellationToken)
    {
        bool done;
        lock (_gate)
        {
            ForestSpecies? species = ForestCatalog.Find(speciesId);
            done = species is { Kind: ForestKind.Decoration }
                && _data.Inventory.GetValueOrDefault(speciesId) < ForestLimits.MaxInventoryPerItem
                && TrySpend(species.Price);
            if (done)
            {
                _data.Inventory[speciesId] = _data.Inventory.GetValueOrDefault(speciesId) + 1;
            }
        }

        return await FinishAsync(done, cancellationToken);
    }

    /// <summary>Removes a withered plant for <see cref="ForestLimits.WitheredRemovalFee"/> coins.</summary>
    public async ValueTask<bool> RemoveWitheredAsync(int objectId, CancellationToken cancellationToken)
    {
        bool done;
        lock (_gate)
        {
            ForestObject? target = _data.Objects.FirstOrDefault(o => o.Id == objectId);
            done = target is { StageValue: ForestStage.Withered } && TrySpend(ForestLimits.WitheredRemovalFee);
            if (done)
            {
                _data.Objects.Remove(target!);
                _selectedObject = null;
            }
        }

        return await FinishAsync(done, cancellationToken);
    }

    /// <summary>
    /// Takes a placed decoration off the field and puts it back into the inventory (no refund, no coin change), so it can be placed again;
    /// the paths re-route. Plants and unknown ids change nothing.
    /// </summary>
    public async ValueTask<bool> RemoveDecorationAsync(int objectId, CancellationToken cancellationToken)
    {
        bool done;
        lock (_gate)
        {
            ForestObject? target = _data.Objects.FirstOrDefault(o => o.Id == objectId);
            done = target is not null && ForestCatalog.Find(target.Species)?.Kind == ForestKind.Decoration
                && _data.Inventory.GetValueOrDefault(target.Species) < ForestLimits.MaxInventoryPerItem;
            if (done)
            {
                _data.Objects.Remove(target!);
                _data.Inventory[target!.Species] = _data.Inventory.GetValueOrDefault(target.Species) + 1;
                _selectedObject = null;
                TryLayout(keepUnpinned: true);   // the street to it disappears; a full field simply keeps its paths
            }
        }

        return await FinishAsync(done, cancellationToken);
    }

    /// <summary>Starts or stops placing an owned decoration; the next click on an empty tile puts it there.</summary>
    public void TogglePlacing(string decorationId)
    {
        lock (_gate)
        {
            _placingDecoration = _placingDecoration == decorationId || _data.Inventory.GetValueOrDefault(decorationId) <= 0 ? null : decorationId;
            _selectedObject = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Selects an object to move, or deselects it when it already is selected.</summary>
    public void ToggleSelect(int objectId)
    {
        lock (_gate)
        {
            _selectedObject = _selectedObject == objectId || _data.Objects.All(o => o.Id != objectId) ? null : objectId;
            _placingDecoration = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Drops any pending move or placement.</summary>
    public void CancelSelection()
    {
        lock (_gate)
        {
            _selectedObject = null;
            _placingDecoration = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Handles a click on an empty tile: places the pending decoration or moves the selected object there. Either way the object is pinned.</summary>
    public async ValueTask<bool> PickAsync(int column, int row, CancellationToken cancellationToken)
    {
        bool done = false;
        lock (_gate)
        {
            if (IsFree(column, row))
            {
                if (_placingDecoration is { } decoration && _data.Inventory.GetValueOrDefault(decoration) > 0)
                {
                    done = TryAdd(new ForestObject
                    {
                        Species = decoration,
                        Stage = (int)ForestStage.Mature,
                        Planted = Time.GetUtcNow().ToUnixTimeSeconds(),
                        Column = column,
                        Row = row,
                        Pinned = true,
                    });
                    if (done)
                    {
                        int left = _data.Inventory[decoration] - 1;
                        if (left > 0)
                        {
                            _data.Inventory[decoration] = left;
                        }
                        else
                        {
                            _data.Inventory.Remove(decoration);
                            _placingDecoration = null;
                        }
                    }
                }
                else if (_selectedObject is { } id && _data.Objects.FirstOrDefault(o => o.Id == id) is { } moving)
                {
                    (int oldColumn, int oldRow, bool wasPinned) = (moving.Column, moving.Row, moving.Pinned);
                    (moving.Column, moving.Row, moving.Pinned) = (column, row, true);
                    done = TryLayout(keepUnpinned: true);
                    if (done)
                    {
                        _selectedObject = null;
                    }
                    else
                    {
                        (moving.Column, moving.Row, moving.Pinned) = (oldColumn, oldRow, wasPinned);
                    }
                }
            }
        }

        return await FinishAsync(done, cancellationToken);
    }

    /// <summary>
    /// Lays out every unpinned object again (<paramref name="unpinAll"/> releases the pins first), so paths and groves form from scratch.
    /// Returns whether the layout changed; a field that cannot hold everything keeps its previous arrangement.
    /// </summary>
    public async ValueTask<bool> ArrangeAsync(bool unpinAll, CancellationToken cancellationToken)
    {
        bool done;
        lock (_gate)
        {
            bool[] pins = [.. _data.Objects.Select(o => o.Pinned)];
            if (unpinAll)
            {
                _data.Objects.ForEach(o => o.Pinned = false);
            }

            done = TryLayout(keepUnpinned: false);
            if (!done)
            {
                for (int i = 0; i < pins.Length; i++)
                {
                    _data.Objects[i].Pinned = pins[i];
                }
            }
        }

        return await FinishAsync(done, cancellationToken);
    }

    /// <summary>Pins an object where it stands (paths route around it, arranging leaves it alone) or releases it again.</summary>
    public async ValueTask<bool> SetPinnedAsync(string objectId, bool pinned, CancellationToken cancellationToken)
    {
        bool done = false;
        lock (_gate)
        {
            if (int.TryParse(objectId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id)
                && _data.Objects.FirstOrDefault(o => o.Id == id) is { } target
                && target.Pinned != pinned)
            {
                target.Pinned = pinned;
                TryLayout(keepUnpinned: true);   // the paths may route differently now; a full field simply keeps them
                done = true;
            }
        }

        return await FinishAsync(done, cancellationToken);
    }

    // Callers hold _gate.
    private bool TrySpend(int price)
    {
        if (_data.Coins < price)
        {
            return false;
        }

        _data.Coins -= price;
        return true;
    }

    // Callers hold _gate.
    private bool IsFree(int column, int row) =>
        column >= 0 && row >= 0 && column < _data.Size && row < _data.Size
        && !ForestWorld.IsPlazaApproach(_data.Size, new GridTile(column, row))
        && !_data.Objects.Any(o => o.Column == column && o.Row == row);

    // Callers hold _gate. Adds the object and lays the field out again; it is placed by the layout unless its column is set (>= 0). Returns false, changing nothing, when everything does not fit.
    private bool TryAdd(ForestObject o)
    {
        if (_data.Objects.Count >= ForestLimits.MaxObjects)
        {
            return false;
        }

        o.Id = _data.NextId;
        _data.Objects.Add(o);
        if (!TryLayout(keepUnpinned: true))
        {
            _data.Objects.Remove(o);
            return false;
        }

        _data.NextId++;
        return true;
    }

    // Callers hold _gate. Runs the world layout: pinned objects stay, the others stay too when keepUnpinned is set (else they are
    // placed from scratch). Returns false, changing nothing, when some object would find no tile.
    private bool TryLayout(bool keepUnpinned)
    {
        WorldObject[] world =
        [
            .. _data.Objects.Select(o => new WorldObject(
                o.Id,
                o.Species,
                ForestCatalog.Find(o.Species)!.Kind,
                o.StageValue,
                (o.Pinned || keepUnpinned) && o.Column >= 0 ? new GridTile(o.Column, o.Row) : null,
                o.Pinned)),
        ];
        WorldLayout layout = ForestWorld.Arrange(_data.Size, world);
        if (layout.Positions.Count < world.Length)
        {
            return false;
        }

        _data.Size = layout.Size;
        foreach (ForestObject o in _data.Objects)
        {
            GridTile tile = layout.Positions[o.Id];
            (o.Column, o.Row) = (tile.Column, tile.Row);
        }

        _paths = layout.Paths;
        return true;
    }

    // Callers hold _gate. Plants the sapling of the running session, if there is one.
    private void PlantGrowingSapling()
    {
        if (_data.Pending is not { } pending)
        {
            return;
        }

        var sapling = new ForestObject { Species = _data.Chosen, Stage = (int)ForestStage.Sapling, Planted = pending.Start, Growing = true, Column = -1, Row = -1 };
        if (TryAdd(sapling))
        {
            pending.PlantId = sapling.Id;
        }
        else
        {
            _logger.Warn("The forest is full; this session has no growing sapling.");
        }
    }

    // Callers hold _gate. A completed session earns its minutes plus a bonus per consecutive day, capped.
    private int EarnedCoins(int minutes, DateTimeOffset at)
    {
        int day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Time.LocalTimeZone).DateTime).DayNumber;
        if (_data.LastDay != day)
        {
            _data.Streak = _data.LastDay == day - 1 ? _data.Streak + 1 : 1;
            _data.LastDay = day;
        }

        int bonusPercent = Math.Min(ForestLimits.StreakBonusMaxPercent, (_data.Streak - 1) * ForestLimits.StreakBonusStepPercent);
        return minutes + (int)((long)minutes * bonusPercent / 100);
    }

    private async ValueTask<bool> FinishAsync(bool changed, CancellationToken cancellationToken)
    {
        if (changed)
        {
            await PersistAsync(cancellationToken);
            Changed?.Invoke();
        }

        return changed;
    }

    private async ValueTask PersistAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            string json;
            lock (_gate)
            {
                if (_readOnly)
                {
                    return;
                }

                json = JsonSerializer.Serialize(_data, JsonOptions);
            }

            await _storage.WriteAsync(DataKey, json, cancellationToken);
        }
        catch (IOException ex)
        {
            _logger.Warn($"Forest data could not be saved: {ex.Message}");
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
            _logger.Warn($"A copy of the unreadable forest data could not be kept: {ex.Message}");
        }
    }

    private static int ClampSeconds(TimeSpan span) => (int)Math.Clamp(span.TotalSeconds, 0, ForestLimits.MaxMinutes * 60);

    private static ForestData Fresh() => new() { Unlocked = [.. FreeSpecies()] };

    private static IEnumerable<string> FreeSpecies() =>
        ForestCatalog.All.Where(s => s.Kind != ForestKind.Decoration && s.Price == 0).Select(s => s.Id);

    // Loaded data is untrusted: drop or repair anything that violates the invariants the rest of the code relies on.
    private static ForestData Sanitize(ForestData? loaded)
    {
        if (loaded is null)
        {
            return Fresh();
        }

        int size = loaded.Size is >= ForestLimits.StartSize and <= ForestLimits.MaxSize ? loaded.Size - ((loaded.Size - ForestLimits.StartSize) % ForestLimits.GrowStep) : ForestLimits.StartSize;
        HashSet<string> unlocked = [.. FreeSpecies()];
        foreach (string id in loaded.Unlocked ?? [])
        {
            if (id is not null && ForestCatalog.Find(id) is { Kind: not ForestKind.Decoration })
            {
                unlocked.Add(id);
            }
        }

        Dictionary<string, int> inventory = [];
        foreach ((string id, int count) in loaded.Inventory ?? [])
        {
            if (id is not null && ForestCatalog.Find(id) is { Kind: ForestKind.Decoration } && count > 0)
            {
                inventory[id] = Math.Min(count, ForestLimits.MaxInventoryPerItem);
            }
        }

        PendingSession? pending = loaded.Pending is { Id.Length: > 0, Planned: >= 0, Start: > 0 } p ? p : null;
        List<ForestObject> objects = [];
        HashSet<int> ids = [];
        HashSet<(int, int)> tiles = [];
        foreach (ForestObject o in loaded.Objects ?? [])
        {
            if (o is not null
                && objects.Count < ForestLimits.MaxObjects
                && o.Id > 0
                && ForestCatalog.Find(o.Species ?? string.Empty) is { } species
                && Enum.IsDefined(o.StageValue)
                && (species.Kind != ForestKind.Decoration || o.StageValue != ForestStage.Withered)
                && (!o.Growing || (species.Kind != ForestKind.Decoration && pending?.PlantId == o.Id))
                && o.Column >= 0 && o.Column < size && o.Row >= 0 && o.Row < size
                && ids.Add(o.Id) && tiles.Add((o.Column, o.Row)))
            {
                o.Minutes = Math.Clamp(o.Minutes, 0, ForestLimits.MaxMinutes);
                objects.Add(o);
            }
        }

        if (pending is not null && !objects.Any(o => o.Growing && o.Id == pending.PlantId))
        {
            pending.PlantId = 0;
        }

        int lastDay = Math.Max(0, loaded.LastDay);
        string chosen = loaded.Chosen is { } c && unlocked.Contains(c) ? c : ForestLimits.DefaultSpecies;
        return new ForestData
        {
            Coins = Math.Clamp(loaded.Coins, 0, ForestLimits.MaxCoins),
            Unlocked = [.. unlocked],
            Chosen = chosen,
            NextId = Math.Max(Math.Max(loaded.NextId, 1), objects.Select(o => o.Id).DefaultIfEmpty(0).Max() + 1),
            Size = size,
            Inventory = inventory,
            Objects = objects,
            Pending = pending,
            LastDay = lastDay,
            Streak = lastDay > 0 ? Math.Clamp(loaded.Streak, 1, 100_000) : 0,
        };
    }
}
