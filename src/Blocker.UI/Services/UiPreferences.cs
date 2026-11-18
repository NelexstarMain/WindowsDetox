using System.Windows;

namespace Blocker.UI.Services;

/// <summary>How much visual effect work the UI does. Applies to the metaball layer and animations.</summary>
internal enum EffectsQuality
{
    /// <summary>Full-resolution effects.</summary>
    High,

    /// <summary>Reduced resolution and frame rate.</summary>
    Low,

    /// <summary>No animated effects.</summary>
    Off,
}

/// <summary>Per-user UI preferences stored next to the application data of the signed-in user.</summary>
internal interface IUiPreferences
{
    /// <summary>Gets or sets the effects quality; setting it persists and applies it immediately.</summary>
    EffectsQuality Effects { get; set; }
}

/// <summary>
/// Stores the effects quality in <c>%LocalAppData%\WindowsDetox\effects.txt</c> and publishes it to the application resources
/// as <see cref="ResourceKey"/> so the controls library can read it.
/// </summary>
internal sealed class UiPreferences : IUiPreferences
{
    /// <summary>Application resource key that holds the current <see cref="EffectsQuality"/> name.</summary>
    public const string ResourceKey = "Blocker.Effects.Quality";

    private readonly string _path;
    private EffectsQuality _effects;

    /// <summary>Loads the stored preference, falling back to <see cref="EffectsQuality.High"/>.</summary>
    /// <param name="path">File to use; the per-user default when <see langword="null"/>.</param>
    public UiPreferences(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsDetox", "effects.txt");
        _effects = Load(_path);
        Publish();
    }

    /// <inheritdoc />
    public EffectsQuality Effects
    {
        get => _effects;
        set
        {
            if (_effects == value)
            {
                return;
            }

            _effects = value;
            Publish();
            Save();
        }
    }

    private static EffectsQuality Load(string path)
    {
        if (!File.Exists(path))
        {
            return EffectsQuality.High;
        }

        try
        {
            return Enum.TryParse(File.ReadAllText(path).Trim(), ignoreCase: true, out EffectsQuality value) && Enum.IsDefined(value) ? value : EffectsQuality.High;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EffectsQuality.High;
        }
    }

    private void Publish()
    {
        if (Application.Current is { } app)
        {
            app.Resources[ResourceKey] = _effects.ToString();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, _effects.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The preference still applies for this run; it simply is not remembered. Nothing else depends on it.
            LastSaveFailed = true;
        }
    }

    /// <summary>Gets whether the last attempt to remember the preference failed.</summary>
    public bool LastSaveFailed { get; private set; }
}
