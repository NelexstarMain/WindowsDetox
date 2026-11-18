using System.Diagnostics.CodeAnalysis;

namespace Blocker.Platform.Windows.Changes;

/// <summary>
/// Strict, purely lexical validation of drive-absolute Windows paths (<c>C:\dir\file</c>). It is platform independent on
/// purpose (unit-testable on Linux) and refuses anything that needs OS resolution: UNC and device paths, forward slashes,
/// <c>.</c>/<c>..</c> segments, alternate data streams and reserved device names.
/// </summary>
internal static class WindowsPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns the path without trailing separator (a drive root keeps its <c>\</c>) or fails.</summary>
    public static bool TryNormalize(string? path, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (path is not { Length: >= 3 and <= 32000 } || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            return false;
        }

        string rest = path[3..];
        if (rest.Length > 0 && rest[^1] == '\\')
        {
            rest = rest[..^1];
        }

        if (rest.Length > 0)
        {
            foreach (string segment in rest.Split('\\'))
            {
                if (!IsValidSegment(segment))
                {
                    return false;
                }
            }
        }

        normalized = string.Concat(char.ToUpperInvariant(path[0]).ToString(), ":\\", rest);
        return true;
    }

    /// <summary>Like <see cref="TryNormalize"/> but throws <see cref="ArgumentException"/>.</summary>
    public static string Normalize(string? path, string paramName) =>
        TryNormalize(path, out string? normalized)
            ? normalized
            : throw new ArgumentException($"'{path}' is not a normalized drive-absolute Windows path.", paramName);

    /// <summary>Windows path comparison (ordinal, case-insensitive).</summary>
    public static bool AreEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file or directory name of a normalized path.</summary>
    public static string GetFileName(string normalized) => normalized[(normalized.LastIndexOf('\\') + 1)..];

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment[^1] is '.' or ' ' || segment.Length > 255)
        {
            return false;
        }

        if (segment.Any(c => c < ' ' || c is '<' or '>' or ':' or '"' or '/' or '|' or '?' or '*'))
        {
            return false;
        }

        int dot = segment.IndexOf('.', StringComparison.Ordinal);
        return !ReservedNames.Contains(dot < 0 ? segment : segment[..dot]);
    }
}
