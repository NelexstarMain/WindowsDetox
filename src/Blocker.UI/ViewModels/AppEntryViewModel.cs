using Blocker.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blocker.UI.ViewModels;

/// <summary>An installed application in the allowlist picker.</summary>
internal sealed partial class AppEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Creates a row from a scan result.</summary>
    /// <param name="app">Scanned application.</param>
    public AppEntryViewModel(InstalledAppDto app)
    {
        ArgumentNullException.ThrowIfNull(app);
        DisplayName = app.DisplayName;
        ExecutablePath = app.ExecutablePath;
        Sha256 = app.Sha256;
        PackageFamilyName = app.PackageFamilyName;
        IconPng = DecodeIcon(app.IconPngBase64);
        Badges = DescribeSources(app.Sources);
    }

    /// <summary>Creates a row for a stored allowlist entry that the scan did not find (for example a portable app).</summary>
    /// <param name="entry">Stored entry.</param>
    public AppEntryViewModel(AllowlistEntryDto entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DisplayName = entry.DisplayName;
        ExecutablePath = entry.ExecutablePath;
        Sha256 = entry.Sha256;
        PackageFamilyName = entry.PackageFamilyName;
        Badges = ["Saved"];
    }

    /// <summary>Gets the name shown to the user.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the executable path, if any.</summary>
    public string? ExecutablePath { get; }

    /// <summary>Gets the lower-case hex SHA-256 of the executable, if known.</summary>
    public string? Sha256 { get; }

    /// <summary>Gets the Store package family name, if any.</summary>
    public string? PackageFamilyName { get; }

    /// <summary>Gets the PNG bytes of the icon, if any.</summary>
    public byte[]? IconPng { get; }

    /// <summary>Gets where the application was found, as short badge texts.</summary>
    public IReadOnlyList<string> Badges { get; }

    /// <summary>Gets the path to show under the name; Store apps show their package family.</summary>
    public string Subtitle => ExecutablePath ?? PackageFamilyName ?? string.Empty;

    /// <summary>Converts the row to its wire form.</summary>
    /// <returns>The allowlist entry.</returns>
    public AllowlistEntryDto ToEntry() => new(DisplayName, ExecutablePath, Sha256, PackageFamilyName);

    /// <summary>Returns whether this row stands for <paramref name="entry"/> (hash, package family or path, in that order of trust).</summary>
    /// <param name="entry">Stored entry.</param>
    /// <returns><see langword="true"/> when they identify the same application.</returns>
    public bool Matches(AllowlistEntryDto entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Sha256 is { Length: > 0 } hash && Sha256 is { Length: > 0 } ownHash)
        {
            return string.Equals(hash, ownHash, StringComparison.OrdinalIgnoreCase);
        }

        if (entry.PackageFamilyName is { Length: > 0 } family && PackageFamilyName is { Length: > 0 } ownFamily)
        {
            return string.Equals(family, ownFamily, StringComparison.OrdinalIgnoreCase);
        }

        return entry.ExecutablePath is { Length: > 0 } path
            && ExecutablePath is { Length: > 0 } ownPath
            && string.Equals(path, ownPath, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? DecodeIcon(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        byte[] buffer = new byte[base64.Length / 4 * 3 + 3];
        return Convert.TryFromBase64String(base64, buffer, out int written) ? buffer.AsSpan(0, written).ToArray() : null;
    }

    private static string[] DescribeSources(AppSourceDto sources)
    {
        List<string> badges = [];
        if (sources.HasFlag(AppSourceDto.StartMenu))
        {
            badges.Add("Start menu");
        }

        if (sources.HasFlag(AppSourceDto.Registry))
        {
            badges.Add("Installed");
        }

        if (sources.HasFlag(AppSourceDto.StorePackage))
        {
            badges.Add("Store");
        }

        if (sources.HasFlag(AppSourceDto.Running))
        {
            badges.Add("Running");
        }

        return [.. badges];
    }
}
