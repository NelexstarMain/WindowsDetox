using Blocker.Contracts;
using Blocker.Sdk.Session;
using Blocker.UI.Services;

namespace Blocker.UI.Render;

/// <summary>
/// The UI's service connection without a service: canned answers for the IPC methods the view models call, plugin contributions
/// and actions served by the in-process <see cref="PluginWorld"/>, and a session state the scenario sets.
/// </summary>
internal sealed class StubServiceConnection : IServiceConnection
{
    private static readonly Guid SessionId = new("5e5510a0-0000-4000-8000-00000000d0e5");

    private static readonly InstalledAppDto[] InstalledApps =
    [
        App("Visual Studio Code", @"C:\Users\Anna\AppData\Local\Programs\Microsoft VS Code\Code.exe", 'V', 0x2F7FD0, AppSourceDto.StartMenu | AppSourceDto.Registry | AppSourceDto.Running),
        App("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", 'E', 0x1B9E8C, AppSourceDto.StartMenu | AppSourceDto.Registry | AppSourceDto.Running),
        App("Windows Terminal", null, 'T', 0x4D4D5C, AppSourceDto.StartMenu | AppSourceDto.StorePackage, "Microsoft.WindowsTerminal_8wekyb3d8bbwe"),
        App("Obsidian", @"C:\Users\Anna\AppData\Local\Obsidian\Obsidian.exe", 'O', 0x7C5CD6, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("Microsoft Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", 'W', 0x2B579A, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("Microsoft Excel", @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE", 'X', 0x217346, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", 'F', 0xE66000, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("Notepad", @"C:\Windows\System32\notepad.exe", 'N', 0x5A6A7A, AppSourceDto.StartMenu),
        App("Calculator", null, 'C', 0x3A7A5C, AppSourceDto.StartMenu | AppSourceDto.StorePackage, "Microsoft.WindowsCalculator_8wekyb3d8bbwe"),
        App("Slack", @"C:\Users\Anna\AppData\Local\slack\slack.exe", 'S', 0x611F69, AppSourceDto.StartMenu | AppSourceDto.Registry | AppSourceDto.Running),
        App("Discord", @"C:\Users\Anna\AppData\Local\Discord\app-1.0.9172\Discord.exe", 'D', 0x5865F2, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("Spotify", @"C:\Users\Anna\AppData\Roaming\Spotify\Spotify.exe", 'S', 0x1DB954, AppSourceDto.StartMenu | AppSourceDto.Registry | AppSourceDto.Running),
        App("Steam", @"C:\Program Files (x86)\Steam\steam.exe", 'S', 0x1B2838, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("Zoom", @"C:\Users\Anna\AppData\Roaming\Zoom\bin\Zoom.exe", 'Z', 0x2D8CFF, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("Figma", @"C:\Users\Anna\AppData\Local\Figma\Figma.exe", 'F', 0xA259FF, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("7-Zip File Manager", @"C:\Program Files\7-Zip\7zFM.exe", '7', 0x3A4A5A, AppSourceDto.StartMenu | AppSourceDto.Registry),
        App("Windows Media Player", null, 'M', 0x0F6CBD, AppSourceDto.StartMenu | AppSourceDto.StorePackage, "Microsoft.ZuneMusic_8wekyb3d8bbwe"),
        App("Paint", @"C:\Windows\System32\mspaint.exe", 'P', 0xD9534F, AppSourceDto.StartMenu),
    ];

    private readonly Func<PluginWorld> _world;
    private AppSetsDto _sets = SeedSets();
    private AppRulesDto _rules = SeedRules();

    public StubServiceConnection(Func<PluginWorld> world) => _world = world;

    public event EventHandler? ConnectionChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? SessionChanged;

    public event EventHandler<NotificationEventArgs>? NotificationReceived
    {
        add { }
        remove { }
    }

    public bool IsConnected => true;

    public SessionSnapshot Session { get; private set; } = SessionSnapshot.Idle;

    /// <summary>Sets the session the dashboard shows. An active session is pushed twice so its progress bar is partly filled.</summary>
    /// <param name="kind"><c>idle</c>, <c>active</c> or <c>pass</c>.</param>
    public void SetSession(string kind)
    {
        TimeSpan pass = TimeSpan.FromMinutes(10);
        switch (kind)
        {
            case "active":
                Push(new SessionSnapshot(SessionPhase.Active, SessionId, TimeSpan.FromHours(2), null, 2, 2, pass));
                Push(new SessionSnapshot(SessionPhase.Active, SessionId, TimeSpan.FromMinutes(72), null, 2, 2, pass));
                break;
            case "pass":
                Push(new SessionSnapshot(SessionPhase.Active, SessionId, TimeSpan.FromHours(2), null, 2, 2, pass));
                Push(new SessionSnapshot(SessionPhase.EmergencyPass, SessionId, TimeSpan.FromMinutes(64), TimeSpan.FromSeconds(48), 1, 2, pass));
                break;
            default:
                Push(SessionSnapshot.Idle);
                break;
        }
    }

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken)
    {
        object response = method switch
        {
            IpcMethods.UiGetContributions => await _world().RenderSlotAsync(((GetContributionsRequest)(object)request!).Slot, cancellationToken),
            IpcMethods.UiSendAction => await SendAsync((SendActionRequest)(object)request!, cancellationToken),
            IpcMethods.AllowlistSet => new OperationResponse(true, []),
            IpcMethods.AppSetsSet => SaveSets((AppSetsDto)(object)request!),
            IpcMethods.AppRulesSet => SaveRules((AppRulesDto)(object)request!),
            IpcMethods.SessionStart => new StartSessionResponse(true, [], SessionId, "WD7K-9QXM-2PLA-4HTR"),
            _ => throw new IpcException(IpcErrorCode.NotFound, $"The render harness has no answer for {method}."),
        };
        return (TResponse)response;
    }

    public ValueTask<TResponse> InvokeAsync<TResponse>(string method, CancellationToken cancellationToken)
    {
        object response = method switch
        {
            IpcMethods.PluginsList => new ListPluginsResponse(PluginList()),
            IpcMethods.AllowlistScanApps => new ScanAppsResponse(InstalledApps),
            IpcMethods.AllowlistGet => StoredAllowlist(),
            IpcMethods.AppSetsGet => _sets,
            IpcMethods.AppRulesGet => _rules,
            IpcMethods.AppRulesStatus => new AppRulesStatusResponse([.. _rules.Rules.Select(RuleStatus)]),
            IpcMethods.SettingsGet => new SettingsDto(new Dictionary<string, string>()),
            _ => throw new IpcException(IpcErrorCode.NotFound, $"The render harness has no answer for {method}."),
        };
        return ValueTask.FromResult((TResponse)response);
    }

    private static AllowlistDto StoredAllowlist() => new(
        [.. InstalledApps.Where(a => a.DisplayName is "Visual Studio Code" or "Microsoft Edge" or "Windows Terminal" or "Obsidian" or "Microsoft Word")
            .Select(a => new AllowlistEntryDto(a.DisplayName, a.ExecutablePath, a.Sha256, a.PackageFamilyName))],
        ["github.com", "learn.microsoft.com", "*.stackoverflow.com", "docs.rs"]);

    private static AllowlistEntryDto Entry(string name)
    {
        InstalledAppDto app = InstalledApps.First(a => a.DisplayName == name);
        return new AllowlistEntryDto(app.DisplayName, app.ExecutablePath, app.Sha256, app.PackageFamilyName);
    }

    private static AppSetsDto SeedSets() => new(
    [
        new AppSetDto("Daily setup", [.. new[] { "Visual Studio Code", "Microsoft Edge", "Windows Terminal", "Obsidian", "Microsoft Word" }.Select(Entry)], ["github.com", "learn.microsoft.com", "*.stackoverflow.com", "docs.rs"]),
        new AppSetDto("Deep work", [Entry("Visual Studio Code"), Entry("Windows Terminal"), Entry("Obsidian")], ["github.com", "docs.rs"]),
        new AppSetDto("Writing", [Entry("Obsidian"), Entry("Microsoft Word")], []),
    ]);

    // Discord is blocked on weekdays 9-17 (it is Monday 10:30 in every render), Spotify has a 60 minute pool, Steam a weekly one.
    private static AppRulesDto SeedRules() => new(
    [
        new AppRuleDto(
            Entry("Discord"),
            [.. Enumerable.Range(0, 5).Select(day => new WeeklyRangeDto(day, 9 * 60, 17 * 60))],
            new AppBudgetDto(AppBudgetMode.None, 0, null, 0)),
        new AppRuleDto(Entry("Spotify"), [], new AppBudgetDto(AppBudgetMode.Daily, 60, null, 0)),
        new AppRuleDto(Entry("Steam"), [new WeeklyRangeDto(5, 10 * 60, 12 * 60)], new AppBudgetDto(AppBudgetMode.Weekly, 0, null, 300)),
    ]);

    private static AppRuleStatusDto RuleStatus(AppRuleDto rule) => rule.App.DisplayName switch
    {
        "Discord" => new AppRuleStatusDto(rule.App, AppRuleState.ScheduledBlock, 0, null, ScenarioRunner.Now.Date.AddHours(17)),
        "Spotify" => new AppRuleStatusDto(rule.App, AppRuleState.Allowed, 42 * 60, 18 * 60, null),
        _ => new AppRuleStatusDto(rule.App, AppRuleState.Allowed, 190 * 60, 110 * 60, null),
    };

    private OperationResponse SaveSets(AppSetsDto sets)
    {
        _sets = sets;
        return new OperationResponse(true, []);
    }

    private OperationResponse SaveRules(AppRulesDto rules)
    {
        _rules = rules;
        return new OperationResponse(true, []);
    }

    private static InstalledAppDto App(string name, string? path, char letter, int color, AppSourceDto sources, string? family = null) =>
        new(name, path, path is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))), family, AppIcons.Png(letter, color), sources);

    private List<PluginInfoDto> PluginList() =>
    [
        .. _world().Plugins.Select(p => new PluginInfoDto(p.Id, p.Name, p.Version, p.Author, true, p.Capabilities)),
        new PluginInfoDto("com.example.weekly-review", "Weekly Review", "0.3.2", "Jan Kowalski", false, ["ui.page", "ui.settings"]),
    ];

    private async ValueTask<EmptyDto> SendAsync(SendActionRequest request, CancellationToken cancellationToken)
    {
        await _world().SendActionAsync(request, cancellationToken);
        return new EmptyDto();
    }

    private void Push(SessionSnapshot snapshot)
    {
        Session = snapshot;
        _world().SetSession(snapshot);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }
}
