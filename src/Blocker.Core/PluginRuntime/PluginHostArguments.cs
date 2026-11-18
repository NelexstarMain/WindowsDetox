using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Blocker.Core.PluginRuntime;

/// <summary>
/// Command line of <c>Blocker.PluginHost</c>: <c>--pipe &lt;name&gt; --plugins &lt;dir&gt; --data &lt;dir&gt; [--token &lt;token&gt;]</c>.
/// The service builds it with <see cref="Format"/>; the host reads it with <see cref="TryParse"/>.
/// </summary>
/// <param name="PipeName">Name of the plugin-host pipe on the service.</param>
/// <param name="PluginsDirectory">Directory of installed plugins (<see cref="PluginStore"/> root).</param>
/// <param name="DataDirectory">Directory of plugin data.</param>
/// <param name="Token">Launch token echoed in <c>pluginHost.register</c>.</param>
public sealed record PluginHostArguments(string PipeName, string PluginsDirectory, string DataDirectory, string? Token = null)
{
    /// <summary>Parses the arguments the operating system already split.</summary>
    /// <param name="args">Process arguments.</param>
    /// <param name="result">The parsed arguments on success.</param>
    /// <param name="error">What is wrong on failure.</param>
    /// <returns><see langword="true"/> when all required options are present and nothing is unknown.</returns>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out PluginHostArguments? result, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Count; i += 2)
        {
            if (args[i] is not ("--pipe" or "--plugins" or "--data" or "--token"))
            {
                result = null;
                error = $"Unknown argument '{args[i]}'.";
                return false;
            }

            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || !values.TryAdd(args[i], args[i + 1]))
            {
                result = null;
                error = $"Option '{args[i]}' needs exactly one non-empty value.";
                return false;
            }
        }

        foreach (string required in new[] { "--pipe", "--plugins", "--data" })
        {
            if (!values.ContainsKey(required))
            {
                result = null;
                error = $"Missing option '{required}'.";
                return false;
            }
        }

        result = new PluginHostArguments(values["--pipe"], values["--plugins"], values["--data"], values.GetValueOrDefault("--token"));
        error = null;
        return true;
    }

    /// <summary>Builds the command-line text (Windows quoting rules) for <see cref="Core.Ports.IRestrictedProcessLauncher"/>.</summary>
    /// <returns>The arguments, without the executable.</returns>
    public string Format()
    {
        StringBuilder text = new();
        text.Append("--pipe ").Append(Quote(PipeName))
            .Append(" --plugins ").Append(Quote(PluginsDirectory))
            .Append(" --data ").Append(Quote(DataDirectory));
        if (Token is not null)
        {
            text.Append(" --token ").Append(Quote(Token));
        }

        return text.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"'))
        {
            return value;
        }

        StringBuilder quoted = new("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                quoted.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
