using System.Text.RegularExpressions;

namespace Blocker.Platform.Windows.Changes;

/// <summary>
/// SDDL for the restricted service DACL. SDDL service right codes: CC query config, DC change config, LC query status,
/// SW enumerate dependents, RP start, WP stop, DT pause/continue, LO interrogate, CR user control, SD delete, RC read control,
/// WD write DAC, WO write owner.
/// </summary>
internal static partial class ServiceSddl
{
    /// <summary>SERVICE_ALL_ACCESS (0xF01FF) in SDDL notation.</summary>
    internal const string FullControl = "CCDCLCSWRPWPDTLOCRSDRCWDWO";

    /// <summary>Query config/status, enumerate dependents, start, interrogate, read control. No stop, delete, pause, config or DAC/owner writes.</summary>
    internal const string QueryAndStart = "CCLCSWRPLORC";

    /// <summary>The ACE that keeps SYSTEM in full control.</summary>
    internal const string SystemAce = "(A;;" + FullControl + ";;;SY)";

    /// <summary>
    /// The standard SCM default DACL (SYSTEM and Administrators full, Interactive and Service users read-only). The installer
    /// creates the services without an explicit security descriptor, so this is what the sweep restores for a service that is
    /// still carrying <see cref="BuildRestricted"/>.
    /// </summary>
    internal const string ScmDefaultDacl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private static readonly string[] AllowedRights = ["CC", "LC", "SW", "RP", "LO", "RC"];

    /// <summary>Administrators, Interactive and Authenticated Users may only query, interrogate and start.</summary>
    public static string BuildRestricted() =>
        "D:" + SystemAce + Ace(QueryAndStart, "BA") + Ace(QueryAndStart, "IU") + Ace(QueryAndStart, "AU");

    /// <summary>
    /// True when the DACL-only SDDL keeps SYSTEM in full control and grants every other trustee nothing beyond
    /// <see cref="QueryAndStart"/> (allow ACEs only). Prevents locking the service manager out or granting stop rights.
    /// </summary>
    public static bool IsSafe(string sddl)
    {
        if (!DaclRegex().IsMatch(sddl) || !sddl.Contains(SystemAce, StringComparison.Ordinal))
        {
            return false;
        }

        MatchCollection aces = AceRegex().Matches(sddl.Replace(SystemAce, string.Empty, StringComparison.Ordinal));
        foreach (Match ace in aces)
        {
            string[] fields = ace.Groups[1].Value.Split(';');
            if (fields.Length != 6 || fields[0] != "A" || fields[2].Length % 2 != 0 || fields[5] == "SY")
            {
                return false;
            }

            for (int i = 0; i < fields[2].Length; i += 2)
            {
                if (!AllowedRights.Contains(fields[2].Substring(i, 2)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// True when a journal may write <paramref name="sddl"/> back as a service's original DACL: it is the DACL recorded as the default for that
    /// service, a DACL that passes <see cref="IsSafe"/>, or one shaped like the SCM default, meaning nobody but SYSTEM and Administrators can
    /// stop, reconfigure or delete the service or change its security. The journal is attacker-reachable state, so this is checked on every restore.
    /// </summary>
    public static bool IsSafeToRestore(string sddl, string? recordedDefault) =>
        (recordedDefault is not null && string.Equals(sddl, recordedDefault, StringComparison.OrdinalIgnoreCase))
        || string.Equals(sddl, ScmDefaultDacl, StringComparison.OrdinalIgnoreCase)
        || IsSafe(sddl)
        || DaclInspector.GrantsNothingDangerousToUnprivileged(sddl, DaclInspector.ServiceControl);

    private static string Ace(string rights, string sid) => $"(A;;{rights};;;{sid})";

    [GeneratedRegex(@"\(([^()]*)\)")]
    private static partial Regex AceRegex();

    [GeneratedRegex(@"^D:(\([^()]*\))*$")]
    private static partial Regex DaclRegex();
}
