using System.Text;

namespace Blocker.Platform.Windows.Processes;

/// <summary>Builds Windows command lines (CommandLineToArgvW / MSVCRT quoting rules). Pure managed code.</summary>
internal static class CommandLine
{
    /// <summary>Quotes one argument so that it round-trips through CommandLineToArgvW. Plain tokens are returned unquoted.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        // Trailing backslashes must be doubled so they do not escape the closing quote.
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>Joins arguments into one quoted command-line tail.</summary>
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    /// <summary>
    /// Full command line: the executable is always quoted (so paths with spaces cannot be hijacked by a shorter prefix such as <c>C:\Program.exe</c>);
    /// <paramref name="arguments"/> is an already formatted argument string appended verbatim.
    /// </summary>
    public static string Build(string executablePath, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"'))
        {
            throw new ArgumentException("A Windows path cannot contain a double quote.", nameof(executablePath));
        }

        var quotedPath = "\"" + executablePath + "\"";
        return string.IsNullOrWhiteSpace(arguments) ? quotedPath : quotedPath + " " + arguments;
    }
}
