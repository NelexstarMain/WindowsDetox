namespace Blocker.Sdk.Diagnostics;

/// <summary>Severity of a plugin log entry.</summary>
public enum PluginLogLevel
{
    /// <summary>Detailed diagnostic information.</summary>
    Debug,

    /// <summary>Normal operational messages.</summary>
    Information,

    /// <summary>Unexpected but recoverable situations.</summary>
    Warning,

    /// <summary>Failures.</summary>
    Error,
}

/// <summary>
/// Rate-limited logger. Entries are written to the plugin's log and shown in the Plugin Manager.
/// Never log secrets or window titles.
/// </summary>
public interface IPluginLogger
{
    /// <summary>Writes a log entry.</summary>
    /// <param name="level">Severity.</param>
    /// <param name="message">Message text (truncated to 2 000 characters).</param>
    /// <param name="exception">Optional exception.</param>
    void Log(PluginLogLevel level, string message, Exception? exception = null);
}

/// <summary>Convenience methods for <see cref="IPluginLogger"/>.</summary>
public static class PluginLoggerExtensions
{
    /// <summary>Writes an <see cref="PluginLogLevel.Information"/> entry.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="message">Message text.</param>
    public static void Info(this IPluginLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(PluginLogLevel.Information, message);
    }

    /// <summary>Writes a <see cref="PluginLogLevel.Warning"/> entry.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="message">Message text.</param>
    public static void Warn(this IPluginLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(PluginLogLevel.Warning, message);
    }

    /// <summary>Writes an <see cref="PluginLogLevel.Error"/> entry.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="message">Message text.</param>
    /// <param name="exception">Optional exception.</param>
    public static void Error(this IPluginLogger logger, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(PluginLogLevel.Error, message, exception);
    }
}
