using Blocker.Contracts;
using Blocker.Core.Abstractions;
using Blocker.Core.AppRules;
using Blocker.Core.Clock;
using Blocker.Core.Ipc;
using Blocker.Core.Orchestration;
using Blocker.Core.PluginRuntime;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Core.Session;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.ServiceHost;

/// <summary>Everything the IPC handlers act on; built by <see cref="BlockerServiceApp"/>.</summary>
/// <param name="Coordinator">Runs sessions.</param>
/// <param name="SessionPolicies">The running session's allowlist.</param>
/// <param name="Draft">The allowlist being edited.</param>
/// <param name="Settings">Settings.</param>
/// <param name="ScanCache">Cached application scan.</param>
/// <param name="Scanner">Application scanner.</param>
/// <param name="Plugins">Installed plugins.</param>
/// <param name="PluginHost">Plugin host access.</param>
/// <param name="Processes">Process control (package family names).</param>
/// <param name="Events">Core event publisher.</param>
/// <param name="Verified">Verified clock.</param>
/// <param name="Time">Time source.</param>
/// <param name="Agent">The user-session agent.</param>
/// <param name="DevMode">Whether <c>session.devAbort</c> works.</param>
/// <param name="PluginTemplate">Produces the plugin starter ZIP.</param>
/// <param name="StateChanged">Called after an operation that may have changed the session state.</param>
/// <param name="AppSets">Saved app sets.</param>
/// <param name="AppRules">Per-app rules, their usage ledger and every decision on them.</param>
/// <param name="AppEnforcer">Meters the foreground app against the rules and enforces them outside sessions.</param>
internal sealed record ServiceHandlerDependencies(
    EnforcementCoordinator Coordinator,
    ISessionPolicyStore SessionPolicies,
    DraftAllowlistStore Draft,
    ServiceSettingsStore Settings,
    AppScanCache ScanCache,
    IAppInventoryScanner Scanner,
    PluginStore Plugins,
    IPluginHostGateway PluginHost,
    IProcessController Processes,
    IEventPublisher Events,
    IVerifiedClock Verified,
    TimeProvider Time,
    AgentLink Agent,
    bool DevMode,
    Func<CancellationToken, ValueTask<byte[]>> PluginTemplate,
    Func<CancellationToken, ValueTask> StateChanged,
    AppSetsStore AppSets,
    AppRuleEngine AppRules,
    AppRuleEnforcer AppEnforcer);

/// <summary>
/// Every client-callable IPC method of the service. Roles, the session gate and payload sizes are enforced by <see cref="IpcServer"/> from
/// <see cref="IpcMethodPolicy"/>; handlers repeat the checks that matter (administrator, no session) so a wrong table entry cannot open a hole.
/// </summary>
internal sealed partial class ServiceIpcHandlers(ServiceHandlerDependencies deps, ILogger<ServiceIpcHandlers> logger)
{
    private const int MaxPackageBytes = 3 * 1024 * 1024;
    private const int MaxScanResponseBytes = 3 * 1024 * 1024;
    private const int MaxPluginPayloadBytes = 64 * 1024;
    private const int PluginSessionPassesPerDay = 2;
    private const int PluginSessionPassMinutes = 10;
    private const string TemplateFileName = "WindowsDetox-PluginStarter.zip";

    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _pluginGate = new(1, 1);
    private readonly Lock _manifestGate = new();
    private readonly Dictionary<string, PluginInfoDto?> _infoCache = new(StringComparer.Ordinal);

    /// <summary>Registers every handler on <paramref name="server"/>.</summary>
    /// <param name="server">The service pipe server.</param>
    /// <returns>The registrations; dispose them to remove the handlers.</returns>
    public IReadOnlyList<IDisposable> RegisterAll(IpcServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return
        [
            server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, GetStatusAsync),
            server.Register<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, StartSessionAsync),
            server.Register<EmptyDto, EmergencyPassResponse>(IpcMethods.SessionUseEmergencyPass, UseEmergencyPassAsync),
            server.Register<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, BreakGlassAsync),
            server.Register<EmptyDto, DevAbortResponse>(IpcMethods.SessionDevAbort, DevAbortAsync),
            server.Register<EmptyDto, ScanAppsResponse>(IpcMethods.AllowlistScanApps, ScanAppsAsync),
            server.Register<EmptyDto, AllowlistDto>(IpcMethods.AllowlistGet, GetAllowlistAsync),
            server.Register<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, SetAllowlistAsync),
            server.Register<EmptyDto, AppSetsDto>(IpcMethods.AppSetsGet, GetAppSetsAsync),
            server.Register<AppSetsDto, OperationResponse>(IpcMethods.AppSetsSet, SetAppSetsAsync),
            server.Register<EmptyDto, AppRulesDto>(IpcMethods.AppRulesGet, GetAppRulesAsync),
            server.Register<AppRulesDto, OperationResponse>(IpcMethods.AppRulesSet, SetAppRulesAsync),
            server.Register<EmptyDto, AppRulesStatusResponse>(IpcMethods.AppRulesStatus, GetAppRulesStatusAsync),
            server.Register<EmptyDto, SettingsDto>(IpcMethods.SettingsGet, GetSettingsAsync),
            server.Register<SettingsDto, OperationResponse>(IpcMethods.SettingsSet, SetSettingsAsync),
            server.Register<EmptyDto, ListPluginsResponse>(IpcMethods.PluginsList, ListPluginsAsync),
            server.Register<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, InstallPluginAsync),
            server.Register<RemovePluginRequest, OperationResponse>(IpcMethods.PluginsRemove, RemovePluginAsync),
            server.Register<SetPluginEnabledRequest, OperationResponse>(IpcMethods.PluginsSetEnabled, SetPluginEnabledAsync),
            server.Register<DownloadTemplateRequest, DownloadTemplateResponse>(IpcMethods.PluginsDownloadTemplate, DownloadTemplateAsync),
            server.Register<GetContributionsRequest, GetContributionsResponse>(IpcMethods.UiGetContributions, GetContributionsAsync),
            server.Register<SendActionRequest, EmptyDto>(IpcMethods.UiSendAction, SendActionAsync),
            server.RegisterPluginHandler(InvokePluginAsync),
            server.Register<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, RegisterAgentAsync),
            server.Register<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, ForegroundWindowAsync),
            server.Register<IdleChangedDto, EmptyDto>(IpcMethods.AgentIdleChanged, IdleChangedAsync),
            server.Register<BlockInfoRequest, BlockInfoResponse>(IpcMethods.StubGetBlockInfo, GetBlockInfoAsync),
            server.Register<EmptyDto, SubscribeResponse>(IpcMethods.EventsSubscribe, SubscribeAsync),
        ];
    }

    private static T Require<T>(T? value)
        where T : class => value ?? throw new IpcException(IpcErrorCode.InvalidRequest, "Invalid parameters.");

    private static void RequireAdministrator(IpcRequestContext context)
    {
        if ((context.Identity.Roles & IpcCallerRole.Administrator) == IpcCallerRole.None)
        {
            throw new IpcException(IpcErrorCode.Unauthorized, "Not authorized.");
        }
    }

    private void RequireNoSession()
    {
        if (deps.Coordinator.IsSessionActive)
        {
            throw new IpcException(IpcErrorCode.NotAllowedDuringSession, "Not allowed while a session is running.");
        }
    }

    private static OperationResponse Failed(params string[] errors) => new(false, errors);

    private static string Printable(string text, int maxLength)
    {
        string clean = string.Concat(text.Select(static c => char.IsControl(c) ? '?' : c));
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private DateTimeOffset VerifiedNow() => deps.Verified.TryGetUtcNow(out DateTimeOffset utc) ? utc : deps.Time.GetUtcNow();

    // ---- session ----

    private ValueTask<SessionStatusDto> GetStatusAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SessionStatusDto.From(deps.Coordinator.Snapshot, deps.Coordinator.CleanupPending));

    private ValueTask<SubscribeResponse> SubscribeAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SubscribeResponse(SessionStatusDto.From(deps.Coordinator.Snapshot, deps.Coordinator.CleanupPending)));

    private ValueTask<StartSessionResponse> StartSessionAsync(IpcRequestContext context, StartSessionRequest request, CancellationToken cancellationToken) =>
        StartSessionCoreAsync(request, cancellationToken);

    /// <summary>
    /// Starts a session on behalf of a plugin that holds <c>session.start</c>: the saved allowlist (which must not be empty, so an automatic
    /// start never locks everything by accident) and the same pass defaults as the Setup page. The break-glass code is dropped, never returned.
    /// </summary>
    /// <param name="minutes">Session length in minutes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it started and why not.</returns>
    internal async ValueTask<(bool Started, string? Error)> StartPluginSessionAsync(int minutes, CancellationToken cancellationToken)
    {
        if (deps.Coordinator.IsSessionActive)
        {
            return (false, "A session is already running.");
        }

        AllowlistDto draft = await deps.Draft.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (draft.Apps.Count == 0)
        {
            return (false, "The saved allowlist is empty. Allow at least one app on the Allowlist page first.");
        }

        StartSessionResponse response = await StartSessionCoreAsync(
            new StartSessionRequest(minutes, PluginSessionPassesPerDay, PluginSessionPassMinutes, false, [], []),
            cancellationToken).ConfigureAwait(false);
        return (response.Success, response.Success ? null : response.Errors.FirstOrDefault() ?? "The session was not started.");
    }

    private async ValueTask<StartSessionResponse> StartSessionCoreAsync(StartSessionRequest request, CancellationToken cancellationToken)
    {
        Require(request.Apps);
        Require(request.Domains);
        RequireNoSession();
        IReadOnlyList<AllowlistEntryDto> apps = request.Apps;
        IReadOnlyList<string> domains = request.Domains;
        if (apps.Count == 0)
        {
            AllowlistDto draft = await deps.Draft.LoadAsync(cancellationToken).ConfigureAwait(false);
            apps = draft.Apps;
            domains = domains.Count > 0 ? domains : draft.Domains;
        }

        if (AllowlistMapping.Validate(apps, domains) is { Count: > 0 } errors)
        {
            return new StartSessionResponse(false, errors, null, null);
        }

        if (request.DurationMinutes is < 1 or > 60 * 24 * 31 || request.EmergencyPassDurationMinutes is < 1 or > 24 * 60)
        {
            return new StartSessionResponse(false, ["The session or emergency pass duration is out of range."], null, null);
        }

        // The zone is chosen here (the service's own), frozen for the session and never taken from the caller.
        var config = new SessionConfig(
            TimeSpan.FromMinutes(request.DurationMinutes),
            request.EmergencyPassesPerDay,
            TimeSpan.FromMinutes(request.EmergencyPassDurationMinutes),
            request.CarryOverUnusedPasses,
            TimeZoneInfo.Local.Id);
        var allowlist = new Allowlist(apps.Select(AllowlistMapping.ToEntry));
        StartEnforcedSessionResult result = await deps.Coordinator.StartSessionAsync(config, allowlist, domains, cancellationToken).ConfigureAwait(false);
        await deps.StateChanged(cancellationToken).ConfigureAwait(false);
        if (result.Started)
        {
            LogSessionStarted(logger, result.SessionId);
        }

        return new StartSessionResponse(result.Started, result.Errors, result.SessionId, result.BreakGlassCode, result.Warnings);
    }

    private async ValueTask<EmergencyPassResponse> UseEmergencyPassAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken)
    {
        if (deps.Coordinator.IsSessionActive)
        {
            await ThrowOnVetoAsync(LockAction.UseEmergencyPass, cancellationToken).ConfigureAwait(false);
        }

        EmergencyPassResult result = await deps.Coordinator.UseEmergencyPassAsync(cancellationToken).ConfigureAwait(false);
        await deps.StateChanged(cancellationToken).ConfigureAwait(false);
        EmergencyPassOutcome outcome = result switch
        {
            EmergencyPassResult.Started => EmergencyPassOutcome.Started,
            EmergencyPassResult.NoPassesLeft => EmergencyPassOutcome.NoPassesLeft,
            EmergencyPassResult.AlreadyActive => EmergencyPassOutcome.AlreadyActive,
            _ => EmergencyPassOutcome.NotInSession,
        };
        return new EmergencyPassResponse(outcome, SessionStatusDto.From(deps.Coordinator.Snapshot, deps.Coordinator.CleanupPending));
    }

    private async ValueTask<BreakGlassResponse> BreakGlassAsync(IpcRequestContext context, BreakGlassRequest request, CancellationToken cancellationToken)
    {
        bool ended = await deps.Coordinator.BreakGlassAsync(Require(request.Code), cancellationToken).ConfigureAwait(false);
        await deps.StateChanged(cancellationToken).ConfigureAwait(false);
        if (ended)
        {
            LogBreakGlassUsed(logger);
        }

        return new BreakGlassResponse(ended);
    }

    private async ValueTask<DevAbortResponse> DevAbortAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken)
    {
        if (!deps.DevMode)
        {
            return new DevAbortResponse(false);
        }

        bool aborted = await deps.Coordinator.DeveloperAbortAsync(cancellationToken).ConfigureAwait(false);
        await deps.StateChanged(cancellationToken).ConfigureAwait(false);
        return new DevAbortResponse(aborted);
    }

    // ---- allowlist and settings ----

    private async ValueTask<ScanAppsResponse> ScanAppsAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<InstalledApp> apps = await deps.Scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            if (await deps.Coordinator.UpdateScannedAppsAsync(apps, cancellationToken).ConfigureAwait(false))
            {
                await deps.ScanCache.SaveAsync(apps, cancellationToken).ConfigureAwait(false);
            }

            return new ScanAppsResponse(ToDtos(apps));
        }
        finally
        {
            _ = _scanGate.Release();
        }
    }

    /// <summary>Maps scan results to wire form; icons are dropped once the response would exceed its size budget.</summary>
    private static List<InstalledAppDto> ToDtos(IReadOnlyList<InstalledApp> apps)
    {
        List<InstalledAppDto> result = new(apps.Count);
        long budget = MaxScanResponseBytes;
        foreach (InstalledApp app in apps)
        {
            string? icon = app.IconPng is { Length: > 0 } png && Base64Length(png.Length) < budget ? Convert.ToBase64String(png) : null;
            budget -= (icon?.Length ?? 0) + 200 + app.DisplayName.Length + (app.ExecutablePath?.Length ?? 0);
            result.Add(new InstalledAppDto(app.DisplayName, app.ExecutablePath, app.Sha256, app.PackageFamilyName, icon, (AppSourceDto)(int)app.Sources));
        }

        return result;

        static long Base64Length(int bytes) => (bytes + 2) / 3 * 4;
    }

    private async ValueTask<AllowlistDto> GetAllowlistAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken)
    {
        // While a session runs, show what is enforced; otherwise the draft the next session will start from.
        if (deps.Coordinator.IsSessionActive && await deps.SessionPolicies.LoadAsync(cancellationToken).ConfigureAwait(false) is { } policy)
        {
            return new AllowlistDto([.. policy.Entries.Select(AllowlistMapping.ToDto)], policy.Domains);
        }

        return await deps.Draft.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OperationResponse> SetAllowlistAsync(IpcRequestContext context, AllowlistDto request, CancellationToken cancellationToken)
    {
        Require(request.Apps);
        Require(request.Domains);
        RequireNoSession();
        if (AllowlistMapping.Validate(request.Apps, request.Domains) is { Count: > 0 } errors)
        {
            return new OperationResponse(false, errors);
        }

        await deps.Draft.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        return new OperationResponse(true, []);
    }

    private async ValueTask<SettingsDto> GetSettingsAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken) =>
        new(await deps.Settings.GetAsync(cancellationToken).ConfigureAwait(false));

    private async ValueTask<OperationResponse> SetSettingsAsync(IpcRequestContext context, SettingsDto request, CancellationToken cancellationToken)
    {
        Require(request.Values);
        RequireNoSession();
        IReadOnlyList<string> errors = await deps.Settings.SetAsync(request.Values, cancellationToken).ConfigureAwait(false);
        return new OperationResponse(errors.Count == 0, errors);
    }

    // ---- stub and agent ----

    private ValueTask<BlockInfoResponse> GetBlockInfoAsync(IpcRequestContext context, BlockInfoRequest request, CancellationToken cancellationToken)
    {
        // The block id is only a hint from the launch command line: show its file name, never anything else, and never trust it further.
        string name = request.BlockId is { Length: > 0 and <= 1024 } id ? id[(id.LastIndexOfAny(['\\', '/']) + 1)..] : string.Empty;
        string subject = name.Length == 0 ? "an application" : Printable(name, 128);
        return ValueTask.FromResult(new BlockInfoResponse(subject, SessionStatusDto.From(deps.Coordinator.Snapshot, deps.Coordinator.CleanupPending)));
    }

    private ValueTask<AgentRegisterResponse> RegisterAgentAsync(IpcRequestContext context, AgentRegisterRequest request, CancellationToken cancellationToken)
    {
        bool accepted = request.ProtocolVersion == AgentProtocol.Version;
        if (accepted)
        {
            deps.Agent.Register(context.Peer);
            LogAgentRegistered(logger, request.SessionId);
        }

        return ValueTask.FromResult(new AgentRegisterResponse(accepted, AgentProtocol.Version));
    }

    private async ValueTask<EmptyDto> ForegroundWindowAsync(IpcRequestContext context, ForegroundWindowDto request, CancellationToken cancellationToken)
    {
        if (request.ProcessId <= 0 || (request.ImagePath?.Length ?? 0) > 1024)
        {
            return new EmptyDto();
        }

        string path = request.ImagePath ?? string.Empty;
        var window = new WindowDescriptor(
            request.ProcessId,
            path[(path.LastIndexOfAny(['\\', '/']) + 1)..],
            path,
            TryGetPackage(request.ProcessId),
            null);
        await deps.Events.PublishAsync(new ActiveWindowChanged(window, VerifiedNow()), cancellationToken).ConfigureAwait(false);
        if (deps.Coordinator.Snapshot.Phase == SessionPhase.Active
            && await deps.PluginHost.InterceptWindowAsync(window, cancellationToken).ConfigureAwait(false) is { Block: true } verdict)
        {
            _ = await deps.Coordinator.BlockWindowProcessAsync(request.ProcessId, request.StartTimeTicks, path, verdict.Reason, cancellationToken).ConfigureAwait(false);
        }

        // After the plugin check of the session, so nothing in the app rules can hold that up.
        await deps.AppEnforcer.OnForegroundAsync(path, window.PackageFamilyName, cancellationToken).ConfigureAwait(false);
        return new EmptyDto();
    }

    private async ValueTask<EmptyDto> IdleChangedAsync(IpcRequestContext context, IdleChangedDto request, CancellationToken cancellationToken)
    {
        TimeSpan idle = TimeSpan.FromSeconds(Math.Clamp(request.IdleSeconds, 0, 366 * 24 * 3600));
        deps.AppEnforcer.SetIdle(request.IsIdle);
        await deps.Events.PublishAsync(new UserIdleChanged(request.IsIdle, idle, VerifiedNow()), cancellationToken).ConfigureAwait(false);
        return new EmptyDto();
    }

    private string? TryGetPackage(int processId)
    {
        try
        {
            return deps.Processes.GetPackageFamilyName(processId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPackageLookupFailed(logger, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} started.")]
    private static partial void LogSessionStarted(ILogger logger, Guid? sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The break-glass code ended the session.")]
    private static partial void LogBreakGlassUsed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent registered from terminal session {TerminalSession}.")]
    private static partial void LogAgentRegistered(ILogger logger, int terminalSession);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading the package family name failed.")]
    private static partial void LogPackageLookupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} installed.")]
    private static partial void LogPluginInstalled(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} removed.")]
    private static partial void LogPluginRemoved(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting the files of plugin {PluginId} failed.")]
    private static partial void LogPluginRemoveFailed(ILogger logger, string pluginId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the manifest of installed plugin {PluginId} failed.")]
    private static partial void LogManifestUnreadable(ILogger logger, string pluginId);
}
