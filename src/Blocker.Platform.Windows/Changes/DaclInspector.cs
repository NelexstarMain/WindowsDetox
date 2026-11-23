using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Blocker.Platform.Windows.Changes;

/// <summary>
/// Purely lexical reading of a DACL-only SDDL string, used to refuse a journal entry that would restore a DACL granting powerful
/// rights to an unprivileged trustee. It deliberately understands only the plain <c>(A|D;flags;rights;;;trustee)</c> form: anything
/// else (owner or group sections, a SACL, object or conditional ACEs, unknown right tokens) is "not safe".
/// </summary>
internal static partial class DaclInspector
{
    /// <summary>Generic all, write, execute and maximum-allowed access masks.</summary>
    internal const uint GenericAll = 0x10000000, GenericWrite = 0x40000000, MaximumAllowed = 0x02000000;

    /// <summary>DELETE, WRITE_DAC and WRITE_OWNER.</summary>
    internal const uint Delete = 0x00010000, WriteDac = 0x00040000, WriteOwner = 0x00080000;

    /// <summary>Service rights that reconfigure or stop a service (<c>DC</c>, <c>WP</c>) plus everything that changes the object's security.</summary>
    internal const uint ServiceControl = 0x2 | 0x20 | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite | MaximumAllowed;

    /// <summary>File and directory rights that change content or security: write data, append, write EA, delete child, write attributes and the shared bits.</summary>
    internal const uint FileModify = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite | MaximumAllowed;

    private const string TrustedInstallerPrefix = "S-1-5-80-956008885-";

    private static readonly Dictionary<string, uint> Tokens = new(StringComparer.Ordinal)
    {
        ["GA"] = GenericAll, ["GR"] = 0x80000000, ["GW"] = GenericWrite, ["GX"] = 0x20000000,
        ["RC"] = 0x00020000, ["SD"] = Delete, ["WD"] = WriteDac, ["WO"] = WriteOwner,
        ["FA"] = 0x001F01FF, ["FR"] = 0x00120089, ["FW"] = 0x00120116, ["FX"] = 0x001200A0,
        ["CC"] = 0x1, ["DC"] = 0x2, ["LC"] = 0x4, ["SW"] = 0x8, ["RP"] = 0x10, ["WP"] = 0x20, ["DT"] = 0x40, ["LO"] = 0x80, ["CR"] = 0x100,
    };

    /// <summary>
    /// True when <paramref name="sddl"/> is a plain DACL and no allow ACE gives any bit of <paramref name="dangerousRights"/> to a trustee other
    /// than SYSTEM, Administrators or TrustedInstaller (Creator Owner counts only on inherit-only ACEs, which affect children, not the object).
    /// An ACE that is already present verbatim in <paramref name="baselineSddl"/> (the DACL the object has right now) is not a new grant and is tolerated.
    /// </summary>
    public static bool GrantsNothingDangerousToUnprivileged(string sddl, uint dangerousRights, string? baselineSddl = null)
    {
        if (!TryParse(sddl, out List<Ace>? aces))
        {
            return false;
        }

        HashSet<string> existing = baselineSddl is not null && TryParse(baselineSddl, out List<Ace>? current)
            ? [.. current.Select(static ace => ace.Text)]
            : [];
        foreach (Ace ace in aces)
        {
            if (ace.IsAllow && (ace.Rights & dangerousRights) != 0 && !IsPrivileged(ace) && !existing.Contains(ace.Text))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPrivileged(Ace ace) => ace.Trustee switch
    {
        "SY" or "BA" or "S-1-5-18" or "S-1-5-32-544" => true,
        "CO" or "S-1-3-0" => ace.InheritOnly,
        var sid => sid.StartsWith(TrustedInstallerPrefix, StringComparison.Ordinal),
    };

    private static bool TryParse(string sddl, [NotNullWhen(true)] out List<Ace>? aces)
    {
        aces = null;
        Match dacl = DaclRegex().Match(sddl);
        if (!dacl.Success)
        {
            return false;
        }

        List<Ace> parsed = [];
        foreach (Capture capture in dacl.Groups["ace"].Captures)
        {
            string[] fields = capture.Value.Split(';');
            if (fields.Length != 6 || fields[0] is not ("A" or "D") || fields[3].Length != 0 || fields[4].Length != 0
                || fields[5].Length == 0 || !TryParseRights(fields[2], out uint rights))
            {
                return false;
            }

            parsed.Add(new Ace(fields[0] == "A", HasFlag(fields[1], "IO"), rights, fields[5], capture.Value));
        }

        aces = parsed;
        return true;
    }

    /// <summary>ACE flags are a sequence of two-letter codes; a plain substring test would match across code boundaries ("CIOI").</summary>
    private static bool HasFlag(string flags, string flag)
    {
        for (int i = 0; i + 1 < flags.Length; i += 2)
        {
            if (string.CompareOrdinal(flags, i, flag, 0, 2) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseRights(string text, out uint rights)
    {
        rights = 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rights);
        }

        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out rights);
        }

        if (text.Length % 2 != 0)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i += 2)
        {
            if (!Tokens.TryGetValue(text.Substring(i, 2), out uint bits))
            {
                return false;
            }

            rights |= bits;
        }

        return true;
    }

    private readonly record struct Ace(bool IsAllow, bool InheritOnly, uint Rights, string Trustee, string Text);

    [GeneratedRegex(@"^D:(?:P|AI|AR)*(?:\((?<ace>[^()]*)\))*$")]
    private static partial Regex DaclRegex();
}
