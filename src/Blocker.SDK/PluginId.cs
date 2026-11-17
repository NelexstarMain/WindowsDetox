using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Blocker.Sdk;

/// <summary>
/// Validated plugin identifier in reverse-DNS form, for example <c>com.example.focus-counter</c>.
/// </summary>
/// <remarks>
/// Rules: 2 to 5 dot-separated segments; each segment starts with a lowercase ASCII letter and contains only
/// lowercase letters, digits and hyphens; total length at most 128 characters. Ids are used as directory
/// names and IPC namespaces, so the rules are deliberately strict.
/// </remarks>
public readonly partial record struct PluginId
{
    private const int MaxLength = 128;

    /// <summary>Creates a plugin id, validating <paramref name="value"/>.</summary>
    /// <param name="value">Raw identifier.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid plugin id.</exception>
    public PluginId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"'{value}' is not a valid plugin id (expected e.g. 'com.example.my-plugin').", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the identifier text.</summary>
    public string Value { get; }

    /// <summary>Returns whether <paramref name="value"/> is a syntactically valid plugin id.</summary>
    /// <param name="value">Candidate identifier.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsValid([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= MaxLength } && IdPattern().IsMatch(value);

    /// <summary>Attempts to create a plugin id without throwing.</summary>
    /// <param name="value">Candidate identifier.</param>
    /// <param name="id">The created id when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryCreate(string? value, out PluginId id)
    {
        if (IsValid(value))
        {
            id = new PluginId(value);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => Value ?? string.Empty;

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*){1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
