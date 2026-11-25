using Blocker.Sdk.Session;
using Blocker.UI.Sdk;

namespace Blocker.Contracts;

/// <summary>Request or response without members (<c>{}</c>). Methods without parameters accept an absent <c>params</c> too.</summary>
public sealed record EmptyDto;

/// <summary>Generic outcome of an operation.</summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Errors">User-readable reasons when it did not.</param>
public sealed record OperationResponse(bool Success, IReadOnlyList<string> Errors);

// ---- session ----

/// <summary>Wire form of <see cref="SessionSnapshot"/>; durations are in seconds.</summary>
/// <param name="Phase">Current phase.</param>
/// <param name="SessionId">Running session id, or <see langword="null"/>.</param>
/// <param name="RemainingSeconds">Time until the session ends.</param>
/// <param name="EmergencyPassRemainingSeconds">Time left on the active emergency pass.</param>
/// <param name="EmergencyPassesLeftToday">Passes still available today.</param>
/// <param name="EmergencyPassesPerDay">Passes granted per day.</param>
/// <param name="EmergencyPassDurationSeconds">Length of one pass.</param>
/// <param name="CleanupPending">The rollback after a finished session failed and is being retried: some restrictions may still be in place.</param>
public sealed record SessionStatusDto(
    SessionPhase Phase,
    Guid? SessionId,
    double? RemainingSeconds,
    double? EmergencyPassRemainingSeconds,
    int EmergencyPassesLeftToday,
    int EmergencyPassesPerDay,
    double EmergencyPassDurationSeconds,
    bool CleanupPending = false)
{
    /// <summary>Converts a snapshot to its wire form.</summary>
    /// <param name="snapshot">Snapshot.</param>
    /// <param name="cleanupPending">Whether a rollback is still being retried.</param>
    /// <returns>The DTO.</returns>
    public static SessionStatusDto From(SessionSnapshot snapshot, bool cleanupPending = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            snapshot.Phase,
            snapshot.SessionId,
            snapshot.Remaining?.TotalSeconds,
            snapshot.EmergencyPassRemaining?.TotalSeconds,
            snapshot.EmergencyPassesLeftToday,
            snapshot.EmergencyPassesPerDay,
            snapshot.EmergencyPassDuration.TotalSeconds,
            cleanupPending);
    }

    /// <summary>Converts the wire form back to a snapshot.</summary>
    /// <returns>The snapshot.</returns>
    public SessionSnapshot ToSnapshot() => new(
        Phase,
        SessionId,
        RemainingSeconds is { } r ? TimeSpan.FromSeconds(r) : null,
        EmergencyPassRemainingSeconds is { } p ? TimeSpan.FromSeconds(p) : null,
        EmergencyPassesLeftToday,
        EmergencyPassesPerDay,
        TimeSpan.FromSeconds(EmergencyPassDurationSeconds));
}

/// <summary>One application of the allowlist.</summary>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="ExecutablePath">Executable path, if any.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the executable, if any.</param>
/// <param name="PackageFamilyName">Store package family name, if any.</param>
public sealed record AllowlistEntryDto(string DisplayName, string? ExecutablePath, string? Sha256, string? PackageFamilyName);

/// <summary>The allowlist: applications and domain patterns (for example <c>example.com</c> or <c>*.example.com</c>).</summary>
/// <param name="Apps">Allowed applications.</param>
/// <param name="Domains">Allowed domain patterns.</param>
public sealed record AllowlistDto(IReadOnlyList<AllowlistEntryDto> Apps, IReadOnlyList<string> Domains);

/// <summary>A named, reusable selection of apps and domains for starting a session.</summary>
/// <param name="Name">Name shown on the set chip; unique, case-insensitive.</param>
/// <param name="Apps">Applications of the set.</param>
/// <param name="Domains">Domain patterns of the set.</param>
public sealed record AppSetDto(string Name, IReadOnlyList<AllowlistEntryDto> Apps, IReadOnlyList<string> Domains);

/// <summary>Every saved app set, in the order the user arranged them.</summary>
/// <param name="Sets">The sets.</param>
public sealed record AppSetsDto(IReadOnlyList<AppSetDto> Sets);

/// <summary>A blocked range within one weekday, in local wall-clock minutes.</summary>
/// <param name="Day">Weekday, 0 = Monday through 6 = Sunday.</param>
/// <param name="StartMinute">First blocked minute after midnight (0 to 1439).</param>
/// <param name="EndMinute">Minute after midnight at which the block ends, exclusive (1 to 1440).</param>
public sealed record WeeklyRangeDto(int Day, int StartMinute, int EndMinute);

/// <summary>How an app's usage-time pool is measured.</summary>
public enum AppBudgetMode
{
    /// <summary>No pool: the app is unlimited outside sessions and scheduled blocks.</summary>
    None,

    /// <summary>The same number of minutes every day.</summary>
    Daily,

    /// <summary>A separate number of minutes for each weekday.</summary>
    PerWeekday,

    /// <summary>One pool of minutes for the whole week, Monday to Sunday.</summary>
    Weekly,
}

/// <summary>An app's usage-time pool. Only the fields of the chosen <see cref="Mode"/> are used.</summary>
/// <param name="Mode">How the pool is measured.</param>
/// <param name="DailyMinutes">Minutes per day for <see cref="AppBudgetMode.Daily"/>.</param>
/// <param name="PerWeekdayMinutes">Seven values, Monday to Sunday, for <see cref="AppBudgetMode.PerWeekday"/>.</param>
/// <param name="WeeklyMinutes">Minutes per week for <see cref="AppBudgetMode.Weekly"/>.</param>
public sealed record AppBudgetDto(AppBudgetMode Mode, int DailyMinutes, IReadOnlyList<int>? PerWeekdayMinutes, int WeeklyMinutes);

/// <summary>
/// Rules for one app that apply outside sessions. Precedence: a running session decides first, then a scheduled
/// block, then the usage pool.
/// </summary>
/// <param name="App">The app the rule applies to.</param>
/// <param name="BlockedRanges">Weekly ranges during which the app is blocked.</param>
/// <param name="Budget">Usage-time pool.</param>
public sealed record AppRuleDto(AllowlistEntryDto App, IReadOnlyList<WeeklyRangeDto> BlockedRanges, AppBudgetDto Budget);

/// <summary>Every per-app rule.</summary>
/// <param name="Rules">The rules, at most one per app.</param>
public sealed record AppRulesDto(IReadOnlyList<AppRuleDto> Rules);

/// <summary>What currently decides about an app with a rule.</summary>
public enum AppRuleState
{
    /// <summary>The app may run; usage counts against its pool if it has one.</summary>
    Allowed,

    /// <summary>A running session decides about the app.</summary>
    SessionControlled,

    /// <summary>The app is inside one of its scheduled blocked ranges.</summary>
    ScheduledBlock,

    /// <summary>The app used up its pool for the current day or week.</summary>
    BudgetExhausted,
}

/// <summary>Live state of one app rule.</summary>
/// <param name="App">The app.</param>
/// <param name="State">What currently decides about the app.</param>
/// <param name="UsedSeconds">Foreground time counted in the current pool period (day or week); 0 without a pool.</param>
/// <param name="RemainingSeconds">Pool time left in the current period; null without a pool.</param>
/// <param name="BlockedUntil">When the current scheduled block or exhausted pool ends; null when not blocked.</param>
public sealed record AppRuleStatusDto(AllowlistEntryDto App, AppRuleState State, long UsedSeconds, long? RemainingSeconds, DateTimeOffset? BlockedUntil);

/// <summary>Result of <see cref="IpcMethods.AppRulesStatus"/>.</summary>
/// <param name="Items">One entry per rule.</param>
public sealed record AppRulesStatusResponse(IReadOnlyList<AppRuleStatusDto> Items);

/// <summary>Parameters of <see cref="IpcMethods.SessionStart"/>. The service chooses the time zone itself.</summary>
/// <param name="DurationMinutes">Session length.</param>
/// <param name="EmergencyPassesPerDay">Passes per day.</param>
/// <param name="EmergencyPassDurationMinutes">Length of one pass.</param>
/// <param name="CarryOverUnusedPasses">Whether unused passes accumulate.</param>
/// <param name="Apps">Allowed applications for this session.</param>
/// <param name="Domains">Allowed domain patterns for this session.</param>
public sealed record StartSessionRequest(
    int DurationMinutes,
    int EmergencyPassesPerDay,
    int EmergencyPassDurationMinutes,
    bool CarryOverUnusedPasses,
    IReadOnlyList<AllowlistEntryDto> Apps,
    IReadOnlyList<string> Domains);

/// <summary>Result of <see cref="IpcMethods.SessionStart"/>.</summary>
/// <param name="Success">Whether the session started.</param>
/// <param name="Errors">Why it did not.</param>
/// <param name="SessionId">Id of the new session.</param>
/// <param name="BreakGlassCode">One-time recovery code; delivered exactly once, never stored in plain text.</param>
/// <param name="Warnings">Protections that could not be applied although the session started; null from an older service.</param>
public sealed record StartSessionResponse(bool Success, IReadOnlyList<string> Errors, Guid? SessionId, string? BreakGlassCode, IReadOnlyList<string>? Warnings = null);

/// <summary>Outcome of an emergency-pass request; mirrors the service's result type.</summary>
public enum EmergencyPassOutcome
{
    /// <summary>The pass started.</summary>
    Started,

    /// <summary>No passes are left today.</summary>
    NoPassesLeft,

    /// <summary>A pass is already running.</summary>
    AlreadyActive,

    /// <summary>No session is running.</summary>
    NotInSession,
}

/// <summary>Result of <see cref="IpcMethods.SessionUseEmergencyPass"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Status">Session state after the request.</param>
public sealed record EmergencyPassResponse(EmergencyPassOutcome Outcome, SessionStatusDto Status);

/// <summary>Parameters of <see cref="IpcMethods.SessionBreakGlass"/>.</summary>
/// <param name="Code">The recovery code shown at session start.</param>
public sealed record BreakGlassRequest(string Code);

/// <summary>Result of <see cref="IpcMethods.SessionBreakGlass"/>; a wrong code reveals nothing beyond <c>Success = false</c>.</summary>
/// <param name="Success">Whether the session ended.</param>
public sealed record BreakGlassResponse(bool Success);

/// <summary>Result of <see cref="IpcMethods.SessionDevAbort"/>.</summary>
/// <param name="Aborted">Whether the session was aborted (only possible in DEVMODE builds).</param>
public sealed record DevAbortResponse(bool Aborted);

// ---- allowlist ----

/// <summary>Where an installed application was discovered.</summary>
[Flags]
public enum AppSourceDto
{
    /// <summary>Unknown.</summary>
    None = 0,

    /// <summary>Start Menu shortcut.</summary>
    StartMenu = 1,

    /// <summary>Uninstall registry key.</summary>
    Registry = 2,

    /// <summary>Microsoft Store package.</summary>
    StorePackage = 4,

    /// <summary>Currently running process.</summary>
    Running = 8,
}

/// <summary>An installed application candidate for the allowlist.</summary>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="ExecutablePath">Main executable, if resolvable.</param>
/// <param name="Sha256">Lower-case hex hash, if computed.</param>
/// <param name="PackageFamilyName">Store package family name, if any.</param>
/// <param name="IconPngBase64">32 px PNG icon, base64.</param>
/// <param name="Sources">Where it was found.</param>
public sealed record InstalledAppDto(string DisplayName, string? ExecutablePath, string? Sha256, string? PackageFamilyName, string? IconPngBase64, AppSourceDto Sources);

/// <summary>Result of <see cref="IpcMethods.AllowlistScanApps"/>. Responses may use the full <see cref="IpcLimits.MaxMessageBytes"/>.</summary>
/// <param name="Apps">Discovered applications.</param>
public sealed record ScanAppsResponse(IReadOnlyList<InstalledAppDto> Apps);

// ---- settings ----

/// <summary>Settings as string key/value pairs; unknown keys are rejected by the service.</summary>
/// <param name="Values">Setting values.</param>
public sealed record SettingsDto(IReadOnlyDictionary<string, string> Values);

// ---- plugins ----

/// <summary>An installed plugin.</summary>
/// <param name="Id">Plugin id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Version">Version text.</param>
/// <param name="Author">Author.</param>
/// <param name="Enabled">Whether the plugin is enabled.</param>
/// <param name="Capabilities">Granted capability names.</param>
/// <param name="Faulted">Whether the plugin was stopped by the circuit breaker after repeated failures.</param>
public sealed record PluginInfoDto(string Id, string Name, string Version, string Author, bool Enabled, IReadOnlyList<string> Capabilities, bool Faulted = false);

/// <summary>Result of <see cref="IpcMethods.PluginsList"/>.</summary>
/// <param name="Plugins">Installed plugins.</param>
public sealed record ListPluginsResponse(IReadOnlyList<PluginInfoDto> Plugins);

/// <summary>Parameters of <see cref="IpcMethods.PluginsInstall"/>.</summary>
/// <param name="PackageBase64">The plugin ZIP, base64 encoded.</param>
public sealed record InstallPluginRequest(string PackageBase64);

/// <summary>Outcome of one plugin test, as reported by the install pipeline.</summary>
public enum PluginTestOutcomeDto
{
    /// <summary>The test passed.</summary>
    Passed,

    /// <summary>The test threw or violated a runner rule.</summary>
    Failed,

    /// <summary>The test exceeded its time budget.</summary>
    TimedOut,

    /// <summary>The test uses a feature the runner does not implement.</summary>
    NotSupported,
}

/// <summary>Result of one plugin test.</summary>
/// <param name="Name">Test display name.</param>
/// <param name="Outcome">Outcome.</param>
/// <param name="Message">Failure message; absent when the test passed.</param>
public sealed record PluginTestResultDto(string Name, PluginTestOutcomeDto Outcome, string? Message = null);

/// <summary>Result of <see cref="IpcMethods.PluginsInstall"/>.</summary>
/// <param name="Success">Whether the plugin was installed.</param>
/// <param name="PluginId">Id of the installed plugin.</param>
/// <param name="Errors">Validation, compile or test errors.</param>
/// <param name="FailedStage">Name of the pipeline stage that failed (<c>Package</c>, <c>Manifest</c>, <c>Policy</c>, <c>Compile</c>, <c>Load</c>, <c>Tests</c> or <c>Host</c>); absent on success and from older services.</param>
/// <param name="Tests">Every test that ran, passing ones included; absent when the tests did not run.</param>
public sealed record InstallPluginResponse(
    bool Success,
    string? PluginId,
    IReadOnlyList<string> Errors,
    string? FailedStage = null,
    IReadOnlyList<PluginTestResultDto>? Tests = null);

/// <summary>Parameters of <see cref="IpcMethods.PluginsRemove"/>.</summary>
/// <param name="PluginId">Plugin to remove.</param>
public sealed record RemovePluginRequest(string PluginId);

/// <summary>Parameters of <see cref="IpcMethods.PluginsSetEnabled"/>.</summary>
/// <param name="PluginId">Plugin to change.</param>
/// <param name="Enabled">New state.</param>
public sealed record SetPluginEnabledRequest(string PluginId, bool Enabled);

/// <summary>Parameters of <see cref="IpcMethods.PluginsDownloadTemplate"/>.</summary>
/// <param name="Template">Template name, or <see langword="null"/> for the default.</param>
public sealed record DownloadTemplateRequest(string? Template);

/// <summary>Result of <see cref="IpcMethods.PluginsDownloadTemplate"/>.</summary>
/// <param name="FileName">Suggested file name.</param>
/// <param name="PackageBase64">Template ZIP, base64 encoded.</param>
public sealed record DownloadTemplateResponse(string FileName, string PackageBase64);

// ---- ui ----

/// <summary>Where a contribution is shown.</summary>
public enum UiSlot
{
    /// <summary>Navigation page.</summary>
    Page,

    /// <summary>Dashboard tile.</summary>
    DashboardWidget,

    /// <summary>Settings section.</summary>
    SettingsSection,

    /// <summary>Block-screen element.</summary>
    BlockScreen,
}

/// <summary>Parameters of <see cref="IpcMethods.UiGetContributions"/>.</summary>
/// <param name="Slot">Slot to fetch.</param>
public sealed record GetContributionsRequest(UiSlot Slot);

/// <summary>One rendered contribution.</summary>
/// <param name="PluginId">Owning plugin.</param>
/// <param name="ContributionId">Id of the contribution within the plugin.</param>
/// <param name="Title">Navigation or section title, if the slot has one.</param>
/// <param name="Json">Tree produced by <c>UiJson.Serialize</c>. Receivers must use <c>UiJson.Deserialize</c> and <c>UiTreeValidator</c>.</param>
/// <param name="Icon">SVG path data of the plugin's navigation icon (validated by the service), if it declares one.</param>
public sealed record UiContributionDto(string PluginId, string ContributionId, string? Title, string Json, string? Icon = null);

/// <summary>Result of <see cref="IpcMethods.UiGetContributions"/>.</summary>
/// <param name="Slot">Requested slot.</param>
/// <param name="Items">Contributions in display order.</param>
public sealed record GetContributionsResponse(UiSlot Slot, IReadOnlyList<UiContributionDto> Items);

/// <summary>Parameters of <see cref="IpcMethods.UiSendAction"/>.</summary>
/// <param name="PluginId">Owning plugin.</param>
/// <param name="ContributionId">Contribution the user interacted with.</param>
/// <param name="Action">The interaction; validated like any untrusted input.</param>
public sealed record SendActionRequest(string PluginId, string ContributionId, UiAction Action);

// ---- agent ----

/// <summary>Parameters of <see cref="IpcMethods.AgentRegister"/>.</summary>
/// <param name="ProtocolVersion">Protocol version the agent speaks.</param>
/// <param name="SessionId">Terminal-services session id the agent runs in.</param>
public sealed record AgentRegisterRequest(int ProtocolVersion, int SessionId);

/// <summary>Result of <see cref="IpcMethods.AgentRegister"/>.</summary>
/// <param name="Accepted">Whether the service accepted the agent.</param>
/// <param name="ProtocolVersion">Protocol version of the service.</param>
public sealed record AgentRegisterResponse(bool Accepted, int ProtocolVersion);

/// <summary>Foreground window report (<see cref="IpcMethods.AgentForegroundWindow"/>). Window titles are deliberately not transmitted.</summary>
/// <param name="ProcessId">Owning process.</param>
/// <param name="StartTimeTicks">Process creation time (FILETIME ticks); identity together with the pid.</param>
/// <param name="ImagePath">Executable path, if known.</param>
public sealed record ForegroundWindowDto(int ProcessId, long StartTimeTicks, string? ImagePath);

/// <summary>Idle report (<see cref="IpcMethods.AgentIdleChanged"/>).</summary>
/// <param name="IsIdle">Whether the user is idle.</param>
/// <param name="IdleSeconds">Seconds since the last input.</param>
public sealed record IdleChangedDto(bool IsIdle, int IdleSeconds);

/// <summary>Parameters of <see cref="IpcMethods.AgentCloseProcess"/> (service to agent).</summary>
/// <param name="ProcessId">Process whose windows to close.</param>
/// <param name="StartTimeTicks">Process creation time ticks.</param>
/// <param name="TimeoutMilliseconds">How long to wait for the process to exit.</param>
public sealed record CloseProcessRequest(int ProcessId, long StartTimeTicks, int TimeoutMilliseconds);

/// <summary>Result of <see cref="IpcMethods.AgentCloseProcess"/>.</summary>
/// <param name="Closed">Whether the process exited in time.</param>
public sealed record CloseProcessResponse(bool Closed);

/// <summary>Severity of a notification.</summary>
public enum NotificationLevel
{
    /// <summary>Informational.</summary>
    Info,

    /// <summary>Needs attention.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>A toast (<see cref="IpcMethods.AgentNotify"/>) or pushed notification (<see cref="IpcMethods.EventNotification"/>).</summary>
/// <param name="Title">Short title.</param>
/// <param name="Message">Body text, plain text only.</param>
/// <param name="Level">Severity.</param>
public sealed record NotificationDto(string Title, string Message, NotificationLevel Level);

// ---- stub / plugin host / events ----

/// <summary>Parameters of <see cref="IpcMethods.StubGetBlockInfo"/>.</summary>
/// <param name="BlockId">Identifier passed to the block screen on launch.</param>
public sealed record BlockInfoRequest(string BlockId);

/// <summary>Result of <see cref="IpcMethods.StubGetBlockInfo"/>.</summary>
/// <param name="Subject">What was blocked (application or domain).</param>
/// <param name="Status">Session state.</param>
/// <param name="ShowNotice">Whether the user wants the block notice; when false the block screen closes without showing anything.</param>
public sealed record BlockInfoResponse(string Subject, SessionStatusDto Status, bool ShowNotice = true);

/// <summary>Parameters of <see cref="IpcMethods.PluginHostRegister"/>.</summary>
/// <param name="ProtocolVersion">Protocol version the host speaks.</param>
/// <param name="Token">Launch token the service passed on the command line (<c>--token</c>); lets the service tell its hosts apart.</param>
public sealed record PluginHostRegisterRequest(int ProtocolVersion, string? Token = null);

/// <summary>Result of <see cref="IpcMethods.EventsSubscribe"/>; the current status is returned so the subscriber needs no extra call.</summary>
/// <param name="Status">Current session state.</param>
public sealed record SubscribeResponse(SessionStatusDto Status);
