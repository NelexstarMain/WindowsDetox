namespace Blocker.Core.WebFilter;

/// <summary>Decides whether a host may be reached through the proxy.</summary>
public interface IWebFilterPolicy
{
    /// <summary>True when <paramref name="host"/> (normalized: lower-case, punycode, no trailing dot) is allowed.</summary>
    bool IsAllowed(string host);
}

/// <summary>Adapts a delegate (for example <c>DomainMatcher.IsAllowed</c>) to <see cref="IWebFilterPolicy"/>.</summary>
public sealed class FuncWebFilterPolicy(Func<string, bool> isAllowed) : IWebFilterPolicy
{
    /// <inheritdoc/>
    public bool IsAllowed(string host) => isAllowed(host);
}
