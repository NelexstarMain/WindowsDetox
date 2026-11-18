using System.Globalization;
using System.Text;
using Blocker.Contracts;
using Blocker.Core.Session;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>A duration choice in the setup page. <see cref="Minutes"/> is <see langword="null"/> for the custom option.</summary>
/// <param name="Label">Text on the choice.</param>
/// <param name="Minutes">Length in minutes, or <see langword="null"/> for a custom length.</param>
internal sealed record DurationOption(string Label, int? Minutes)
{
    /// <summary>Gets whether this is the custom option.</summary>
    public bool IsCustom => Minutes is null;
}

/// <summary>Session and protection setup: how long, which emergency passes, and the confirmation flow that starts the session.</summary>
internal sealed partial class SetupViewModel : ServiceViewModel
{
    /// <summary>Largest value of the pass-count input, from the service's rules.</summary>
    public const int MaxPassesPerDay = SessionConfigValidator.MaxPassesPerDay;

    /// <summary>Largest value of the pass-length input, in minutes.</summary>
    public static readonly int MaxPassMinutes = (int)SessionConfigValidator.MaxPassDuration.TotalMinutes;

    private readonly IAllowlistSelection _selection;
    private readonly IDialogService _dialogs;
    private readonly IAppDialogs _appDialogs;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;

    [ObservableProperty]
    private DurationOption _selectedDuration;

    [ObservableProperty]
    private double _customHours = 3;

    [ObservableProperty]
    private double _customMinutes;

    [ObservableProperty]
    private double _passesPerDay = 2;

    [ObservableProperty]
    private double _passMinutes = 10;

    [ObservableProperty]
    private bool _carryOverUnusedPasses;

    [ObservableProperty]
    private string? _durationError;

    [ObservableProperty]
    private string? _passError;

    [ObservableProperty]
    private string _durationText = string.Empty;

    [ObservableProperty]
    private string _endsAtText = string.Empty;

    [ObservableProperty]
    private string _appsText = string.Empty;

    [ObservableProperty]
    private string _domainsText = string.Empty;

    [ObservableProperty]
    private string _passesText = string.Empty;

    [ObservableProperty]
    private string? _startBlockedReason;

    [ObservableProperty]
    private IReadOnlyList<string> _startErrors = [];

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="selection">Allowlist chosen on the allowlist page.</param>
    /// <param name="dialogs">Confirmation dialogs.</param>
    /// <param name="appDialogs">Application dialogs.</param>
    /// <param name="snackbar">Snackbars.</param>
    /// <param name="navigator">Page navigation.</param>
    /// <param name="ticker">Tick used to keep the end time current.</param>
    /// <param name="time">Time source.</param>
    public SetupViewModel(
        IServiceConnection connection,
        IAllowlistSelection selection,
        IDialogService dialogs,
        IAppDialogs appDialogs,
        ISnackbarService snackbar,
        INavigator navigator,
        IUiTicker ticker,
        TimeProvider time)
        : base(connection, snackbar)
    {
        _selection = selection;
        _dialogs = dialogs;
        _appDialogs = appDialogs;
        _navigator = navigator;
        _time = time;
        DurationOptions =
        [
            new("1 hour", 60),
            new("2 hours", 120),
            new("5 hours", 300),
            new("1 day", 1440),
            new("2 days", 2880),
            new("Custom", null),
        ];
        _selectedDuration = DurationOptions[1];

        selection.SelectionChanged += (_, _) => Refresh();
        connection.SessionChanged += (_, _) => Refresh();
        connection.ConnectionChanged += (_, _) => Refresh();
        ticker.Tick += (_, _) => RefreshEndsAt();
        Refresh();
    }

    /// <summary>Gets the duration choices.</summary>
    public IReadOnlyList<DurationOption> DurationOptions { get; }

    /// <summary>Gets whether the custom hours and minutes inputs are shown.</summary>
    public bool IsCustomDuration => SelectedDuration.IsCustom;

    /// <summary>Gets the session length in minutes, or 0 when the custom input is empty or invalid.</summary>
    public int DurationMinutes => SelectedDuration.Minutes ?? ToMinutes(CustomHours, CustomMinutes);

    /// <summary>Gets the emergency passes per day as an integer.</summary>
    public int PassesPerDayValue => (int)Math.Round(PassesPerDay);

    /// <summary>Gets the length of one emergency pass in minutes as an integer.</summary>
    public int PassMinutesValue => (int)Math.Round(PassMinutes);

    /// <summary>Gets whether the session can be started right now.</summary>
    public bool CanStart => StartBlockedReason is null;

    /// <summary>Builds the wire request from the current inputs and the allowlist.</summary>
    /// <returns>The request for <c>session.start</c>.</returns>
    public StartSessionRequest BuildRequest() => new(
        DurationMinutes,
        PassesPerDayValue,
        PassMinutesValue,
        CarryOverUnusedPasses,
        _selection.BuildSelectedEntries(),
        [.. _selection.Domains]);

    /// <summary>Builds the text of the confirmation dialog: what will be locked and for how long.</summary>
    /// <returns>Multi-line plain text.</returns>
    public string BuildConfirmationMessage()
    {
        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"For {DurationText}, until {EndsAtText.ToLower(CultureInfo.CurrentCulture)}, Blocker will lock:");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Every app except the {_selection.SelectedCount} you allowed");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Every website except {_selection.Domains.Count} allowed {(_selection.Domains.Count == 1 ? "domain" : "domains")}");
        text.AppendLine("  The allowlist, session settings and plugins");
        text.AppendLine();
        text.Append(PassesPerDayValue == 0
            ? "You will have no emergency passes. Only your break-glass code can end the session early."
            : string.Create(CultureInfo.InvariantCulture, $"You can end it early only with your break-glass code or by using one of {PassesPerDayValue} daily emergency passes."));
        if (_selection.SelectedCount == 0)
        {
            text.AppendLine().AppendLine().Append("No apps are allowed, so everything that is not part of Windows will be closed.");
        }

        return text.ToString();
    }

    partial void OnSelectedDurationChanged(DurationOption value) => Refresh();

    partial void OnCustomHoursChanged(double value) => Refresh();

    partial void OnCustomMinutesChanged(double value) => Refresh();

    partial void OnPassesPerDayChanged(double value) => Refresh();

    partial void OnPassMinutesChanged(double value) => Refresh();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        Refresh();
        if (!CanStart)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            string.Create(CultureInfo.InvariantCulture, $"Start a {DurationText} session?"),
            BuildConfirmationMessage(),
            "Start session",
            "Not yet",
            cancellationToken);
        if (!confirmed)
        {
            return;
        }

        StartSessionResponse? response = null;
        StartSessionRequest request = BuildRequest();
        bool done = await TryAsync(async () => response = await Connection.StartSessionAsync(request, cancellationToken), "Could not start the session");
        if (!done || response is null)
        {
            return;
        }

        if (!response.Success)
        {
            StartErrors = response.Errors;
            Snackbar.Show("Session not started", response.Errors.Count > 0 ? response.Errors[0] : "The service refused the request.", NotificationSeverity.Error);
            return;
        }

        StartErrors = [];
        string? code = response.BreakGlassCode;
        if (code is null)
        {
            Snackbar.Show("Session started", "The service did not return a break-glass code.", NotificationSeverity.Warning);
        }
        else
        {
            // The code exists only in this local until the user confirms; it is never stored or logged.
            await _appDialogs.ShowBreakGlassCodeAsync(code, CancellationToken.None);
        }

        _navigator.GoTo(AppPage.Dashboard);
    }

    private static int ToMinutes(double hours, double minutes) => (int)(Math.Round(hours) * 60 + Math.Round(minutes));

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsCustomDuration));
        OnPropertyChanged(nameof(DurationMinutes));
        OnPropertyChanged(nameof(PassesPerDayValue));
        OnPropertyChanged(nameof(PassMinutesValue));

        TimeSpan duration = TimeSpan.FromMinutes(DurationMinutes);
        DurationError = duration < SessionConfigValidator.MinDuration
            ? $"Choose at least {SessionConfigValidator.MinDuration.TotalMinutes:0} minutes."
            : duration > SessionConfigValidator.MaxDuration
                ? $"Choose {SessionConfigValidator.MaxDuration.TotalDays:0} days or less."
                : null;
        PassError = PassesPerDayValue is < 0 or > MaxPassesPerDay
            ? $"Passes per day must be between 0 and {MaxPassesPerDay}."
            : PassMinutesValue < 1 || PassMinutesValue > MaxPassMinutes
                ? $"A pass can last 1 to {MaxPassMinutes} minutes."
                : null;

        DurationText = DurationError is null ? DurationFormat.Compact(duration) : "unset";
        AppsText = AppCountText(_selection.SelectedCount);
        DomainsText = string.Create(CultureInfo.InvariantCulture, $"{_selection.Domains.Count} web {(_selection.Domains.Count == 1 ? "domain" : "domains")} allowed");
        PassesText = PassesPerDayValue == 0
            ? "No emergency passes"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{PassesPerDayValue} {(PassesPerDayValue == 1 ? "pass" : "passes")} a day, {PassMinutesValue} min each{(CarryOverUnusedPasses ? ", unused passes carry over" : string.Empty)}");
        RefreshEndsAt();

        StartBlockedReason = true switch
        {
            _ when !Connection.IsConnected => "Waiting for the Blocker service.",
            _ when Connection.Session.IsSessionRunning => "A session is already running.",
            _ when DurationError is not null => DurationError,
            _ when PassError is not null => PassError,
            _ => null,
        };
        OnPropertyChanged(nameof(CanStart));
        StartSessionCommand.NotifyCanExecuteChanged();
    }

    partial void OnCarryOverUnusedPassesChanged(bool value) => Refresh();

    private static string AppCountText(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? "app" : "apps")} allowed");

    private void RefreshEndsAt()
    {
        if (DurationError is not null)
        {
            EndsAtText = string.Empty;
            return;
        }

        DateTimeOffset now = _time.GetLocalNow();
        DateTimeOffset end = now.AddMinutes(DurationMinutes);
        EndsAtText = end.Date == now.Date
            ? $"Today at {end:HH:mm}"
            : end.Date == now.Date.AddDays(1)
                ? $"Tomorrow at {end:HH:mm}"
                : string.Create(CultureInfo.CurrentCulture, $"{end:ddd d MMM} at {end:HH:mm}");
    }
}
