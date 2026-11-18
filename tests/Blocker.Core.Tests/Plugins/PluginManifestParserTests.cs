using Blocker.Core.Plugins;
using Blocker.Sdk;

namespace Blocker.Core.Tests.Plugins;

public sealed class PluginManifestParserTests
{
    private static readonly (string Key, string Raw)[] Base =
    [
        ("id", "\"com.test.sample\""),
        ("name", "\"Sample\""),
        ("version", "\"1.2.3\""),
        ("author", "\"Tester\""),
        ("description", "\"Does a thing.\""),
        ("sdkVersion", "\"1.0\""),
        ("entryType", "\"Sample.Plugin\""),
        ("capabilities", "[\"events.session\", \"tick\"]"),
    ];

    [Fact]
    public void Icon_is_optional_and_accepts_path_data()
    {
        Assert.Null(PluginManifestParser.Parse(Json()).Manifest!.Icon);
        Assert.Equal("M0 0 L10 10 Z", PluginManifestParser.Parse(Json(("icon", "\"M0 0 L10 10 Z\""))).Manifest!.Icon);
    }

    [Theory]
    [InlineData("\"<svg/>\"")]
    [InlineData("\"\"")]
    [InlineData("5")]
    public void Invalid_icon_is_rejected(string raw)
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(Json(("icon", raw)));

        Assert.False(result.Succeeded);
    }

    /// <summary>Builds a manifest from <see cref="Base"/>; a null raw value removes the property, an unknown key adds one.</summary>
    private static string Json(params (string Key, string? Raw)[] overrides)
    {
        List<(string Key, string Raw)> props = [.. Base];
        foreach ((string key, string? raw) in overrides)
        {
            int index = props.FindIndex(p => p.Key == key);
            if (index >= 0)
            {
                props.RemoveAt(index);
            }

            if (raw is not null)
            {
                props.Insert(index >= 0 ? index : props.Count, (key, raw));
            }
        }

        return "{" + string.Join(",", props.Select(p => $"\"{p.Key}\":{p.Raw}")) + "}";
    }

    private static string Quoted(string value) => "\"" + value + "\"";

    [Fact]
    public void Parses_a_complete_manifest()
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(PluginTestSupport.ValidManifestJson);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Errors);
        PluginManifest manifest = Assert.IsType<PluginManifest>(result.Manifest);
        Assert.Equal("com.test.sample", manifest.Id.Value);
        Assert.Equal("Sample", manifest.Name);
        Assert.Equal(new Version(1, 2, 3), manifest.Version);
        Assert.Equal("Tester", manifest.Author);
        Assert.Equal("Does a thing.", manifest.Description);
        Assert.Equal(new Version(1, 0), manifest.SdkVersion);
        Assert.Equal("Sample.Plugin", manifest.EntryType);
        Assert.Equal([PluginCapability.SessionEvents, PluginCapability.Tick], manifest.Capabilities);
    }

    [Fact]
    public void Description_is_optional_and_capabilities_may_be_empty()
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(Json(("description", null), ("capabilities", "[]")));

        Assert.True(result.Succeeded);
        Assert.Null(result.Manifest!.Description);
        Assert.Empty(result.Manifest.Capabilities);
    }

    [Fact]
    public void Accepts_boundary_lengths()
    {
        string json = Json(("name", Quoted(new string('n', 64))), ("author", Quoted(new string('a', 64))), ("description", Quoted(new string('d', 500))));

        PluginManifestParseResult result = PluginManifestParser.Parse(json);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Accepts_every_known_capability_name()
    {
        string all = "[" + string.Join(",", PluginCapabilities.AllNames.Select(Quoted)) + "]";

        PluginManifestParseResult result = PluginManifestParser.Parse(Json(("capabilities", all)));

        Assert.Equal(Enum.GetValues<PluginCapability>(), result.Manifest!.Capabilities);
    }

    [Theory]
    [InlineData("id", "\"Bad Id\"", "'id' is not a valid plugin id (expected e.g. 'com.example.my-plugin'): 'Bad Id'.")]
    [InlineData("id", "\"single\"", "'id' is not a valid plugin id (expected e.g. 'com.example.my-plugin'): 'single'.")]
    [InlineData("id", "\"Com.Example.X\"", "'id' is not a valid plugin id (expected e.g. 'com.example.my-plugin'): 'Com.Example.X'.")]
    [InlineData("id", "\"\"", "'id' is not a valid plugin id (expected e.g. 'com.example.my-plugin'): ''.")]
    [InlineData("id", "5", "'id' must be a string.")]
    [InlineData("id", "null", "'id' must be a string.")]
    [InlineData("id", null, "'id' is required.")]
    [InlineData("name", "\"\"", "'name' must be 1 to 64 characters of plain text.")]
    [InlineData("name", "\"   \"", "'name' must be 1 to 64 characters of plain text.")]
    [InlineData("name", "\"tab\\there\"", "'name' must be 1 to 64 characters of plain text.")]
    [InlineData("name", null, "'name' is required.")]
    [InlineData("author", "\"\"", "'author' must be 1 to 64 characters of plain text.")]
    [InlineData("author", "true", "'author' must be a string.")]
    [InlineData("author", null, "'author' is required.")]
    [InlineData("version", "\"1.0\"", "'version' must be 'major.minor.patch' with numbers up to 9 digits: '1.0'.")]
    [InlineData("version", "\"1.0.0.0\"", "'version' must be 'major.minor.patch' with numbers up to 9 digits: '1.0.0.0'.")]
    [InlineData("version", "\"1.0.0-beta\"", "'version' must be 'major.minor.patch' with numbers up to 9 digits: '1.0.0-beta'.")]
    [InlineData("version", "\"a.b.c\"", "'version' must be 'major.minor.patch' with numbers up to 9 digits: 'a.b.c'.")]
    [InlineData("version", "\"1.0.9999999999\"", "'version' must be 'major.minor.patch' with numbers up to 9 digits: '1.0.9999999999'.")]
    [InlineData("version", null, "'version' is required.")]
    [InlineData("description", "7", "'description' must be a string.")]
    [InlineData("description", "\"line\\nbreak\"", "'description' must be at most 500 characters without control characters.")]
    [InlineData("sdkVersion", "\"1\"", "'sdkVersion' must be 'major.minor', for example '1.0': '1'.")]
    [InlineData("sdkVersion", "\"1.0.0\"", "'sdkVersion' must be 'major.minor', for example '1.0': '1.0.0'.")]
    [InlineData("sdkVersion", "\"2.0\"", "'sdkVersion' major version 2 is not supported; this Blocker supports SDK 1.x.")]
    [InlineData("sdkVersion", "\"0.9\"", "'sdkVersion' major version 0 is not supported; this Blocker supports SDK 1.x.")]
    [InlineData("sdkVersion", "\"1.1\"", "'sdkVersion' 1.1 is newer than the installed SDK 1.0.")]
    [InlineData("sdkVersion", null, "'sdkVersion' is required.")]
    [InlineData("entryType", "\"1Bad\"", "'entryType' must be a C# type name such as 'MyPlugin.PluginMain': '1Bad'.")]
    [InlineData("entryType", "\"A..B\"", "'entryType' must be a C# type name such as 'MyPlugin.PluginMain': 'A..B'.")]
    [InlineData("entryType", "\"A.B<T>\"", "'entryType' must be a C# type name such as 'MyPlugin.PluginMain': 'A.B<T>'.")]
    [InlineData("entryType", "\"A.B+Nested\"", "'entryType' must be a C# type name such as 'MyPlugin.PluginMain': 'A.B+Nested'.")]
    [InlineData("entryType", "\"\"", "'entryType' must be a C# type name such as 'MyPlugin.PluginMain': ''.")]
    [InlineData("entryType", null, "'entryType' is required.")]
    [InlineData("capabilities", "[\"events.nothing\"]", "Unknown capability 'events.nothing'.")]
    [InlineData("capabilities", "[\"Tick\"]", "Unknown capability 'Tick'.")]
    [InlineData("capabilities", "[\"tick\", \"tick\"]", "Duplicate capability 'tick'.")]
    [InlineData("capabilities", "\"tick\"", "'capabilities' must be an array of strings.")]
    [InlineData("capabilities", "[1]", "'capabilities' must contain only strings.")]
    [InlineData("capabilities", "[null]", "'capabilities' must contain only strings.")]
    [InlineData("capabilities", null, "'capabilities' is required (use [] when none are needed).")]
    [InlineData("extra", "1", "Unknown property 'extra'.")]
    public void Rejects_an_invalid_field_with_exactly_one_error(string key, string? raw, string expected)
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(Json((key, raw)));

        Assert.False(result.Succeeded);
        Assert.Null(result.Manifest);
        Assert.Equal([expected], result.Errors);
    }

    [Fact]
    public void Rejects_a_name_longer_than_64_characters()
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(Json(("name", Quoted(new string('n', 65)))));

        Assert.Equal(["'name' must be 1 to 64 characters of plain text."], result.Errors);
    }

    [Fact]
    public void Rejects_a_description_longer_than_500_characters()
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(Json(("description", Quoted(new string('d', 501)))));

        Assert.Equal(["'description' must be at most 500 characters without control characters."], result.Errors);
    }

    [Fact]
    public void Rejects_a_duplicate_property()
    {
        string json = PluginTestSupport.ValidManifestJson.Replace("\"name\": \"Sample\",", "\"name\": \"Sample\", \"name\": \"Other\",", StringComparison.Ordinal);

        PluginManifestParseResult result = PluginManifestParser.Parse(json);

        Assert.Equal(["Duplicate property 'name'."], result.Errors);
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(Json(("id", "\"X\""), ("version", "\"1\""), ("extra", "0")));

        Assert.Equal(
            [
                "Unknown property 'extra'.",
                "'id' is not a valid plugin id (expected e.g. 'com.example.my-plugin'): 'X'.",
                "'version' must be 'major.minor.patch' with numbers up to 9 digits: '1'.",
            ],
            result.Errors);
    }

    [Theory]
    [InlineData("[]", "plugin.json must contain a JSON object.")]
    [InlineData("\"text\"", "plugin.json must contain a JSON object.")]
    [InlineData("null", "plugin.json must contain a JSON object.")]
    public void Rejects_input_that_is_not_a_manifest_object(string json, string expected)
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(json);

        Assert.Equal([expected], result.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ // comment\n}")]
    [InlineData("{\"id\": \"a.b\",}")]
    [InlineData("{\"id\": ")]
    [InlineData("not json")]
    public void Rejects_malformed_json_comments_and_trailing_commas(string json)
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(json);

        string error = Assert.Single(result.Errors);
        Assert.StartsWith("plugin.json is not valid JSON: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_manifest_larger_than_16_kb()
    {
        string json = Json(("description", Quoted(new string('d', PluginManifestParser.MaxBytes))));

        PluginManifestParseResult result = PluginManifestParser.Parse(json);

        Assert.Equal(["plugin.json exceeds the 16 KB limit."], result.Errors);
    }

    [Fact]
    public void Rejects_excessively_nested_json()
    {
        string json = Json(("extra", string.Concat(Enumerable.Repeat("[", 20)) + string.Concat(Enumerable.Repeat("]", 20))));

        PluginManifestParseResult result = PluginManifestParser.Parse(json);

        Assert.StartsWith("plugin.json is not valid JSON: ", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Control_characters_in_values_are_not_echoed_in_errors()
    {
        PluginManifestParseResult result = PluginManifestParser.Parse(Json(("id", "\"bad\\u001b[31mid\"")));

        Assert.Equal(["'id' is not a valid plugin id (expected e.g. 'com.example.my-plugin'): 'bad?[31mid'."], result.Errors);
    }
}
