using Blocker.UI.Controls.Metaballs;

namespace Blocker.UI.Services;

/// <summary>Per-user UI preferences stored next to the application data of the signed-in user.</summary>
internal interface IUiPreferences
{
    /// <summary>Raised after <see cref="Effects"/> changed.</summary>
    event EventHandler? Changed;

    /// <summary>Gets or sets the effects quality; setting it persists it and raises <see cref="Changed"/>.</summary>
    MetaballQuality Effects { get; set; }
}

/// <summary>
/// Stores the effects quality in <c>%LocalAppData%\WindowsDetox\effects.txt</c>; the main window applies it to its metaball host.
/// </summary>
internal sealed class UiPreferences : IUiPreferences
{
    private readonly string _path;
    private MetaballQuality _effects;

    /// <summary>Loads the stored preference, falling back to <see cref="MetaballQuality.High"/>.</summary>
    /// <param name="path">File to use; the per-user default when <see langword="null"/>.</param>
    public UiPreferences(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsDetox", "effects.txt");
        _effects = Load(_path);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public MetaballQuality Effects
    {
        get => _effects;
        set
        {
            if (_effects == value)
            {
                return;
            }

            _effects = value;
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static MetaballQuality Load(string path)
    {
        if (!File.Exists(path))
        {
            return MetaballQuality.High;
        }

        try
        {
            return Enum.TryParse(File.ReadAllText(path).Trim(), ignoreCase: true, out MetaballQuality value) && Enum.IsDefined(value) ? value : MetaballQuality.High;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MetaballQuality.High;
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
