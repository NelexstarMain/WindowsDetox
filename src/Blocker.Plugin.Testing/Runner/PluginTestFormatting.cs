using System.Globalization;
using System.Text;

namespace Blocker.Plugin.Testing.Runner;

/// <summary>Safe, bounded text formatting for test names, messages and stack traces.</summary>
internal static class PluginTestFormatting
{
    private const int MaxDisplayNameLength = 200;
    private const int MaxStackTraceLines = 20;

    public static string DisplayName(string className, string method, object?[]? args)
    {
        string name = args is null
            ? $"{className}.{method}"
            : $"{className}.{method}({string.Join(", ", args.Select(FormatArgument))})";
        return name.Length <= MaxDisplayNameLength ? name : string.Concat(name.AsSpan(0, MaxDisplayNameLength - 3), "...");
    }

    /// <summary>Removes control characters except line feed (carriage returns are dropped).</summary>
    public static string Message(string text) => string.Concat(text.Where(c => c == '\n' || !char.IsControl(c)));

    public static string? StackTrace(string? trace) =>
        trace is null ? null : string.Join('\n', Message(trace).Split('\n').Take(MaxStackTraceLines));

    private static string FormatArgument(object? value) => value switch
    {
        null => "null",
        string s => $"\"{Escape(s)}\"",
        char c => $"'{Escape(c.ToString())}'",
        bool b => b ? "true" : "false",
        IFormattable f => Escape(f.ToString(null, CultureInfo.InvariantCulture)),
        _ => Escape(value.ToString() ?? value.GetType().Name),
    };

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
