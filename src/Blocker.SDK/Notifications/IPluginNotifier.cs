namespace Blocker.Sdk.Notifications;

/// <summary>
/// Requires <see cref="PluginCapability.Notify"/>. Shows a short toast through the Blocker agent in the user's session. The title
/// is always the plugin's name, so a plugin cannot imitate a system message. Toasts are rate limited per plugin.
/// </summary>
public interface IPluginNotifier
{
    /// <summary>Shows a toast.</summary>
    /// <param name="message">Plain text, at most <see cref="PluginNotifierLimits.MaxMessageLength"/> characters; control characters are replaced.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the toast was handed to the agent; <see langword="false"/> when it was rate limited or no agent is connected.</returns>
    ValueTask<bool> ShowAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>Limits of <see cref="IPluginNotifier"/>.</summary>
public static class PluginNotifierLimits
{
    /// <summary>Longest toast text.</summary>
    public const int MaxMessageLength = 200;

    /// <summary>Toasts one plugin may show per hour; further toasts are dropped.</summary>
    public const int MaxPerHour = 20;
}
