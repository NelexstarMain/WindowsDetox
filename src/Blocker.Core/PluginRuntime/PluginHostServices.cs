using Blocker.Contracts;
using Blocker.Sdk;
using Blocker.Sdk.Notifications;
using Blocker.Sdk.Sessions;

namespace Blocker.Core.PluginRuntime;

/// <summary>Production <see cref="ISessionLauncher"/>.</summary>
internal sealed class HostSessionLauncher(PluginInfo plugin, IPluginHostServiceChannel channel) : ISessionLauncher
{
    public async ValueTask<SessionStartResult> StartAsync(TimeSpan duration, string reason, CancellationToken cancellationToken = default)
    {
        if (!plugin.Has(PluginCapability.SessionStart))
        {
            throw new PluginCapabilityException(PluginCapability.SessionStart);
        }

        ArgumentNullException.ThrowIfNull(reason);
        if (duration < SessionLaunchLimits.MinDuration || duration > SessionLaunchLimits.MaxDuration)
        {
            return new SessionStartResult(false, "The session length is out of range.");
        }

        string text = PluginTextSanitizer.Printable(reason, SessionLaunchLimits.MaxReasonLength);
        int minutes = (int)Math.Ceiling(duration.TotalMinutes);
        PluginStartSessionResponse response = await channel.StartSessionAsync(new(plugin.Id.Value, minutes, text), cancellationToken).ConfigureAwait(false);
        return new SessionStartResult(response.Started, response.Started ? null : response.Error ?? "The session was not started.");
    }
}

/// <summary>Production <see cref="IPluginNotifier"/>.</summary>
internal sealed class HostPluginNotifier(PluginInfo plugin, IPluginHostServiceChannel channel) : IPluginNotifier
{
    public async ValueTask<bool> ShowAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!plugin.Has(PluginCapability.Notify))
        {
            throw new PluginCapabilityException(PluginCapability.Notify);
        }

        ArgumentNullException.ThrowIfNull(message);
        string text = PluginTextSanitizer.Printable(message, PluginNotifierLimits.MaxMessageLength);
        if (text.Length == 0)
        {
            return false;
        }

        return (await channel.NotifyAsync(new(plugin.Id.Value, text), cancellationToken).ConfigureAwait(false)).Shown;
    }
}

/// <summary>Text cleaning shared by the host wrappers.</summary>
internal static class PluginTextSanitizer
{
    /// <summary>Replaces control characters with <c>?</c>, trims and cuts to <paramref name="maxLength"/>.</summary>
    public static string Printable(string text, int maxLength)
    {
        string clean = string.Concat(text.Select(static c => char.IsControl(c) ? '?' : c)).Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}
