using System.Globalization;
using Blocker.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blocker.UI.ViewModels;

/// <summary>What decides about an app right now, as the list and the header show it.</summary>
internal enum LimitState
{
    /// <summary>The app has no rule.</summary>
    None,

    /// <summary>The app has a rule and may run.</summary>
    Allowed,

    /// <summary>A running session decides.</summary>
    SessionControlled,

    /// <summary>A scheduled block blocks the app.</summary>
    ScheduledBlock,

    /// <summary>The app used up its time pool.</summary>
    PoolExhausted,
}

/// <summary>One app of the App limits list: its icon and name, its rule and what the service says about it right now.</summary>
internal sealed partial class AppLimitItemViewModel : ObservableObject
{
    /// <summary>Group of apps that have a rule; listed first.</summary>
    public const string RuledGroup = "With limits";

    /// <summary>Group of apps without a rule.</summary>
    public const string OtherGroup = "Other apps";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRule), nameof(GroupName))]
    private AppRuleDto? _rule;

    [ObservableProperty]
    private AppRuleStatusDto? _status;

    [ObservableProperty]
    private string _statusText = "No limits";

    [ObservableProperty]
    private LimitState _state = LimitState.None;

    [ObservableProperty]
    private double _usageFraction;

    [ObservableProperty]
    private bool _hasPool;

    /// <summary>Creates the item.</summary>
    /// <param name="app">The application row (name, icon and identity).</param>
    public AppLimitItemViewModel(AppEntryViewModel app)
    {
        ArgumentNullException.ThrowIfNull(app);
        App = app;
    }

    /// <summary>Gets the application row.</summary>
    public AppEntryViewModel App { get; }

    /// <summary>Gets whether the app has a rule.</summary>
    public bool HasRule => Rule is not null;

    /// <summary>Gets the list group: apps with a rule are pinned above the others.</summary>
    public string GroupName => HasRule ? RuledGroup : OtherGroup;

    /// <summary>Takes the latest rule and status of the app and rewrites the status line.</summary>
    /// <param name="rule">The app's rule, if any.</param>
    /// <param name="status">What the service says about the rule, if anything.</param>
    /// <param name="now">The current time.</param>
    /// <param name="zone">The local time zone.</param>
    public void Update(AppRuleDto? rule, AppRuleStatusDto? status, DateTimeOffset now, TimeZoneInfo zone)
    {
        Rule = rule;
        Status = status;
        (StatusText, State) = Describe(rule, status, now, zone);
        HasPool = rule is { Budget.Mode: not AppBudgetMode.None } && status?.RemainingSeconds is not null;
        UsageFraction = status is { RemainingSeconds: { } remaining } && HasPool
            ? status.UsedSeconds + remaining <= 0 ? 1 : Math.Clamp((double)status.UsedSeconds / (status.UsedSeconds + remaining), 0, 1)
            : 0;
    }

    /// <summary>Words the live state of a rule for tooltips; the list and header show it with icons.</summary>
    /// <param name="rule">The rule, or <see langword="null"/> for an app without one.</param>
    /// <param name="status">The live state, or <see langword="null"/> when the service has none.</param>
    /// <param name="now">The current time.</param>
    /// <param name="zone">The local time zone.</param>
    /// <returns>The status line and the state behind it.</returns>
    public static (string Text, LimitState State) Describe(AppRuleDto? rule, AppRuleStatusDto? status, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (rule is null)
        {
            return ("No limits", LimitState.None);
        }

        switch (status?.State)
        {
            case AppRuleState.SessionControlled:
                return ("Session decides", LimitState.SessionControlled);
            case AppRuleState.ScheduledBlock:
                return (status.BlockedUntil is { } blockEnd ? $"Blocked until {WhenText(blockEnd, now, zone)}" : "Blocked", LimitState.ScheduledBlock);
            case AppRuleState.BudgetExhausted:
                return (status.BlockedUntil is { } poolEnd ? $"Time is up until {WhenText(poolEnd, now, zone)}" : "Time is up", LimitState.PoolExhausted);
        }

        if (status?.RemainingSeconds is { } remaining)
        {
            string period = rule.Budget.Mode == AppBudgetMode.Weekly ? "this week" : "today";
            return (remaining < 60 ? $"Under a minute left {period}" : $"{DurationFormat.Compact(TimeSpan.FromSeconds(remaining))} left {period}", LimitState.Allowed);
        }

        return ("Allowed now", LimitState.Allowed);
    }

    private static string WhenText(DateTimeOffset moment, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(moment, zone);
        DateTime today = TimeZoneInfo.ConvertTime(now, zone).Date;
        string time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == today
            ? time
            : local.Date == today.AddDays(1)
                ? $"tomorrow {time}"
                : string.Create(CultureInfo.CurrentCulture, $"{local:ddd} {time}");
    }
}
