namespace Blocker.Plugin.Testing;

/// <summary>Thrown by <see cref="FakePluginContext.RenderAsync"/> when a rendered UI tree is invalid.</summary>
public sealed class UiValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="errors">Validation errors; must not be empty.</param>
    public UiValidationException(IReadOnlyList<string> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>Gets every validation error.</summary>
    public IReadOnlyList<string> Errors { get; }

    private static string BuildMessage(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return $"The UI tree is invalid ({errors.Count} error(s)): {string.Join("; ", errors)}";
    }
}
