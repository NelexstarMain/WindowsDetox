namespace Blocker.Core.Policy;

/// <summary>Windows path helpers that behave identically on every host OS (tests run on Linux): both separators, case-insensitive, dot segments resolved.</summary>
internal static class WinPath
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Lower-case, back-slash separated, without <c>\\?\</c> prefix, trailing separator or dot segments; empty for blank input.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        string p = path.Trim();
        bool unc = false;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = p[8..];
            unc = true;
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
        }
        else
        {
            unc = p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal);
        }

        var stack = new List<string>();
        foreach (string segment in p.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(segment);
        }

        string joined = string.Join('\\', stack).ToLowerInvariant();
        return unc ? @"\\" + joined : joined;
    }

    /// <summary>
    /// True for a path on a local drive letter (<c>C:\x</c>, also behind <c>\\?\</c>). Shares (<c>\\server\share</c>, <c>\\?\UNC\</c>), device and
    /// volume paths (<c>\\.\</c>, <c>\\?\GLOBALROOT\</c>) and relative paths are not: opening them from the service would reach out over the network
    /// or into a device, so they are never read.
    /// </summary>
    public static bool IsLocalDrivePath(string? path)
    {
        ReadOnlySpan<char> p = path.AsSpan().Trim();
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/';
    }

    /// <summary>True when the already-normalized <paramref name="path"/> is strictly below the already-normalized <paramref name="directory"/>.</summary>
    public static bool IsUnder(string path, string directory) =>
        directory.Length > 0
        && path.Length > directory.Length
        && path[directory.Length] == '\\'
        && path.StartsWith(directory, StringComparison.Ordinal);

    /// <summary>File name without a launchable extension (<c>.exe</c>, <c>.com</c>, <c>.scr</c>) and without trailing dots/spaces.</summary>
    public static string ImageName(string? pathOrName)
    {
        if (string.IsNullOrEmpty(pathOrName))
        {
            return "";
        }

        ReadOnlySpan<char> name = pathOrName.AsSpan();
        name = name[(name.LastIndexOfAny(Separators) + 1)..].TrimEnd(" .");
        foreach (string ext in (ReadOnlySpan<string>)[".exe", ".com", ".scr"])
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length].ToString();
            }
        }

        return name.ToString();
    }
}
