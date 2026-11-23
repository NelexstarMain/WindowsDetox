using System.IO.Abstractions;
using Blocker.Contracts;
using Blocker.Core.Abstractions;
using Blocker.Core.Clock;
using Blocker.Core.Ipc;
using Blocker.Core.Journal;
using Blocker.Core.Orchestration;
using Blocker.Core.Ports;
using Blocker.Core.Session;
using Blocker.Core.Supervision;

namespace Blocker.Core.ServiceHost;

/// <summary>What the service needs to run the plugin host process on its own low-integrity pipe.</summary>
public sealed record PluginHostPorts
{
    /// <summary>Gets the launcher of the restricted host process.</summary>
    public required IRestrictedProcessLauncher Launcher { get; init; }

    /// <summary>Gets the factory of the plugin-host pipe instances (Low integrity label on Windows).</summary>
    public required IPipeListenerFactory PipeFactory { get; init; }

    /// <summary>Gets the resolver that accepts the plugin host (and nothing else) on that pipe.</summary>
    public required ICallerIdentityResolver IdentityResolver { get; init; }
}

/// <summary>
/// Every platform dependency of the service, so the whole composition (<see cref="BlockerServiceApp"/>) runs on Linux against fakes and on
/// Windows against the Win32 implementations. Nothing in here is optional unless stated.
/// </summary>
public sealed record BlockerServicePorts
{
    /// <summary>Gets the file system.</summary>
    public required IFileSystem FileSystem { get; init; }

    /// <summary>Gets the machine-bound secret protection (DPAPI on Windows).</summary>
    public required ISecretProtector SecretProtector { get; init; }

    /// <summary>Gets the process monitor.</summary>
    public required IProcessMonitor ProcessMonitor { get; init; }

    /// <summary>Gets the process controller.</summary>
    public required IProcessController ProcessController { get; init; }

    /// <summary>Gets the launcher of the block screen in the user's session.</summary>
    public required IUserSessionLauncher UserSessionLauncher { get; init; }

    /// <summary>Gets the signature verifier.</summary>
    public required ISignatureVerifier SignatureVerifier { get; init; }

    /// <summary>Gets the browser firewall.</summary>
    public required IBrowserFirewall BrowserFirewall { get; init; }

    /// <summary>Gets the application scanner of the allowlist picker.</summary>
    public required IAppInventoryScanner AppScanner { get; init; }

    /// <summary>Gets the planner of the system changes of a session.</summary>
    public required IEnforcementChangePlanner ChangePlanner { get; init; }

    /// <summary>Gets the factory of the journal's change handlers; the argument is the installation's ownership marker.</summary>
    public required Func<string, IReadOnlyList<IReversibleChangeHandler>> CreateChangeHandlers { get; init; }

    /// <summary>Gets the factory of the service pipe instances.</summary>
    public required IPipeListenerFactory ServicePipeFactory { get; init; }

    /// <summary>Gets the resolver of the roles of service pipe clients.</summary>
    public required ICallerIdentityResolver ServiceIdentityResolver { get; init; }

    /// <summary>Gets the plugin host, or <see langword="null"/> when <see cref="PluginHostGateway"/> is supplied instead.</summary>
    public PluginHostPorts? PluginHost { get; init; }

    /// <summary>Gets a ready plugin host gateway (tests); when set, no plugin host process is supervised.</summary>
    public IPluginHostGateway? PluginHostGateway { get; init; }

    /// <summary>Gets the service installer used to start the watchdog while a session runs; <see langword="null"/> skips that.</summary>
    public IServiceInstaller? ServiceInstaller { get; init; }

    /// <summary>Gets the service control port for supervising the watchdog; <see langword="null"/> skips mutual supervision.</summary>
    public IServiceControl? ServiceControl { get; init; }

    /// <summary>Gets the process wait port for supervising the watchdog; <see langword="null"/> skips mutual supervision.</summary>
    public IProcessWaiter? ProcessWaiter { get; init; }

    /// <summary>Gets the independent network time sources (SNTP and HTTPS mix).</summary>
    public required IReadOnlyList<INetworkTimeSource> TimeSources { get; init; }

    /// <summary>Gets the monotonic clock.</summary>
    public required IMonotonicClock Clock { get; init; }

    /// <summary>Gets the time source for every timer and timeout.</summary>
    public required TimeProvider Time { get; init; }

    /// <summary>Gets the identity of the current boot (stable within a boot, different after a reboot).</summary>
    public required Func<long> BootMarker { get; init; }

    /// <summary>Gets the static enforcement settings (paths, browsers, bypass tools, services).</summary>
    public required EnforcementSettings Enforcement { get; init; }

    /// <summary>Gets the producer of the plugin starter ZIP.</summary>
    public required Func<CancellationToken, ValueTask<byte[]>> PluginTemplate { get; init; }

    /// <summary>Gets whether developer abort works (DEVMODE builds started with <c>--dev-mode</c> only).</summary>
    public bool DevMode { get; init; }

    /// <summary>
    /// Gets the action that gives the data root its protected DACL (SYSTEM and Administrators only); <see langword="null"/> skips it. It runs at
    /// every start before any state file is read, and a failure is logged without stopping the service.
    /// </summary>
    public Action<BlockerPaths>? ProtectDataRoot { get; init; }

    /// <summary>Gets the name of the service pipe.</summary>
    public string ServicePipeName { get; init; } = IpcPipeNames.Service;

    /// <summary>Gets the factory of the filter proxy; the default listens on the configured loopback port.</summary>
    public WebFilterProxyFactory? ProxyFactory { get; init; }

    /// <summary>Gets the PBKDF2 iterations of the break-glass hash.</summary>
    public int BreakGlassIterations { get; init; } = BreakGlassCode.DefaultIterations;

    /// <summary>Gets the limits of the service pipe.</summary>
    public IpcServerOptions? IpcOptions { get; init; }

    /// <summary>Checks that the plugin host is specified exactly once.</summary>
    /// <exception cref="ArgumentException">Neither or both of <see cref="PluginHost"/> and <see cref="PluginHostGateway"/> are set.</exception>
    public void Validate()
    {
        if ((PluginHost is null) == (PluginHostGateway is null))
        {
            throw new ArgumentException("Exactly one of PluginHost and PluginHostGateway must be set.", nameof(PluginHost));
        }

        Enforcement.Validate();
    }
}
