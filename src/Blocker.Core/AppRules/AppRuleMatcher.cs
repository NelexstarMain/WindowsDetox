using Blocker.Contracts;
using Blocker.Core.Policy;
using Blocker.Core.ServiceHost;

namespace Blocker.Core.AppRules;

/// <summary>
/// Finds the rules a process belongs to, with the semantics of <see cref="Allowlist.Match"/> (hash first, path only for entries without a hash,
/// then package family name) because each rule's identity is held in an <see cref="Allowlist"/> of its own. Immutable.
/// </summary>
internal sealed class AppRuleMatcher
{
    private readonly (string Key, Allowlist Identity)[] _rules;

    /// <summary>Creates a matcher over validated rules.</summary>
    /// <param name="rules">The rules to match against.</param>
    public AppRuleMatcher(IEnumerable<AppRuleDto> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        AppRuleDto[] list = [.. rules];
        _rules = [.. list.Select(static r => (AppRuleIdentity.LedgerKey(r.App), new Allowlist([AllowlistMapping.ToEntry(r.App)])))];
        NeedsHash = list.Any(static r => !string.IsNullOrWhiteSpace(r.App.Sha256));
        NeedsPackage = list.Any(static r => !string.IsNullOrWhiteSpace(r.App.PackageFamilyName));
    }

    /// <summary>Gets whether any rule is identified by a hash, so matching a process needs its SHA-256.</summary>
    public bool NeedsHash { get; }

    /// <summary>Gets whether any rule is identified by a package family name.</summary>
    public bool NeedsPackage { get; }

    /// <summary>Returns the ledger keys of every rule the process belongs to, in rule order.</summary>
    /// <param name="imagePath">Image path of the process.</param>
    /// <param name="sha256">Hex SHA-256 of the image, or <see langword="null"/> when unknown (hash rules then do not match).</param>
    /// <param name="packageFamilyName">Package family name, or <see langword="null"/>.</param>
    /// <returns>The keys; empty when no rule matches.</returns>
    public IReadOnlyList<string> Match(string imagePath, string? sha256, string? packageFamilyName) =>
        [.. _rules.Where(r => r.Identity.Match(imagePath, sha256, packageFamilyName) is not null).Select(static r => r.Key)];
}
