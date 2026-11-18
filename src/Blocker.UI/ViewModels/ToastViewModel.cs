using System.Globalization;
using Blocker.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blocker.UI.ViewModels;

/// <summary>
/// One toast of the agent. When a countdown is given (the 60-second emergency-pass warning) the toast shows a live
/// <c>m:ss</c> countdown and closes itself when it reaches zero; other toasts close after a fixed time.
/// </summary>
internal sealed partial class ToastViewModel : ObservableObject
{
    /// <summary>How long a toast without a countdown stays on screen.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(8);

    private readonly TimeProvider _time;
    private readonly long _shownAt;
    private readonly TimeSpan _lifetime;
    private readonly TimeSpan? _countdown;

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    private bool _isExpired;

    /// <summary>Creates a toast and starts its clock.</summary>
    /// <param name="notification">Content received from the service.</param>
    /// <param name="countdown">Time left until blocking resumes, or <see langword="null"/> for a plain toast.</param>
    /// <param name="time">Time source.</param>
    public ToastViewModel(NotificationDto notification, TimeSpan? countdown, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Title = notification.Title;
        Message = notification.Message;
        IsWarning = notification.Level != NotificationLevel.Info;
        _time = time;
        _shownAt = time.GetTimestamp();
        _countdown = countdown;
        _lifetime = countdown is { } c ? c + TimeSpan.FromSeconds(1) : DefaultLifetime;
        Refresh();
    }

    /// <summary>Gets the title.</summary>
    public string Title { get; }

    /// <summary>Gets the message text.</summary>
    public string Message { get; }

    /// <summary>Gets whether the toast uses the warning (amber) style.</summary>
    public bool IsWarning { get; }

    /// <summary>Gets whether a countdown is shown.</summary>
    public bool HasCountdown => _countdown is not null;

    /// <summary>Updates the countdown and the expiry flag; call it from a timer.</summary>
    public void Refresh()
    {
        TimeSpan elapsed = _time.GetElapsedTime(_shownAt);
        if (_countdown is { } total)
        {
            TimeSpan left = total - elapsed;
            long seconds = (long)Math.Ceiling(Math.Max(0, left.TotalSeconds));
            CountdownText = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
        }

        IsExpired = elapsed >= _lifetime;
    }
}
