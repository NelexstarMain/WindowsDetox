using System.Buffers;
using System.Collections.Frozen;

namespace Blocker.Core.Policy;

/// <summary>A user-approved application. At least one of the three identifiers must be set.</summary>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="ExecutablePath">Full path; counts as a match only when the entry has no hash (with a hash, the hash alone decides).</param>
/// <param name="Sha256">Hex SHA-256 of the executable (any case).</param>
/// <param name="PackageFamilyName">Store package family name.</param>
public sealed record AllowlistEntry(string DisplayName, string? ExecutablePath = null, string? Sha256 = null, string? PackageFamilyName = null);

/// <summary>Immutable process and domain allowlist for one session.</summary>
public sealed class Allowlist
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    private readonly FrozenDictionary<string, AllowlistEntry> _byHash;
    private readonly FrozenDictionary<string, AllowlistEntry> _byPath;
    private readonly FrozenDictionary<string, AllowlistEntry> _byPackage;

    /// <summary>Creates the allowlist.</summary>
    /// <exception cref="ArgumentException">An entry has no identifier or a malformed SHA-256.</exception>
    public Allowlist(IEnumerable<AllowlistEntry> entries, IEnumerable<DomainPattern>? domains = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var byHash = new Dictionary<string, AllowlistEntry>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, AllowlistEntry>(StringComparer.Ordinal);
        var byPackage = new Dictionary<string, AllowlistEntry>(StringComparer.OrdinalIgnoreCase);
        var all = new List<AllowlistEntry>();
        foreach (AllowlistEntry entry in entries)
        {
            string path = WinPath.Normalize(entry.ExecutablePath);
            string? hash = entry.Sha256?.Trim();
            string? package = entry.PackageFamilyName?.Trim();
            if (path.Length == 0 && string.IsNullOrEmpty(hash) && string.IsNullOrEmpty(package))
            {
                throw new ArgumentException($"Allowlist entry '{entry.DisplayName}' has no path, hash or package family name.", nameof(entries));
            }

            if (!string.IsNullOrEmpty(hash))
            {
                if (hash.Length != 64 || hash.AsSpan().ContainsAnyExcept(HexDigits))
                {
                    throw new ArgumentException($"Allowlist entry '{entry.DisplayName}' has a malformed SHA-256.", nameof(entries));
                }

                byHash[hash] = entry;
            }
            else if (path.Length > 0)
            {
                byPath[path] = entry;
            }

            if (!string.IsNullOrEmpty(package))
            {
                byPackage[package] = entry;
            }

            all.Add(entry);
        }

        _byHash = byHash.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _byPath = byPath.ToFrozenDictionary(StringComparer.Ordinal);
        _byPackage = byPackage.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Entries = all;
        Domains = new DomainMatcher(domains ?? []);
    }

    /// <summary>An allowlist that allows nothing.</summary>
    public static Allowlist Empty { get; } = new([]);

    /// <summary>The configured entries.</summary>
    public IReadOnlyList<AllowlistEntry> Entries { get; }

    /// <summary>Allowed web domains.</summary>
    public DomainMatcher Domains { get; }

    /// <summary>
    /// Matches a process: hash first, then path (only for entries without a hash, so a replaced binary at an allowed path is rejected), then package.
    /// </summary>
    /// <returns>The rule that matched, or <see langword="null"/>.</returns>
    public VerdictReason? Match(string imagePath, string? sha256, string? packageFamilyName)
    {
        if (sha256 is not null && _byHash.ContainsKey(sha256))
        {
            return VerdictReason.AllowlistHash;
        }

        if (_byPath.Count > 0 && _byPath.ContainsKey(WinPath.Normalize(imagePath)))
        {
            return VerdictReason.AllowlistPath;
        }

        return packageFamilyName is not null && _byPackage.ContainsKey(packageFamilyName) ? VerdictReason.AllowlistPackage : null;
    }
}
