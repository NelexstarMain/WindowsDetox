using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Blocker.Core.Policy;

/// <summary>
/// A normalized domain rule: lower-case, IDN converted to punycode, one trailing dot removed.
/// <c>example.com</c> matches only that host. <c>*.example.com</c> matches every subdomain at any depth <b>and the apex</b> <c>example.com</c>.
/// IP literals, spaces, empty labels, names above 253 characters and wildcards over a single label (<c>*.com</c>) are rejected.
/// </summary>
public sealed record DomainPattern
{
    private DomainPattern(string host, bool includesSubdomains)
    {
        Host = host;
        IncludesSubdomains = includesSubdomains;
    }

    /// <summary>Normalized host (the part after <c>*.</c> for wildcards).</summary>
    public string Host { get; }

    /// <summary>True for <c>*.</c> patterns.</summary>
    public bool IncludesSubdomains { get; }

    /// <summary>Parses <paramref name="text"/>; false when invalid.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out DomainPattern? pattern)
    {
        pattern = null;
        bool wildcard = text is not null && text.StartsWith("*.", StringComparison.Ordinal);
        if (!DomainName.TryNormalize(wildcard ? text![2..] : text, out string? host) || (wildcard && !host.Contains('.', StringComparison.Ordinal)))
        {
            return false;
        }

        pattern = new DomainPattern(host, wildcard);
        return true;
    }

    /// <summary>Parses <paramref name="text"/>.</summary>
    /// <exception cref="FormatException">The text is not a valid pattern.</exception>
    public static DomainPattern Parse(string text) =>
        TryParse(text, out DomainPattern? pattern) ? pattern : throw new FormatException($"Invalid domain pattern '{text}'.");

    /// <inheritdoc/>
    public override string ToString() => IncludesSubdomains ? "*." + Host : Host;
}

/// <summary>Allocation-light domain allowlist lookup: exact hash lookup, then a suffix walk over the labels (O(labels)).</summary>
public sealed class DomainMatcher
{
    private readonly FrozenSet<string> _exact;
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _wildcards;

    /// <summary>Creates the matcher.</summary>
    public DomainMatcher(IEnumerable<DomainPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);
        foreach (DomainPattern pattern in patterns)
        {
            (pattern.IncludesSubdomains ? wildcards : exact).Add(pattern.Host);
        }

        _exact = exact.ToFrozenSet(StringComparer.Ordinal);
        _wildcards = wildcards.ToFrozenSet(StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>True when <paramref name="host"/> (any case, IDN or punycode, optional trailing dot) is allowed. IPs and invalid names are never allowed.</summary>
    public bool IsAllowed(string host)
    {
        if (!DomainName.TryNormalize(host, out string? name))
        {
            return false;
        }

        if (_exact.Contains(name))
        {
            return true;
        }

        ReadOnlySpan<char> suffix = name;
        while (true)
        {
            if (_wildcards.Contains(suffix))
            {
                return true;
            }

            int dot = suffix.IndexOf('.');
            if (dot < 0)
            {
                return false;
            }

            suffix = suffix[(dot + 1)..];
        }
    }
}
