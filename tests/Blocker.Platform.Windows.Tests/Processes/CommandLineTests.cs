using System.Text;
using Blocker.Platform.Windows.Processes;

namespace Blocker.Platform.Windows.Tests.Processes;

public sealed class CommandLineTests
{
    /// <summary>Reference parser implementing the documented CommandLineToArgvW / MSVCRT rules, to prove that quoting round-trips.</summary>
    private static List<string> Parse(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var started = false;
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                var count = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    count++;
                    i++;
                }

                started = true;
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\Program Files\app\", "\"C:\\Program Files\\app\\\\\"")] // trailing backslash doubled
    [InlineData(@"C:\no\spaces\here", @"C:\no\spaces\here")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")] // backslash before a quote
    public void Quote_FollowsWindowsRules(string argument, string expected) => Assert.Equal(expected, CommandLine.Quote(argument));

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("say \"hi\"")]
    [InlineData(@"C:\Program Files\app\")]
    [InlineData("a\\\"b")]
    [InlineData("tab\tseparated")]
    [InlineData(@"\\server\share\dir with space\")]
    [InlineData("\"")]
    [InlineData("\\\\\"\\\\")]
    public void Quote_RoundTripsThroughReferenceParser(string argument) =>
        Assert.Equal([argument], Parse(CommandLine.Quote(argument)));

    [Fact]
    public void Join_RoundTripsMultipleArguments()
    {
        string[] arguments = ["--agent", "--pipe", @"\\.\pipe\Blocker Service", "", "x\"y"];

        Assert.Equal(arguments, Parse(CommandLine.Join(arguments)));
    }

    [Fact]
    public void Build_AlwaysQuotesTheExecutable()
    {
        Assert.Equal("\"C:\\Program Files\\Blocker\\ui.exe\" --agent", CommandLine.Build(@"C:\Program Files\Blocker\ui.exe", "--agent"));
        Assert.Equal("\"C:\\a.exe\"", CommandLine.Build(@"C:\a.exe", "  "));
    }

    [Fact]
    public void Build_RejectsPathsWithQuotes() =>
        Assert.Throws<ArgumentException>(() => CommandLine.Build("C:\\a\"b.exe", string.Empty));
}
