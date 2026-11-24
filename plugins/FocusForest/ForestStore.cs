// Game state of the Focus Forest: coins, unlocked species, inventory and the objects standing on the growing field.
//
// A completed session earns coins and plants one tree or bush; a session ended with break-glass plants a withered one.
// The state is small and bounded (at most 16 x 16 objects), kept in memory and written through to plugin storage after
// every change. Selection state (which object is being moved, which decoration is being placed) is never persisted.

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

    /// <summary>Hard cap of stored objects (a full maximum field).</summary>
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
                _placingDecoration);
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
    }

    /// <summary>
    /// Applies a finished session: running to its planned end earns one coin per minute and plants the chosen species at the
    /// stage of its length, break-glass plants a withered one, anything else changes nothing. Ends without a matching start
    /// (plugin installed mid-session) are ignored.
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
            if (ended.Reason is SessionEndReason.Expired or SessionEndReason.BreakGlass)
            {
                int minutes = Math.Clamp(ClampSeconds(ended.OccurredAt - DateTimeOffset.FromUnixTimeSeconds(pending.Start)) / 60, 0, ForestLimits.MaxMinutes);
                bool completed = ended.Reason == SessionEndReason.Expired;
                if (completed)
                {
                    _data.Coins = (int)Math.Min((long)_data.Coins + minutes, ForestLimits.MaxCoins);
                }

                if (!AddAutoPlaced(_data.Chosen, completed ? ForestCatalog.StageFor(minutes) : ForestStage.Withered, minutes, pending.Start))
                {
                    _logger.Warn("The forest is full; this session's plant was not placed.");
                }
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
            _selectedObject = null;
            _placingDecoration = null;
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

    /// <summary>Handles a click on an empty tile: places the pending decoration or moves the selected object there.</summary>
    public async ValueTask<bool> PickAsync(int column, int row, CancellationToken cancellationToken)
    {
        bool done = false;
        lock (_gate)
        {
            if (IsFree(column, row))
            {
                if (_placingDecoration is { } decoration && _data.Inventory.GetValueOrDefault(decoration) > 0)
                {
                    done = AddObject(decoration, ForestStage.Mature, 0, Time.GetUtcNow().ToUnixTimeSeconds(), column, row);
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
                    moving.Column = column;
                    moving.Row = row;
                    _selectedObject = null;
                    done = true;
                }
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

    private async ValueTask<bool> FinishAsync(bool changed, CancellationToken cancellationToken)
    {
        if (changed)
        {
            await PersistAsync(cancellationToken);
            Changed?.Invoke();
        }

        return changed;
    }

    // Callers hold _gate.
    private bool IsFree(int column, int row) =>
        column >= 0 && row >= 0 && column < _data.Size && row < _data.Size && !_data.Objects.Any(o => o.Column == column && o.Row == row);

    // Callers hold _gate. Puts the object on the free tile closest to the centre (ties: top row first), growing the field when needed.
    private bool AddAutoPlaced(string speciesId, ForestStage stage, int minutes, long planted)
    {
        while (_data.Objects.Count >= _data.Size * _data.Size && _data.Size < ForestLimits.MaxSize)
        {
            Grow();
        }

        foreach ((int column, int row) in TilesFromCentre(_data.Size))
        {
            if (IsFree(column, row))
            {
                return AddObject(speciesId, stage, minutes, planted, column, row);
            }
        }

        return false;
    }

    // Callers hold _gate and checked that the tile is free.
    private bool AddObject(string speciesId, ForestStage stage, int minutes, long planted, int column, int row)
    {
        if (_data.Objects.Count >= ForestLimits.MaxObjects)
        {
            return false;
        }

        _data.Objects.Add(new ForestObject
        {
            Id = _data.NextId++,
            Species = speciesId,
            Stage = (int)stage,
            Column = column,
            Row = row,
            Planted = planted,
            Minutes = minutes,
        });
        if (_data.Objects.Count * 4 >= _data.Size * _data.Size * 3 && _data.Size < ForestLimits.MaxSize)
        {
            Grow();
        }

        return true;
    }

    // Callers hold _gate. Growing by one tile on each side keeps the objects where they are relative to the centre.
    private void Grow()
    {
        _data.Size = Math.Min(ForestLimits.MaxSize, _data.Size + ForestLimits.GrowStep);
        foreach (ForestObject o in _data.Objects)
        {
            o.Column++;
            o.Row++;
        }
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
                && o.Column >= 0 && o.Column < size && o.Row >= 0 && o.Row < size
                && ids.Add(o.Id) && tiles.Add((o.Column, o.Row)))
            {
                o.Minutes = Math.Clamp(o.Minutes, 0, ForestLimits.MaxMinutes);
                objects.Add(o);
            }
        }

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
            Pending = loaded.Pending is { Id.Length: > 0, Planned: >= 0, Start: > 0 } p ? p : null,
        };
    }
}
