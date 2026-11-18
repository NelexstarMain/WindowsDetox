using Blocker.Contracts;
using Blocker.Core.PluginRuntime;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;

namespace Blocker.Core.ServiceHost;

/// <summary>What the service needs from the plugin host; implemented over <see cref="PluginHostSupervisor"/> and replaced by a fake in tests.</summary>
public interface IPluginHostGateway
{
    /// <summary>Gets the plugins as last reported by the host.</summary>
    IReadOnlyList<HostPluginStatusDto> Plugins { get; }

    /// <summary>Tells the host to (re)load the enabled plugins.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The plugins as reported by the host.</returns>
    ValueTask<IReadOnlyList<HostPluginStatusDto>> ReloadAsync(CancellationToken cancellationToken);

    /// <summary>Runs the whole install pipeline on a package in a throw-away host.</summary>
    /// <param name="package">The plugin ZIP.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report.</returns>
    ValueTask<PluginInstallReportDto> EvaluatePackageAsync(ReadOnlyMemory<byte> package, CancellationToken cancellationToken);

    /// <summary>Collects lock-policy vetoes; empty when nobody objects or the host does not answer.</summary>
    /// <param name="action">The voluntary action.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The vetoes.</returns>
    ValueTask<EvaluateLockResponse> EvaluateLockAsync(LockAction action, CancellationToken cancellationToken);

    /// <summary>Renders all contributions of a slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The serialized trees.</returns>
    ValueTask<GetContributionsResponse> RenderSlotAsync(UiSlot slot, CancellationToken cancellationToken);

    /// <summary>Forwards a user interaction to a plugin contribution.</summary>
    /// <param name="request">The interaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    ValueTask<OperationResponse> SendUiActionAsync(SendActionRequest request, CancellationToken cancellationToken);

    /// <summary>Forwards a plugin IPC call.</summary>
    /// <param name="pluginId">Owning plugin.</param>
    /// <param name="method">Plugin-local method.</param>
    /// <param name="payloadJson">Request JSON.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response JSON.</returns>
    /// <exception cref="Blocker.Contracts.IpcException">The host or the plugin refused the call.</exception>
    ValueTask<string> InvokePluginMethodAsync(string pluginId, string method, string payloadJson, CancellationToken cancellationToken);

    /// <summary>Asks the process interceptors; abstain on any failure.</summary>
    /// <param name="process">The new process.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict.</returns>
    ValueTask<InterceptResponse> InterceptProcessAsync(ProcessDescriptor process, CancellationToken cancellationToken);

    /// <summary>Asks the window interceptors; abstain on any failure.</summary>
    /// <param name="window">The window.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict.</returns>
    ValueTask<InterceptResponse> InterceptWindowAsync(WindowDescriptor window, CancellationToken cancellationToken);

    /// <summary>Tells the host about a session change.</summary>
    /// <param name="snapshot">The new state.</param>
    /// <param name="verifiedUtcNow">Verified UTC now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the host acknowledged or the call failed.</returns>
    ValueTask NotifySessionChangedAsync(SessionSnapshot snapshot, DateTimeOffset verifiedUtcNow, CancellationToken cancellationToken);

    /// <summary>Delivers a core event to the plugins.</summary>
    /// <param name="evt">The event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the host acknowledged or the call failed.</returns>
    ValueTask PublishEventAsync(IBlockerEvent evt, CancellationToken cancellationToken);
}

/// <summary>The gateway of the running installation: every call goes to the <see cref="PluginHostSupervisor"/>.</summary>
/// <param name="supervisor">The supervisor.</param>
internal sealed class SupervisorPluginHostGateway(PluginHostSupervisor supervisor) : IPluginHostGateway
{
    public IReadOnlyList<HostPluginStatusDto> Plugins => supervisor.Plugins;

    public ValueTask<IReadOnlyList<HostPluginStatusDto>> ReloadAsync(CancellationToken cancellationToken) => supervisor.LoadPluginsAsync(cancellationToken);

    public ValueTask<PluginInstallReportDto> EvaluatePackageAsync(ReadOnlyMemory<byte> package, CancellationToken cancellationToken) =>
        supervisor.EvaluatePackageAsync(package, cancellationToken);

    public ValueTask<EvaluateLockResponse> EvaluateLockAsync(LockAction action, CancellationToken cancellationToken) =>
        supervisor.EvaluateLockAsync(action, cancellationToken);

    public ValueTask<GetContributionsResponse> RenderSlotAsync(UiSlot slot, CancellationToken cancellationToken) =>
        supervisor.RenderSlotAsync(slot, cancellationToken);

    public ValueTask<OperationResponse> SendUiActionAsync(SendActionRequest request, CancellationToken cancellationToken) =>
        supervisor.SendUiActionAsync(request, cancellationToken);

    public ValueTask<string> InvokePluginMethodAsync(string pluginId, string method, string payloadJson, CancellationToken cancellationToken) =>
        supervisor.InvokePluginMethodAsync(pluginId, method, payloadJson, cancellationToken);

    public ValueTask<InterceptResponse> InterceptProcessAsync(ProcessDescriptor process, CancellationToken cancellationToken) =>
        supervisor.InterceptProcessAsync(process, cancellationToken);

    public ValueTask<InterceptResponse> InterceptWindowAsync(WindowDescriptor window, CancellationToken cancellationToken) =>
        supervisor.InterceptWindowAsync(window, cancellationToken);

    public ValueTask NotifySessionChangedAsync(SessionSnapshot snapshot, DateTimeOffset verifiedUtcNow, CancellationToken cancellationToken) =>
        supervisor.NotifySessionChangedAsync(snapshot, verifiedUtcNow, cancellationToken);

    public ValueTask PublishEventAsync(IBlockerEvent evt, CancellationToken cancellationToken) => supervisor.PublishEventAsync(evt, cancellationToken);
}
