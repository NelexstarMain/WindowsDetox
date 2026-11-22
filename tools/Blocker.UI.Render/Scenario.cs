using System.Text.Json;

namespace Blocker.UI.Render;

/// <summary>One screenshot: where to navigate, how big the window is, the session state and what to do before the picture is taken.</summary>
/// <param name="Name">File name without extension; also the value of <c>--only</c>.</param>
/// <param name="Target"><c>main:dashboard|allowlist|setup|plugins|settings</c> or <c>plugin:calendar|goals|insights</c> (the plugin's page).</param>
/// <param name="Size">Window size in device-independent pixels; defaults to 1100 x 700.</param>
/// <param name="Session"><c>idle</c> (default), <c>active</c> or <c>pass</c>.</param>
/// <param name="Steps">Interactions executed in order.</param>
internal sealed record Scenario(string Name, string Target, int[]? Size, string? Session, IReadOnlyList<ScenarioStep>? Steps);

/// <summary>
/// One interaction. Exactly one of <see cref="Action"/>, <see cref="Click"/>, <see cref="Drag"/> or <see cref="Wait"/> is set.
/// <c>action</c> goes through the view model command of the view that shows the element (the same path as a real click);
/// <c>click</c> and <c>drag</c> inject real mouse input at window coordinates.
/// </summary>
internal sealed record ScenarioStep
{
    /// <summary>Gets the UI action element id.</summary>
    public string? Action { get; init; }

    /// <summary><c>click</c>, <c>toggle</c> or <c>submit</c>; inferred when absent (submit when <see cref="Args"/> is set, else click).</summary>
    public string? Kind { get; init; }

    /// <summary>Gets the toggle state, <c>true</c> or <c>false</c>.</summary>
    public string? Value { get; init; }

    /// <summary>Gets the form field values of a submit.</summary>
    public Dictionary<string, JsonElement>? Args { get; init; }

    /// <summary>Gets the window coordinates of a click.</summary>
    public double[]? Click { get; init; }

    /// <summary>Gets the start and end window coordinates of a drag.</summary>
    public double[][]? Drag { get; init; }

    /// <summary>Gets a pause in milliseconds.</summary>
    public int? Wait { get; init; }
}

/// <summary>The scenario file.</summary>
/// <param name="Shots">Screenshots in render order.</param>
internal sealed record ScenarioFile(IReadOnlyList<Scenario> Shots)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads and checks a scenario file.</summary>
    /// <param name="path">Path of the JSON file.</param>
    /// <returns>The scenarios.</returns>
    public static ScenarioFile Load(string path)
    {
        ScenarioFile file = JsonSerializer.Deserialize<ScenarioFile>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"{path} is empty.");
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Scenario shot in file.Shots)
        {
            if (string.IsNullOrWhiteSpace(shot.Name) || !names.Add(shot.Name))
            {
                throw new InvalidDataException($"Shot names must be unique and not empty ('{shot.Name}').");
            }
        }

        return file;
    }
}

/// <summary>Command-line options: <c>--out &lt;dir&gt;</c>, <c>--only a,b</c>, <c>--scenarios &lt;file&gt;</c>.</summary>
/// <param name="OutputDirectory">Where the PNG files go.</param>
/// <param name="Only">Names to render; empty means all.</param>
/// <param name="ScenarioPath">The scenario file.</param>
internal sealed record RunOptions(string OutputDirectory, IReadOnlySet<string> Only, string ScenarioPath)
{
    /// <summary>Parses the command line.</summary>
    /// <param name="args">Arguments.</param>
    /// <returns>The options.</returns>
    public static RunOptions Parse(string[] args)
    {
        string output = Path.Combine(Environment.CurrentDirectory, "renders");
        string scenarios = Path.Combine(AppContext.BaseDirectory, "scenarios.json");
        HashSet<string> only = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--out":
                    output = value;
                    break;
                case "--scenarios":
                    scenarios = value;
                    break;
                case "--only":
                    only.UnionWith(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}.");
            }

            i++;
        }

        return new RunOptions(output, only, scenarios);
    }
}
