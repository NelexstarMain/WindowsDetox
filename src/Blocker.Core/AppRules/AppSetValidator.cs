using Blocker.Contracts;
using Blocker.Core.ServiceHost;

namespace Blocker.Core.AppRules;

/// <summary>Result of <see cref="AppSetValidator.Validate"/>.</summary>
/// <param name="Sets">The sets with trimmed names; <see langword="null"/> when there are errors.</param>
/// <param name="Errors">User-readable errors; empty when valid.</param>
public sealed record AppSetsValidation(AppSetsDto? Sets, IReadOnlyList<string> Errors)
{
    /// <summary>Gets whether the sets passed validation.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates the saved app sets. Sets are plain storage for the session setup; they never take part in enforcement.</summary>
public static class AppSetValidator
{
    /// <summary>Largest number of sets.</summary>
    public const int MaxSets = 50;

    /// <summary>Longest name, after trimming.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Largest number of errors returned.</summary>
    public const int MaxErrors = 20;

    /// <summary>Checks the sets and returns them with trimmed names, in the order sent.</summary>
    /// <param name="sets">The sets as sent.</param>
    /// <returns>The normalized sets or the errors.</returns>
    public static AppSetsValidation Validate(AppSetsDto sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Sets is null)
        {
            return new AppSetsValidation(null, ["The sets are missing."]);
        }

        if (sets.Sets.Count > MaxSets)
        {
            return new AppSetsValidation(null, [$"At most {MaxSets} app sets are allowed."]);
        }

        List<string> errors = [];
        List<AppSetDto> normalized = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (AppSetDto set in sets.Sets)
        {
            if (set?.Name is null || set.Apps is null || set.Domains is null)
            {
                errors.Add("An app set is incomplete.");
                continue;
            }

            string name = set.Name.Trim();
            string label = Printable(name);
            if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
            {
                errors.Add($"An app set needs a name of 1 to {MaxNameLength} characters without control characters ('{(label.Length > MaxNameLength ? label[..MaxNameLength] + "..." : label)}').");
                continue;
            }

            if (!names.Add(name))
            {
                errors.Add($"The app set name '{label}' is used by more than one set (names ignore case).");
                continue;
            }

            errors.AddRange(AllowlistMapping.Validate(set.Apps, set.Domains).Select(error => $"Set '{label}': {Printable(error)}"));
            normalized.Add(set with { Name = name });
        }

        return errors.Count == 0
            ? new AppSetsValidation(new AppSetsDto(normalized), [])
            : new AppSetsValidation(null, [.. errors.Take(MaxErrors)]);
    }

    private static string Printable(string text) => string.Concat(text.Select(static c => char.IsControl(c) ? '?' : c));
}
