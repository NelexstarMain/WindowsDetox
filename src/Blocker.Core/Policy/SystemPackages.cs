using System.Collections.Frozen;

namespace Blocker.Core.Policy;

/// <summary>
/// Store packages that ship with Windows and are treated like the system baseline. A package family name is assigned by the OS from the signing
/// publisher, so <c>_cw5n1h2txyewy</c> (Microsoft Windows) and <c>_8wekyb3d8bbwe</c> (Microsoft Corporation) cannot be claimed by a sideloaded
/// package without a certificate that Windows trusts for that publisher. Every Windows-publisher package is an OS component; of the Microsoft
/// Corporation packages only the essential tools and runtimes are listed, so games, media players, browsers and the Store stay blockable.
/// </summary>
internal static class SystemPackages
{
    private const string WindowsPublisher = "cw5n1h2txyewy";
    private const string MicrosoftPublisher = "8wekyb3d8bbwe";

    private static readonly FrozenSet<string> MicrosoftNames = new[]
    {
        "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.Windows.Photos",
        "Microsoft.WindowsCamera", "Microsoft.WindowsAlarms", "Microsoft.WindowsSoundRecorder", "Microsoft.MicrosoftStickyNotes",
        "Microsoft.SecHealthUI", "Microsoft.DesktopAppInstaller", "Microsoft.WidgetsPlatformRuntime", "Microsoft.StorePurchaseApp",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Versioned framework packages (for example <c>Microsoft.WindowsAppRuntime.1.6</c>) that host background helpers.</summary>
    private static readonly string[] MicrosoftNamePrefixes = ["Microsoft.WindowsAppRuntime.", "Microsoft.VCLibs.", "Microsoft.UI.Xaml."];

    /// <summary>True when <paramref name="packageFamilyName"/> belongs to a Windows component or an essential inbox Microsoft app.</summary>
    public static bool IsSystemPackage(string? packageFamilyName)
    {
        int separator = packageFamilyName?.LastIndexOf('_') ?? -1;
        if (separator <= 0)
        {
            return false;
        }

        string name = packageFamilyName![..separator];
        string publisher = packageFamilyName[(separator + 1)..];
        if (publisher.Equals(WindowsPublisher, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return publisher.Equals(MicrosoftPublisher, StringComparison.OrdinalIgnoreCase)
            && (MicrosoftNames.Contains(name) || MicrosoftNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }
}
