namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// Converts NT device paths (<c>\Device\HarddiskVolume3\Windows\x.exe</c>) to DOS paths (<c>C:\Windows\x.exe</c>).
/// Free of P/Invoke: the device table is produced by an injected query (QueryDosDevice in production), so the logic is unit-testable anywhere.
/// </summary>
internal sealed class NtPathConverter
{
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly Func<string, string?> _queryDosDevice;
    private readonly Func<long> _tickCount;
    private IReadOnlyDictionary<string, string> _table;
    private long _lastRefresh;

    /// <param name="queryDosDevice">Maps a drive name such as <c>C:</c> to its NT target (<c>\Device\HarddiskVolume3</c>) or null.</param>
    /// <param name="tickCount">Monotonic milliseconds; injectable for tests.</param>
    public NtPathConverter(Func<string, string?> queryDosDevice, Func<long>? tickCount = null)
    {
        _queryDosDevice = queryDosDevice;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _table = BuildTable(queryDosDevice);
        _lastRefresh = _tickCount();
    }

    /// <summary>Builds the device-prefix to drive-letter table for A: to Z:. SUBST targets (<c>\??\</c>) are ignored; the first letter wins for duplicate devices.</summary>
    public static IReadOnlyDictionary<string, string> BuildTable(Func<string, string?> queryDosDevice)
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            var target = queryDosDevice(drive);
            if (!string.IsNullOrEmpty(target) && !target.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                table.TryAdd(target, drive);
            }
        }

        return table;
    }

    /// <summary>
    /// Maps one NT path using <paramref name="table"/> (longest device prefix wins, matched on a path-segment boundary).
    /// Handles <c>\??\</c> prefixes and <c>\Device\Mup\</c> (UNC). Already-DOS paths are returned unchanged; unknown devices give null.
    /// </summary>
    public static string? Map(string ntPath, IReadOnlyDictionary<string, string> table)
    {
        if (string.IsNullOrEmpty(ntPath))
        {
            return null;
        }

        if (ntPath.Length >= 3 && ntPath[1] == ':' && ntPath[2] == '\\')
        {
            return ntPath;
        }

        if (ntPath.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            var rest = ntPath[4..];
            return rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + rest[4..] : Map(rest, table);
        }

        const string Mup = @"\Device\Mup\";
        if (ntPath.StartsWith(Mup, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + ntPath[Mup.Length..];
        }

        string? bestDevice = null;
        foreach (var device in table.Keys)
        {
            if (IsPrefixAtSegment(ntPath, device) && (bestDevice is null || device.Length > bestDevice.Length))
            {
                bestDevice = device;
            }
        }

        return bestDevice is null ? null : table[bestDevice] + ntPath[bestDevice.Length..];
    }

    /// <summary>Converts a path using the cached table; on an unknown <c>\Device\</c> prefix the table is rebuilt (volumes can be mounted at runtime), at most every 5 seconds.</summary>
    public string? Convert(string ntPath)
    {
        var mapped = Map(ntPath, Volatile.Read(ref _table));
        if (mapped is not null || !ntPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return mapped;
        }

        var now = _tickCount();
        if (now - Interlocked.Read(ref _lastRefresh) < MinRefreshInterval.TotalMilliseconds)
        {
            return null;
        }

        Interlocked.Exchange(ref _lastRefresh, now);
        var refreshed = BuildTable(_queryDosDevice);
        Volatile.Write(ref _table, refreshed);
        return Map(ntPath, refreshed);
    }

    private static bool IsPrefixAtSegment(string path, string device) =>
        path.StartsWith(device, StringComparison.OrdinalIgnoreCase) && (path.Length == device.Length || path[device.Length] == '\\');
}
