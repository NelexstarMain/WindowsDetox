using Blocker.Contracts;
using Blocker.Core.Policy;

namespace Blocker.Core.AppRules;

/// <summary>
/// Which identities an app rule answers to, read exactly like <see cref="Allowlist"/> reads an entry: a hash alone decides (the path of an entry
/// with a hash does not count), otherwise the normalized path, and a package family name in addition to either.
/// </summary>
internal static class AppRuleIdentity
{
    /// <summary>Returns the identity keys of a valid entry, strongest first (hash, or path when there is no hash; then package).</summary>
    public static IReadOnlyList<string> Keys(AllowlistEntryDto app)
    {
        ArgumentNullException.ThrowIfNull(app);
        List<string> keys = [];
        if (app.Sha256?.Trim() is { Length: > 0 } hash)
        {
            keys.Add("sha256:" + hash.ToLowerInvariant());
        }
        else if (WinPath.Normalize(app.ExecutablePath) is { Length: > 0 } path)
        {
            keys.Add("path:" + path);
        }

        if (app.PackageFamilyName?.Trim() is { Length: > 0 } package)
        {
            keys.Add("pfn:" + package.ToLowerInvariant());
        }

        return keys;
    }

    /// <summary>The key usage is recorded under: the strongest identity of the entry.</summary>
    public static string LedgerKey(AllowlistEntryDto app) =>
        Keys(app) is [{ } first, ..] ? first : throw new ArgumentException("The entry has no identity.", nameof(app));

    /// <summary>True when the two entries share at least one identity, which makes them the same app for rules while nothing is blocked.</summary>
    public static bool Overlap(AllowlistEntryDto a, AllowlistEntryDto b) => Keys(a).Intersect(Keys(b), StringComparer.Ordinal).Any();

    /// <summary>
    /// True when <paramref name="wider"/> answers to every identity of <paramref name="narrower"/> (and possibly more), so it matches every process
    /// the narrower entry matched. Sharing one key is not enough: a chain of such steps could walk a rule to an identity nothing runs under.
    /// </summary>
    public static bool Covers(AllowlistEntryDto wider, AllowlistEntryDto narrower) => !Keys(narrower).Except(Keys(wider), StringComparer.Ordinal).Any();
}
