using System.Collections.ObjectModel;
using System.Globalization;
using Blocker.Contracts;
using Blocker.Sdk.Session;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using Blocker.UI.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>
/// Session dashboard: live countdown, state chip, emergency pass card, break-glass entry and plugin widgets.
/// All times derive from the service's snapshots; the UI only interpolates between pushes.
/// </summary>
internal sealed partial class DashboardViewModel : ServiceViewModel
{
    /// <summary>Remaining pass time at or below which the pass card switches to its warning state.</summary>
    public static readonly TimeSpan PassWarningThreshold = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan WidgetRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IDialogService _dialogs;
    private readonly IAppDialogs _appDialogs;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;
    private readonly SessionTimeline _timeline;
    private long _widgetsRefreshedAt;
    private Guid? _trackedSession;
    private TimeSpan _longestRemaining;

    [ObservableProperty]
    private string _stateLabel = "Idle";

    [ObservableProperty]
    private string _clockText = DurationFormat.NoClock;

    [ObservableProperty]
    private bool _isSessionRunning;

    [ObservableProperty]
    private double _progressRatio;

    [ObservableProperty]
    private bool _isPassActive;

    [ObservableProperty]
    private bool _isPassWarning;

    [ObservableProperty]
    private string _passClockText = DurationFormat.NoClock;

    [ObservableProperty]
    private string _passesLeftText = "0 left today";

    [ObservableProperty]
    private string _passLengthText = string.Empty;

    [ObservableProperty]
    private string? _emergencyHint;

    [ObservableProperty]
    private bool _canUseEmergencyPass;

    [ObservableProperty]
    private string? _widgetsError;

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="dialogs">Confirmation dialogs.</param>
    /// <param name="appDialogs">Application dialogs.</param>
    /// <param name="snackbar">Snackbars.</param>
    /// <param name="navigator">Page navigation.</param>
    /// <param name="ticker">Countdown tick.</param>
    /// <param name="time">Time source.</param>
    /// <param name="board">The user's widget layout.</param>
    public DashboardViewModel(
        IServiceConnection connection,
        IDialogService dialogs,
        IAppDialogs appDialogs,
        ISnackbarService snackbar,
        INavigator navigator,
        IUiTicker ticker,
        TimeProvider time,
        WidgetBoardViewModel board)
        : base(connection, snackbar)
    {
        _dialogs = dialogs;
        _appDialogs = appDialogs;
        _navigator = navigator;
        _time = time;
        Board = board;
        _timeline = new SessionTimeline(time);
        _widgetsRefreshedAt = time.GetTimestamp();

        connection.SessionChanged += OnSessionChanged;
        connection.ConnectionChanged += OnConnectionChanged;
        ticker.Tick += OnTick;
        _timeline.Update(connection.Session);
        Refresh();
        if (connection.IsConnected)
        {
            _ = RefreshWidgetsAsync(CancellationToken.None);
        }
    }

    /// <summary>Gets every dashboard widget the plugins offer.</summary>
    public ObservableCollection<ContributionViewModel> Widgets { get; } = [];

    /// <summary>Gets the widgets the user put on the dashboard.</summary>
    public WidgetBoardViewModel Board { get; }

    /// <summary>Gets whether no session is running.</summary>
    public bool IsIdle => !IsSessionRunning;

    /// <summary>Gets whether the service is unreachable.</summary>
    public bool IsDisconnected => !Connection.IsConnected;

    /// <summary>Recomputes every displayed value from the timeline. Called by pushes and by the ticker.</summary>
    public void Refresh()
    {
        SessionSnapshot snapshot = _timeline.Snapshot;
        IsSessionRunning = snapshot.IsSessionRunning;
        StateLabel = snapshot.Phase switch
        {
            SessionPhase.Active => "Active",
            SessionPhase.EmergencyPass => "Emergency pass",
            _ => "Idle",
        };
        ClockText = DurationFormat.Clock(_timeline.Remaining);
        ProgressRatio = ComputeProgress(snapshot, _timeline.Remaining);

        TimeSpan? passRemaining = snapshot.Phase == SessionPhase.EmergencyPass ? _timeline.PassRemaining : null;
        IsPassActive = passRemaining is not null;
        PassClockText = DurationFormat.Clock(passRemaining);
        IsPassWarning = passRemaining is { } left && left <= PassWarningThreshold;

        PassesLeftText = string.Create(CultureInfo.InvariantCulture, $"{snapshot.EmergencyPassesLeftToday} of {snapshot.EmergencyPassesPerDay} left today");
        PassLengthText = snapshot.EmergencyPassDuration > TimeSpan.Zero ? $"· {DurationFormat.Compact(snapshot.EmergencyPassDuration)} each" : string.Empty;

        EmergencyHint = DescribeUnavailability(snapshot);
        CanUseEmergencyPass = EmergencyHint is null;
        UseEmergencyPassCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsDisconnected));
    }

    /// <summary>Re-fetches the plugin dashboard widgets from the service.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the widgets are updated.</returns>
    [RelayCommand]
    public async Task RefreshWidgetsAsync(CancellationToken cancellationToken)
    {
        if (!Connection.IsConnected)
        {
            return;
        }

        _widgetsRefreshedAt = _time.GetTimestamp();
        try
        {
            GetContributionsResponse response = await Connection.GetContributionsAsync(UiSlot.DashboardWidget, cancellationToken);
            ContributionViewModel.Synchronize(Widgets, response.Items);
            Board.Update(Widgets);
            WidgetsError = null;
        }
        catch (Exception ex) when (ServiceFault.IsExpected(ex))
        {
            WidgetsError = ServiceFault.Describe(ex);
        }
    }

    /// <summary>Forwards a widget interaction to its plugin and refreshes the widgets.</summary>
    /// <param name="action">The interaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the plugin handled the action.</returns>
    [RelayCommand]
    private async Task SendWidgetActionAsync(ContributionAction? action, CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return;
        }

        bool sent = await TryAsync(
            async () => await Connection.SendActionAsync(action.Contribution.PluginId, action.Contribution.ContributionId, action.Action, cancellationToken),
            "Widget action failed");
        if (sent)
        {
            await RefreshWidgetsAsync(cancellationToken);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseEmergencyPass))]
    private async Task UseEmergencyPassAsync(CancellationToken cancellationToken)
    {
        SessionSnapshot snapshot = _timeline.Snapshot;
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"Blocking pauses for {DurationFormat.Compact(snapshot.EmergencyPassDuration)}. This uses one of your {snapshot.EmergencyPassesLeftToday} remaining passes today. You get a warning 60 seconds before blocking resumes.");
        if (!await _dialogs.ConfirmAsync("Use an emergency pass?", message, "Pause blocking", "Keep blocking", cancellationToken))
        {
            return;
        }

        EmergencyPassResponse? response = null;
        bool done = await TryAsync(async () => response = await Connection.UseEmergencyPassAsync(cancellationToken), "Emergency pass failed");
        if (!done || response is null)
        {
            return;
        }

        _timeline.Update(response.Status.ToSnapshot());
        Refresh();
        switch (response.Outcome)
        {
            case EmergencyPassOutcome.Started:
                Snackbar.Show("Emergency pass started", "Blocking is paused. Pause ends automatically.", NotificationSeverity.Warning);
                break;
            case EmergencyPassOutcome.NoPassesLeft:
                Snackbar.Show("No passes left", "You have used all emergency passes for today.", NotificationSeverity.Error);
                break;
            case EmergencyPassOutcome.AlreadyActive:
                Snackbar.Show("Pass already running", "An emergency pass is already active.", NotificationSeverity.Info);
                break;
            default:
                Snackbar.Show("No session running", "Emergency passes only apply during a session.", NotificationSeverity.Info);
                break;
        }
    }

    [RelayCommand]
    private async Task EnterBreakGlassCodeAsync(CancellationToken cancellationToken)
    {
        string? code = await _appDialogs.PromptBreakGlassCodeAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        BreakGlassResponse? response = null;
        bool done = await TryAsync(async () => response = await Connection.BreakGlassAsync(code.Trim(), cancellationToken), "Break-glass failed");
        if (!done)
        {
            return;
        }

        if (response is { Success: true })
        {
            Snackbar.Show("Session ended", "Everything is unlocked again.", NotificationSeverity.Success);
        }
        else
        {
            Snackbar.Show("Code not accepted", "Check the code and try again.", NotificationSeverity.Error);
        }
    }

    /// <summary>Elapsed fraction (0 to 1) of the running session, measured against the longest remaining time seen for it.</summary>
    private double ComputeProgress(SessionSnapshot snapshot, TimeSpan? remaining)
    {
        if (!snapshot.IsSessionRunning || remaining is not { } left)
        {
            _trackedSession = null;
            _longestRemaining = TimeSpan.Zero;
            return 0;
        }

        if (_trackedSession != snapshot.SessionId)
        {
            _trackedSession = snapshot.SessionId;
            _longestRemaining = TimeSpan.Zero;
        }

        if (left > _longestRemaining)
        {
            _longestRemaining = left;
        }

        return _longestRemaining <= TimeSpan.Zero ? 0 : Math.Clamp(1 - (left / _longestRemaining), 0, 1);
    }

    [RelayCommand]
    private void OpenAllowlist() => _navigator.GoTo(AppPage.Allowlist);

    [RelayCommand]
    private void StartSession() => _navigator.GoTo(AppPage.Setup);

    private string? DescribeUnavailability(SessionSnapshot snapshot) => true switch
    {
        _ when !Connection.IsConnected => "Reconnect to the service to use an emergency pass.",
        _ when snapshot.Phase == SessionPhase.Idle => "Emergency passes apply while a session is running.",
        _ when snapshot.Phase == SessionPhase.EmergencyPass => "A pass is already running.",
        _ when snapshot.EmergencyPassesLeftToday <= 0 => "No passes left today. They renew at the start of the next day.",
        _ => null,
    };

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        _timeline.Update(Connection.Session);
        Refresh();
        _ = RefreshWidgetsAsync(CancellationToken.None);
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        Refresh();
        if (Connection.IsConnected)
        {
            _ = RefreshWidgetsAsync(CancellationToken.None);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Refresh();
        if (_time.GetElapsedTime(_widgetsRefreshedAt) >= WidgetRefreshInterval)
        {
            _ = RefreshWidgetsAsync(CancellationToken.None);
        }
    }
}
