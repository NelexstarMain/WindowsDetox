using Blocker.Sdk.Diagnostics;

namespace Blocker.Plugin.Testing;

/// <summary>One entry captured by <see cref="RecordingPluginLogger"/>.</summary>
/// <param name="Level">Severity.</param>
/// <param name="Message">Message, truncated to 2 000 characters like the host logger.</param>
/// <param name="Exception">Optional exception.</param>
public sealed record PluginLogEntry(PluginLogLevel Level, string Message, Exception? Exception);

/// <summary>Thread-safe <see cref="IPluginLogger"/> that records every entry for assertions.</summary>
public sealed class RecordingPluginLogger : IPluginLogger
{
    private const int MaxMessageLength = 2000;
    private readonly List<PluginLogEntry> _entries = [];

    /// <summary>Gets a snapshot of the entries logged so far, oldest first.</summary>
    public IReadOnlyList<PluginLogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc/>
    public void Log(PluginLogLevel level, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        string text = message.Length > MaxMessageLength ? message[..MaxMessageLength] : message;
        lock (_entries)
        {
            _entries.Add(new PluginLogEntry(level, text, exception));
        }
    }
}
