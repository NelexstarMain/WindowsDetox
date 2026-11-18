using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;
using Blocker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.Stub;

/// <summary>One validated plugin block-screen element.</summary>
/// <param name="PluginId">Owning plugin.</param>
/// <param name="ContributionId">Contribution id.</param>
/// <param name="Node">Validated tree.</param>
internal sealed record BlockContribution(string PluginId, string ContributionId, UiNode Node);

/// <summary>A user interaction with a block-screen element.</summary>
/// <param name="Contribution">The element's contribution.</param>
/// <param name="Action">The interaction.</param>
internal sealed record BlockContributionAction(BlockContribution Contribution, UiAction Action);

/// <summary>
/// The block screen: what was blocked and why, the live session countdown, the emergency exit (inline confirmation, no dialog) and plugin
/// content. If the service cannot be reached it says so neutrally and asks to close itself after <see cref="UnreachableCloseDelay"/>.
/// </summary>
internal sealed partial class BlockScreenViewModel : ObservableObject
{
    /// <summary>How long the window stays when the service is unreachable.</summary>
    public static readonly TimeSpan UnreachableCloseDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PassStartedCloseDelay = TimeSpan.FromSeconds(3);

    private readonly IStubService _service;
    private readonly StubArguments _arguments;
    private readonly TimeProvider _time;
    private readonly SessionTimeline _timeline;
    private long? _closeAt;

    [ObservableProperty]
    private string _subject;

    [ObservableProperty]
    private string _clockText = DurationFormat.NoClock;

    [ObservableProperty]
    private string _emergencyLabel = "Use emergency exit";

    [ObservableProperty]
    private bool _isUnreachable;

    [ObservableProperty]
    private bool _isConfirming;

    [ObservableProperty]
    private bool _canUseEmergencyExit;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _sessionIsRunning;

    /// <summary>Creates the view model.</summary>
    /// <param name="service">Service connection.</param>
    /// <param name="arguments">What the stub was started for.</param>
    /// <param name="ticker">Countdown tick.</param>
    /// <param name="time">Time source.</param>
    public BlockScreenViewModel(IStubService service, StubArguments arguments, IStubTicker ticker, TimeProvider time)
    {
        _service = service;
        _arguments = arguments;
        _time = time;
        _timeline = new SessionTimeline(time);
        _subject = arguments.BlockedPath ?? "An app";
        ReasonText = arguments.DescribeReason();
        service.SessionChanged += (_, snapshot) =>
        {
            _timeline.Update(snapshot);
            Refresh();
        };
        ticker.Tick += (_, _) => Refresh();
    }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Gets the path of the blocked executable, if known.</summary>
    public string? BlockedPath => _arguments.BlockedPath;

    /// <summary>Gets the name shown for the blocked app: the file name without extension.</summary>
    public string AppName => BlockedPath is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : Subject;

    /// <summary>Gets the reason in plain words.</summary>
    public string ReasonText { get; }

    /// <summary>Gets the plugin block-screen elements.</summary>
    public ObservableCollection<BlockContribution> Contributions { get; } = [];

    /// <summary>Gets whether any plugin element is shown.</summary>
    public bool HasContributions => Contributions.Count > 0;

    /// <summary>Gets the neutral message shown when the service cannot be reached.</summary>
    public string UnreachableMessage => "Blocker isn't answering right now, so it can't show session details. This window closes by itself in a few seconds.";

    /// <summary>Loads block info, subscribes to updates and loads plugin content. Call once after the window is shown.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the first load is done.</returns>
    [RelayCommand]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            BlockInfoResponse info = await _service.GetBlockInfoAsync(_arguments.BlockId, cancellationToken);
            Subject = info.Subject;
            _timeline.Update(info.Status.ToSnapshot());
            Refresh();
            GetContributionsResponse contributions = await _service.GetContributionsAsync(cancellationToken);
            Synchronize(contributions.Items);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            IsUnreachable = true;
            _closeAt = _time.GetTimestamp() + (long)(UnreachableCloseDelay.TotalSeconds * _time.TimestampFrequency);
        }
    }

    /// <summary>Recomputes the clock and the emergency exit state; closes the window when a scheduled close is due.</summary>
    public void Refresh()
    {
        SessionSnapshot snapshot = _timeline.Snapshot;
        SessionIsRunning = snapshot.IsSessionRunning;
        ClockText = DurationFormat.Clock(_timeline.Remaining);
        EmergencyLabel = string.Create(CultureInfo.InvariantCulture, $"Use emergency exit ({snapshot.EmergencyPassesLeftToday} left)");
        CanUseEmergencyExit = !IsUnreachable && snapshot.Phase == SessionPhase.Active && snapshot.EmergencyPassesLeftToday > 0;
        UseEmergencyExitCommand.NotifyCanExecuteChanged();

        if (_closeAt is { } due && _time.GetTimestamp() >= due)
        {
            _closeAt = null;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseEmergencyExit))]
    private void UseEmergencyExit() => IsConfirming = true;

    [RelayCommand]
    private void CancelEmergencyExit() => IsConfirming = false;

    [RelayCommand]
    private async Task ConfirmEmergencyExitAsync(CancellationToken cancellationToken)
    {
        IsConfirming = false;
        try
        {
            EmergencyPassResponse response = await _service.UseEmergencyPassAsync(cancellationToken);
            _timeline.Update(response.Status.ToSnapshot());
            Refresh();
            if (response.Outcome == EmergencyPassOutcome.Started)
            {
                StatusMessage = "Blocking is paused. Open the app again; this window closes now.";
                _closeAt = _time.GetTimestamp() + (long)(PassStartedCloseDelay.TotalSeconds * _time.TimestampFrequency);
            }
            else
            {
                StatusMessage = response.Outcome switch
                {
                    EmergencyPassOutcome.NoPassesLeft => "No emergency passes left today.",
                    EmergencyPassOutcome.AlreadyActive => "An emergency pass is already running.",
                    _ => "No session is running.",
                };
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            StatusMessage = "The emergency exit could not reach Blocker. Try again.";
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task SendActionAsync(BlockContributionAction? action, CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return;
        }

        try
        {
            await _service.SendActionAsync(action.Contribution.PluginId, action.Contribution.ContributionId, action.Action, cancellationToken);
            Synchronize((await _service.GetContributionsAsync(cancellationToken)).Items);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            StatusMessage = "That plugin action could not be sent.";
        }
    }

    private static bool IsExpected(Exception ex) => ex is IpcException or IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException;

    private void Synchronize(IEnumerable<UiContributionDto> items)
    {
        Contributions.Clear();
        foreach (UiContributionDto dto in items)
        {
            UiNode node;
            try
            {
                node = UiJson.Deserialize(dto.Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (UiTreeValidator.Validate(node).IsValid)
            {
                Contributions.Add(new BlockContribution(dto.PluginId, dto.ContributionId, node));
            }
        }

        OnPropertyChanged(nameof(HasContributions));
    }
}
