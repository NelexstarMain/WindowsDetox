namespace Blocker.Core.Plugins;

/// <summary>Helpers for putting untrusted text into messages.</summary>
internal static class PluginText
{
    /// <summary>Replaces control characters with '?' and shortens the text to <paramref name="maxLength"/> characters.</summary>
    public static string Printable(string value, int maxLength = 64)
    {
        string text = string.Concat(value.Select(c => char.IsControl(c) ? '?' : c));
        return text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 3), "...");
    }
}
