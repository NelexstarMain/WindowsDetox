using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blocker.Sdk;

namespace Blocker.Core.Plugins;

/// <summary>Validated content of a plugin's <c>plugin.json</c>.</summary>
/// <param name="Id">Reverse-DNS plugin id.</param>
/// <param name="Name">Display name, 1 to 64 characters.</param>
/// <param name="Version">Plugin version with exactly three parts.</param>
/// <param name="Author">Author display name, 1 to 64 characters.</param>
/// <param name="Description">Optional description, at most 500 characters.</param>
/// <param name="SdkVersion">SDK contract version (<c>major.minor</c>) the plugin was written for.</param>
/// <param name="EntryType">Full name of the public class that implements <c>IBlockerPlugin</c>.</param>
/// <param name="Capabilities">Declared capabilities, without duplicates.</param>
public sealed record PluginManifest(
    PluginId Id,
    string Name,
    Version Version,
    string Author,
    string? Description,
    Version SdkVersion,
    string EntryType,
    IReadOnlyList<PluginCapability> Capabilities);

/// <summary>Outcome of <see cref="PluginManifestParser.Parse"/>: a manifest, or a non-empty list of errors.</summary>
/// <param name="Manifest">The manifest; <see langword="null"/> when <paramref name="Errors"/> is not empty.</param>
/// <param name="Errors">Human-readable validation errors, one per problem.</param>
public sealed record PluginManifestParseResult(PluginManifest? Manifest, IReadOnlyList<string> Errors)
{
    /// <summary>Gets whether the manifest is valid.</summary>
    public bool Succeeded => Manifest is not null;
}

/// <summary>Strict parser for <c>plugin.json</c>: unknown properties, duplicates, wrong types and out-of-range values are errors.</summary>
public static partial class PluginManifestParser
{
    /// <summary>Maximum size of the manifest in UTF-8 bytes.</summary>
    public const int MaxBytes = 16 * 1024;

    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 500;
    private const int MaxEntryTypeLength = 256;
    private const string FileName = "plugin.json";

    private static readonly string[] Known = ["id", "name", "version", "author", "description", "sdkVersion", "entryType", "capabilities"];

    /// <summary>Parses and validates a manifest.</summary>
    /// <param name="json">Manifest text.</param>
    /// <returns>The manifest, or every validation error found.</returns>
    public static PluginManifestParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return Fail($"{FileName} exceeds the {MaxBytes / 1024} KB limit.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 8 });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Fail($"{FileName} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Validate(document.RootElement)
                : Fail($"{FileName} must contain a JSON object.");
        }
    }

    private static PluginManifestParseResult Validate(JsonElement root)
    {
        List<string> errors = [];
        Dictionary<string, JsonElement> props = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!Known.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add($"Unknown property '{Printable(property.Name)}'.");
            }
            else if (!props.TryAdd(property.Name, property.Value))
            {
                errors.Add($"Duplicate property '{property.Name}'.");
            }
        }

        string? id = Text(props, "id", required: true, errors);
        string? name = Text(props, "name", required: true, errors);
        string? versionText = Text(props, "version", required: true, errors);
        string? author = Text(props, "author", required: true, errors);
        string? description = Text(props, "description", required: false, errors);
        string? sdkText = Text(props, "sdkVersion", required: true, errors);
        string? entryType = Text(props, "entryType", required: true, errors);

        if (id is not null && !PluginId.IsValid(id))
        {
            errors.Add($"'id' is not a valid plugin id (expected e.g. 'com.example.my-plugin'): '{Printable(id)}'.");
        }

        CheckDisplayText(name, "name", MaxNameLength, errors);
        CheckDisplayText(author, "author", MaxNameLength, errors);
        if (description is not null && (description.Length > MaxDescriptionLength || description.Any(char.IsControl)))
        {
            errors.Add($"'description' must be at most {MaxDescriptionLength} characters without control characters.");
        }

        Version? version = null;
        if (versionText is not null && !(ThreePartVersion().IsMatch(versionText) && Version.TryParse(versionText, out version)))
        {
            errors.Add($"'version' must be 'major.minor.patch' with numbers up to 9 digits: '{Printable(versionText)}'.");
        }

        Version? sdk = null;
        if (sdkText is not null)
        {
            sdk = ParseSdkVersion(sdkText, errors);
        }

        if (entryType is not null && (entryType.Length > MaxEntryTypeLength || !TypeName().IsMatch(entryType)))
        {
            errors.Add($"'entryType' must be a C# type name such as 'MyPlugin.PluginMain': '{Printable(entryType)}'.");
        }

        List<PluginCapability> capabilities = ParseCapabilities(props, errors);
        return errors.Count > 0
            ? new PluginManifestParseResult(null, errors)
            : new PluginManifestParseResult(
                new PluginManifest(new PluginId(id!), name!, version!, author!, description, sdk!, entryType!, capabilities), []);
    }

    private static Version? ParseSdkVersion(string text, List<string> errors)
    {
        Match match = SdkVersion().Match(text);
        if (!match.Success)
        {
            errors.Add($"'sdkVersion' must be 'major.minor', for example '1.0': '{Printable(text)}'.");
            return null;
        }

        var parsed = new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        Version current = BlockerSdk.Version;
        if (parsed.Major != current.Major)
        {
            errors.Add($"'sdkVersion' major version {parsed.Major} is not supported; this Blocker supports SDK {current.Major}.x.");
        }
        else if (parsed.Minor > current.Minor)
        {
            errors.Add($"'sdkVersion' {text} is newer than the installed SDK {current.Major}.{current.Minor}.");
        }

        return parsed;
    }

    private static List<PluginCapability> ParseCapabilities(Dictionary<string, JsonElement> props, List<string> errors)
    {
        List<PluginCapability> result = [];
        if (!props.TryGetValue("capabilities", out JsonElement element))
        {
            errors.Add("'capabilities' is required (use [] when none are needed).");
            return result;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add("'capabilities' must be an array of strings.");
            return result;
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            string? text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (text is null)
            {
                errors.Add("'capabilities' must contain only strings.");
            }
            else if (!PluginCapabilities.TryParse(text, out PluginCapability capability))
            {
                errors.Add($"Unknown capability '{Printable(text)}'.");
            }
            else if (result.Contains(capability))
            {
                errors.Add($"Duplicate capability '{text}'.");
            }
            else
            {
                result.Add(capability);
            }
        }

        return result;
    }

    private static string? Text(Dictionary<string, JsonElement> props, string key, bool required, List<string> errors)
    {
        if (!props.TryGetValue(key, out JsonElement element))
        {
            if (required)
            {
                errors.Add($"'{key}' is required.");
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"'{key}' must be a string.");
            return null;
        }

        return element.GetString();
    }

    private static void CheckDisplayText(string? value, string key, int maxLength, List<string> errors)
    {
        if (value is not null && (value.Length is 0 or > MaxNameLength || value.Any(char.IsControl) || value.Trim().Length == 0))
        {
            errors.Add($"'{key}' must be 1 to {maxLength} characters of plain text.");
        }
    }

    private static string Printable(string value) => PluginText.Printable(value);

    private static PluginManifestParseResult Fail(string error) => new(null, [error]);

    [GeneratedRegex(@"^\d{1,9}\.\d{1,9}\.\d{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreePartVersion();

    [GeneratedRegex(@"^(\d{1,9})\.(\d{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkVersion();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeName();
}
