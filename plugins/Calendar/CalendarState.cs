// In-memory UI state shared by the page, the widgets and the settings: which view and day are shown, the form draft, the last message and
// the armed (first-click) destructive button. Nothing here is persisted.

namespace CalendarPlanner;

/// <summary>Which calendar view the page shows.</summary>
public enum CalendarMode
{
    /// <summary>Month grid with the selected day's agenda.</summary>
    Month = 0,

    /// <summary>Seven days of the week.</summary>
    Week = 1,

    /// <summary>The next days as a list.</summary>
    Agenda = 2,
}

/// <summary>A message shown above the tiles until dismissed.</summary>
/// <param name="Text">The text.</param>
/// <param name="IsError">Whether it is an error.</param>
public sealed record CalendarMessage(string Text, bool IsError);

/// <summary>Thread-safe UI state.</summary>
public sealed class CalendarUiState
{
    /// <summary>How long a destructive button waits for its confirming second click.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private CalendarMode _mode;
    private int? _anchor;
    private int? _selected;
    private CalendarDraft? _draft;
    private CalendarMessage? _message;
    private string? _armed;
    private long _armedAt;
    private int? _widgetMonth;
    private int? _widgetSelected;

    /// <summary>Creates the state.</summary>
    public CalendarUiState(TimeProvider time) => _time = time;

    /// <summary>Raised after every change.</summary>
    public event Action? Changed;

    /// <summary>The view.</summary>
    public CalendarMode Mode => Read(() => _mode);

    /// <summary>The day the visible month or week is anchored on, or <see langword="null"/> for today.</summary>
    public int? Anchor => Read(() => _anchor);

    /// <summary>The selected day, or <see langword="null"/> for today.</summary>
    public int? Selected => Read(() => _selected);

    /// <summary>The form draft being edited, or <see langword="null"/> for a fresh one.</summary>
    public CalendarDraft? Draft => Read(() => _draft);

    /// <summary>The message.</summary>
    public CalendarMessage? Message => Read(() => _message);

    /// <summary>Any day of the month the widget shows, or <see langword="null"/> for the current month.</summary>
    public int? WidgetMonth => Read(() => _widgetMonth);

    /// <summary>The day selected in the widget, or <see langword="null"/> for today.</summary>
    public int? WidgetSelected => Read(() => _widgetSelected);

    /// <summary>Switches the view.</summary>
    public void SetMode(CalendarMode mode) => Write(() => _mode = mode);

    /// <summary>Sets the anchor day.</summary>
    public void SetAnchor(int? day) => Write(() => _anchor = day);

    /// <summary>Selects a day.</summary>
    public void SetSelected(int? day) => Write(() => _selected = day);

    /// <summary>Sets the draft.</summary>
    public void SetDraft(CalendarDraft? draft) => Write(() => _draft = draft);

    /// <summary>Sets or clears the message.</summary>
    public void SetMessage(CalendarMessage? message) => Write(() => _message = message);

    /// <summary>Sets the widget's month.</summary>
    public void SetWidgetMonth(int? day) => Write(() => _widgetMonth = day);

    /// <summary>Selects a day in the widget.</summary>
    public void SetWidgetSelected(int? day) => Write(() => _widgetSelected = day);

    /// <summary>Arms a destructive action; a second <see cref="TryConfirm"/> with the same key within <see cref="ConfirmWindow"/> succeeds.</summary>
    public void Arm(string key) => Write(() =>
    {
        _armed = key;
        _armedAt = _time.GetTimestamp();
    });

    /// <summary>Whether <paramref name="key"/> is armed and still inside the confirm window.</summary>
    public bool IsArmed(string key) => Read(() => _armed == key && _time.GetElapsedTime(_armedAt) < ConfirmWindow);

    /// <summary>Consumes the armed state; <see langword="true"/> when <paramref name="key"/> was armed within the window.</summary>
    public bool TryConfirm(string key)
    {
        bool ok;
        lock (_gate)
        {
            ok = _armed == key && _time.GetElapsedTime(_armedAt) < ConfirmWindow;
            _armed = null;
        }

        Changed?.Invoke();
        return ok;
    }

    private T Read<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }

    private void Write(Action write)
    {
        lock (_gate)
        {
            write();
        }

        Changed?.Invoke();
    }
}
