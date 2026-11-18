using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocker.UI.Services;

/// <summary>How many dashboard columns a widget spans.</summary>
internal enum WidgetSize
{
    /// <summary>One column.</summary>
    Small,

    /// <summary>Two columns.</summary>
    Wide,
}

/// <summary>One widget the user put on the dashboard.</summary>
/// <param name="Key">Contribution key, <c>pluginId/contributionId</c>.</param>
/// <param name="Size">Columns the widget spans.</param>
internal sealed record WidgetPlacement(string Key, WidgetSize Size = WidgetSize.Small);

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

            using FileStream stream = File.OpenRead(path);
            List<WidgetPlacement>? saved = JsonSerializer.Deserialize<List<WidgetPlacement>>(stream, Options);
            return saved?.Where(p => !string.IsNullOrWhiteSpace(p?.Key)).DistinctBy(p => p.Key, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(IReadOnlyList<WidgetPlacement> layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(layout, Options));
        File.Move(temp, path, overwrite: true);
    }
}
