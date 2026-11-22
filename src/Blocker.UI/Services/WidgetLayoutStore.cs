using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocker.UI.Services;

/// <summary>One widget the user put on the dashboard.</summary>
/// <param name="Key">Contribution key, <c>pluginId/contributionId</c>.</param>
/// <param name="Cols">Grid columns the widget spans (1 to <see cref="MaxCols"/>).</param>
/// <param name="Rows">Grid rows the widget spans (1 to <see cref="MaxRows"/>).</param>
internal sealed record WidgetPlacement(string Key, int Cols = 1, int Rows = 1)
{
    /// <summary>Most columns a widget may span.</summary>
    public const int MaxCols = 2;

    /// <summary>Most rows a widget may span.</summary>
    public const int MaxRows = 3;

    /// <summary>Limits a column span to the allowed range.</summary>
    /// <param name="cols">Requested columns.</param>
    /// <returns>A value from 1 to <see cref="MaxCols"/>.</returns>
    public static int ClampCols(int cols) => Math.Clamp(cols, 1, MaxCols);

    /// <summary>Limits a row span to the allowed range.</summary>
    /// <param name="rows">Requested rows.</param>
    /// <returns>A value from 1 to <see cref="MaxRows"/>.</returns>
    public static int ClampRows(int rows) => Math.Clamp(rows, 1, MaxRows);
}

/// <summary>Persists which widgets the user placed on the dashboard, and in which order.</summary>
internal interface IWidgetLayoutStore
{
    /// <summary>Reads the saved layout.</summary>
    /// <returns>The placements in display order, or <see langword="null"/> when nothing usable was saved yet.</returns>
    IReadOnlyList<WidgetPlacement>? Load();

    /// <summary>Replaces the saved layout.</summary>
    /// <param name="layout">The placements in display order.</param>
    /// <exception cref="IOException">The layout could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The layout location is not writable.</exception>
    void Save(IReadOnlyList<WidgetPlacement> layout);
}

/// <summary>Stores the widget layout as a JSON file in the user's local application data.</summary>
/// <param name="path">Full path of the JSON file.</param>
internal sealed class WidgetLayoutStore(string path) : IWidgetLayoutStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The size of the first layout format; read for old files only.</summary>
    private enum LegacySize
    {
        Small,
        Wide,
    }

    /// <summary>Saved placement as written by any version: <c>cols</c> and <c>rows</c> now, <c>size</c> before spans existed.</summary>
    private sealed record StoredPlacement(string? Key, int? Cols, int? Rows, LegacySize? Size);

    /// <summary>Gets <c>%LOCALAPPDATA%\WindowsDetox\widgets.json</c>.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsDetox", "widgets.json");

    /// <inheritdoc />
    public IReadOnlyList<WidgetPlacement>? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            List<StoredPlacement?> saved = ReadElements(File.ReadAllBytes(path), out bool skippedInvalid);
            if (skippedInvalid)
            {
                TryKeepBadCopy();
            }

            List<WidgetPlacement> placements = saved
                .Where(p => !string.IsNullOrWhiteSpace(p?.Key))
                .Select(p => new WidgetPlacement(p!.Key!, WidgetPlacement.ClampCols(p.Cols ?? (p.Size == LegacySize.Wide ? 2 : 1)), WidgetPlacement.ClampRows(p.Rows ?? 1)))
                .DistinctBy(p => p.Key, StringComparer.Ordinal)
                .ToList();
            return skippedInvalid && placements.Count == 0 ? null : placements;
        }
        catch (JsonException)
        {
            TryKeepBadCopy();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(IReadOnlyList<WidgetPlacement> layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A unique temp name keeps concurrent writers (two app instances) from sharing one half-written file.
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, layout, Options);
                stream.Flush(flushToDisk: true);   // the data must be on disk before the move, or a power loss can leave an empty file
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // One bad element must not discard the rest of the layout, so elements are read one by one.
    private static List<StoredPlacement?> ReadElements(byte[] json, out bool skippedInvalid)
    {
        skippedInvalid = false;
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The layout file does not contain a list.");
        }

        List<StoredPlacement?> elements = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            try
            {
                elements.Add(element.Deserialize<StoredPlacement>(Options));
            }
            catch (JsonException)
            {
                skippedInvalid = true;
            }
        }

        return elements;
    }

    // The next Save replaces the file, so whatever could not be read is copied aside first. Best effort: failing to keep the
    // copy must not stop the dashboard from starting with defaults.
    private bool TryKeepBadCopy()
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
