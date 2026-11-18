using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Input validation, JSON and error helpers shared by the change handlers.</summary>
internal static class ChangeGuard
{
    /// <summary>Serializes a state record compactly (source-generated, trim/AOT safe).</summary>
    public static string ToJson<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    /// <summary>Parses a state record; malformed or <c>null</c> JSON becomes an <see cref="ArgumentException"/>.</summary>
    public static T FromJson<T>(string json, JsonTypeInfo<T> typeInfo, string paramName)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo) ?? throw new ArgumentException("State JSON must not be 'null'.", paramName);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"State JSON is malformed: {ex.Message}", paramName, ex);
        }
    }

    /// <summary>Install ids end up in registry data: restrict them to a short, harmless alphabet.</summary>
    public static string RequireInstallId(string? installId)
    {
        if (string.IsNullOrEmpty(installId) || installId.Length > 64 || !installId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '{' or '}'))
        {
            throw new ArgumentException("Install id must be 1-64 characters of letters, digits, '-', '_', '{' or '}'.", nameof(installId));
        }

        return installId;
    }

    /// <summary>SCM service names: non-empty, at most 256 characters, no path separators or control characters.</summary>
    public static string RequireServiceName(string? name, string paramName = "target")
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(c => char.IsControl(c) || c is '/' or '\\'))
        {
            throw new ArgumentException("Invalid service name.", paramName);
        }

        return name;
    }

    /// <summary>The last Win32 error recorded by a <c>SetLastError</c> P/Invoke, as an exception.</summary>
    public static Win32Exception LastError(string api) => new(Marshal.GetLastPInvokeError(), $"{api} failed");

    /// <summary>A Win32 (or WFP) status code returned directly by an API, as an exception.</summary>
    public static Win32Exception Error(string api, uint code) => new(unchecked((int)code), $"{api} failed (0x{code:X8})");
}
