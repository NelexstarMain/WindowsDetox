namespace Blocker.Contracts;

/// <summary>Named-pipe names (without the <c>\\.\pipe\</c> prefix) shared by clients and the service.</summary>
public static class IpcPipeNames
{
    /// <summary>Pipe of <c>Blocker.Service</c> for the UI, agent and block screen.</summary>
    public const string Service = "WindowsDetox.Service";

    /// <summary>Prefix of the dedicated low-integrity pipes of plugin hosts; the host id is appended.</summary>
    public const string PluginHostPrefix = "WindowsDetox.PluginHost.";
}
