namespace Blocker.UI;

/// <summary>Command-line options of <c>Blocker.UI.exe</c>.</summary>
/// <param name="IsAgent">Run without a window as the user-session agent (<c>--agent</c>).</param>
internal sealed record StartupOptions(bool IsAgent)
{
    /// <summary>Parses the command line; unknown arguments are ignored.</summary>
    /// <param name="args">Process arguments.</param>
    /// <returns>The options.</returns>
    public static StartupOptions Parse(IEnumerable<string> args) =>
        new(args.Any(a => string.Equals(a, "--agent", StringComparison.OrdinalIgnoreCase)));
}
