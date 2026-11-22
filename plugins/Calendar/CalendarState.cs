// In-memory UI state shared by the page, the widgets and the settings: which view and day are shown, what is selected, the pending block
// and the editor draft, the last message and the armed (first-click) destructive button. Nothing here is persisted.

namespace CalendarPlanner;

/// <summary>Which calendar view the page shows.</summary>
public enum CalendarMode
{
    /// <summary>One day as a time grid.</summary>
    Day = 0,

    /// <summary>Seven days as a time grid.</summary>
    Week = 1,

    /// <summary>The month as a grid of days with the titles of their entries.</summary>
    Month = 2,
}

/// <summary>A message shown above the calendar until the next action.</summary>
/// <param name="Text">The text.</param>
/// <param name="IsError">Whether it is an error.</param>
public sealed record CalendarMessage(string Text, bool IsError);

/// <summary>One occurrence of an entry: the entry and the day it has in its series.</summary>
/// <param name="EntryId">Entry id.</param>
/// <param name="OriginalDay">Series day of the occurrence.</param>
public readonly record struct OccurrenceRef(int EntryId, int OriginalDay);

/// <summary>A new block the user dragged on the grid and has not saved yet.</summary>
/// <param name="Day">Day number.</param>
/// <param name="Start">Start, minutes after midnight.</param>
/// <param name="End">End, minutes after midnight.</param>
public sealed record PendingBlock(int Day, int Start, int End);

/// <summary>Thread-safe UI state.</summary>
public sealed class CalendarUiState
{
    /// <summary>How long a destructive button waits for its confirming second click.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private CalendarMode _mode = CalendarMode.Week;
    private int? _anchor;
    private IReadOnlyList<OccurrenceRef> _selection = [];
    private string? _anchorId;
    private PendingBlock? _pending;
    private CalendarDraft? _draft;
    private bool _showMore;
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

    /// <summary>A day of the shown day, week or month, or <see langword="null"/> for today.</summary>
    public int? Anchor => Read(() => _anchor);

    /// <summary>The selected occurrences in the order they were picked; the last one carries the popover.</summary>
    public IReadOnlyList<OccurrenceRef> Selection => Read(() => _selection);

    /// <summary>Id of the grid block the user clicked last (where the popover is anchored), or <see langword="null"/>.</summary>
    public string? AnchorId => Read(() => _anchorId);

    /// <summary>The block being created, or <see langword="null"/>.</summary>
    public PendingBlock? Pending => Read(() => _pending);

    /// <summary>The editor draft, or <see langword="null"/> when no editor is open.</summary>
    public CalendarDraft? Draft => Read(() => _draft);

    /// <summary>Whether the editor shows its extra fields.</summary>
    public bool ShowMore => Read(() => _showMore);

    /// <summary>The message.</summary>
    public CalendarMessage? Message => Read(() => _message);

    /// <summary>Any day of the month the widget shows, or <see langword="null"/> for the current month.</summary>
    public int? WidgetMonth => Read(() => _widgetMonth);

    /// <summary>The day selected in the widget, or <see langword="null"/> for today.</summary>
    public int? WidgetSelected => Read(() => _widgetSelected);

    /// <summary>Shows another period: sets the view and the day and drops the selection, the pending block, the editor and the message.</summary>
    public void Show(CalendarMode mode, int? anchor) => Write(() =>
    {
        _mode = mode;
        _anchor = anchor;
        DropTransient();
        _message = null;
    });

    /// <summary>Drops the selection, the pending block, the editor, the armed button and the message (the popover closes).</summary>
    public void Dismiss() => Write(() =>
    {
        DropTransient();
        _message = null;
    });

    /// <summary>Selects occurrences; the popover is anchored to <paramref name="anchorId"/>. Closes the editor and the pending block.</summary>
    public void Select(IReadOnlyList<OccurrenceRef> selection, string? anchorId) => Write(() =>
    {
        DropTransient();
        _selection = selection;
        _anchorId = selection.Count == 0 ? null : anchorId;
        _message = null;
    });

    /// <summary>Starts a new block: the ghost on the grid and the editor for it.</summary>
    public void BeginCreate(PendingBlock pending, CalendarDraft draft) => Write(() =>
    {
        DropTransient();
        _pending = pending;
        _draft = draft;
        _message = null;
    });

    /// <summary>Opens the editor for the selected occurrence.</summary>
    public void BeginEdit(CalendarDraft draft) => Write(() =>
    {
        _draft = draft;
        _showMore = false;
        _armed = null;
        _message = null;
    });

    /// <summary>Replaces the draft of the open editor (after a refused save, so typed values stay).</summary>
    public void SetDraft(CalendarDraft draft) => Write(() => _draft = draft);

    /// <summary>Shows or hides the extra fields of the editor.</summary>
    public void SetShowMore(bool value) => Write(() => _showMore = value);

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

    private void DropTransient()
    {
        _selection = [];
        _anchorId = null;
        _pending = null;
        _draft = null;
        _showMore = false;
        _armed = null;
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
