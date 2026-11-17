using System.Text;
using System.Text.Json;
using Blocker.Sdk;

namespace Blocker.Core.Events;

/// <summary>Size and syntax checks for custom event payloads (same rules as the plugin testing fake).</summary>
internal static class PayloadValidator
{
    public static void ThrowIfInvalid(string json, string paramName)
    {
        if (Encoding.UTF8.GetByteCount(json) > BlockerSdk.MaxPayloadBytes)
        {
            throw new ArgumentException($"Payload exceeds {BlockerSdk.MaxPayloadBytes} bytes.", paramName);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Payload is not valid JSON: {ex.Message}", paramName, ex);
        }
    }
}
