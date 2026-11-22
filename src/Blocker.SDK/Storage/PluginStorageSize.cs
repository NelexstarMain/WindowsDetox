using System.Text.Json;

namespace Blocker.Sdk.Storage;

/// <summary>
/// Quota accounting shared by the production storage and the in-memory fake: the UTF-8 size of the JSON-escaped key and
/// value, which is what the file holds (the default JSON encoder escapes non-ASCII text and <c>&amp;&lt;&gt;'+</c>).
/// </summary>
internal static class PluginStorageSize
{
    public static long Of(string key, string value) => Encoded(key) + Encoded(value);

    public static bool IsWellFormed(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static void ThrowIfMalformed(string value, string paramName)
    {
        if (!IsWellFormed(value))
        {
            throw new ArgumentException("The value contains an unpaired UTF-16 surrogate and cannot be stored as UTF-8.", paramName);
        }
    }

    private static long Encoded(string text) => JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length;
}
