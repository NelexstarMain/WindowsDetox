using System.Text;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Builds service binary path command lines and polls for service shutdown (pure, unit-testable).</summary>
internal static class ServiceCommandLine
{
    /// <summary>SERVICE_STOPPED.</summary>
    internal const uint StateStopped = 1;

    /// <summary>
    /// <c>"exe" arguments</c>: the executable is always quoted (it may contain spaces; unquoted paths are a privilege-escalation
    /// vector). <paramref name="arguments"/> is an already-formatted argument string (see <see cref="QuoteArgument"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The path is not a normalized drive-absolute path or the arguments contain control characters.</exception>
    public static string Build(string executablePath, string? arguments)
    {
        string exe = WindowsPath.Normalize(executablePath, nameof(executablePath));
        if (arguments is not null && arguments.Any(char.IsControl))
        {
            throw new ArgumentException("Arguments must not contain control characters.", nameof(arguments));
        }

        return string.IsNullOrWhiteSpace(arguments) ? $"\"{exe}\"" : $"\"{exe}\" {arguments.Trim()}";
    }

    /// <summary>Quotes one argument following the CommandLineToArgvW / MSVCRT rules.</summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        StringBuilder builder = new("\"");
        for (int i = 0; i < argument.Length; i++)
        {
            int backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                builder.Append('\\', backslashes * 2); // backslashes before the closing quote
                break;
            }

            if (argument[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>Quotes and joins arguments.</summary>
    public static string JoinArguments(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(QuoteArgument));

    /// <summary>Polls <paramref name="queryState"/> until the service is stopped; false when <paramref name="timeout"/> elapses first.</summary>
    public static bool WaitUntilStopped(Func<uint> queryState, TimeProvider time, Action<TimeSpan> sleep, TimeSpan timeout, TimeSpan interval)
    {
        long start = time.GetTimestamp();
        while (queryState() != StateStopped)
        {
            if (time.GetElapsedTime(start) >= timeout)
            {
                return false;
            }

            sleep(interval);
        }

        return true;
    }
}
