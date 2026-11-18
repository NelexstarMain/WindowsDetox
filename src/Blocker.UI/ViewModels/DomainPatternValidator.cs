using Blocker.Core.Policy;

namespace Blocker.UI.ViewModels;

/// <summary>Outcome of validating a domain pattern typed by the user.</summary>
/// <param name="IsValid">Whether the pattern can be added.</param>
/// <param name="Normalized">Normalized pattern (lower-case, punycode) when valid; otherwise the trimmed input.</param>
/// <param name="Message">Hint or error text shown under the input; <see langword="null"/> for empty input.</param>
internal sealed record DomainValidation(bool IsValid, string Normalized, string? Message);

/// <summary>Validates domain patterns and explains problems in plain words. The service's own parser is the final judge.</summary>
internal static class DomainPatternValidator
{
    /// <summary>Text shown while the input is empty.</summary>
    public const string ExampleHint = "Use example.com for one site or *.example.com for the site and all its subdomains.";

    /// <summary>Validates <paramref name="input"/>.</summary>
    /// <param name="input">Raw text from the input box.</param>
    /// <returns>The validation result with a user-readable message.</returns>
    public static DomainValidation Validate(string? input)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new DomainValidation(false, text, ExampleHint);
        }

        string? problem = text switch
        {
            _ when text.Contains("://", StringComparison.Ordinal) => "Enter only the domain, without http:// or https://.",
            _ when text.Contains('/') || text.Contains('?') || text.Contains('#') => "Enter only the domain, without a path.",
            _ when text.Contains('@') => "Enter only the domain, without a user name.",
            _ when text.Contains(':') => "Remove the port number; the rule covers every port.",
            _ when text.Any(char.IsWhiteSpace) => "A domain cannot contain spaces.",
            _ when text.Contains('*') && !text.StartsWith("*.", StringComparison.Ordinal) => "A wildcard can only be at the start, as in *.example.com.",
            _ when text.LastIndexOf('*') > 0 => "Use a single wildcard at the start, as in *.example.com.",
            _ when text.Contains("..", StringComparison.Ordinal) => "A domain cannot contain empty parts between dots.",
            _ => null,
        };
        if (problem is not null)
        {
            return new DomainValidation(false, text, problem);
        }

        if (!DomainPattern.TryParse(text, out DomainPattern? pattern))
        {
            return new DomainValidation(false, text, text.StartsWith("*.", StringComparison.Ordinal)
                ? "Add at least a name and an ending after the wildcard, as in *.example.com."
                : "That is not a valid domain. Use letters, digits and hyphens, like example.com. IP addresses are not supported.");
        }

        string normalized = pattern.ToString();
        string note = pattern.IncludesSubdomains
            ? $"Allows {pattern.Host} and every subdomain."
            : $"Allows only {pattern.Host}, not its subdomains.";
        return new DomainValidation(true, normalized, note);
    }
}
