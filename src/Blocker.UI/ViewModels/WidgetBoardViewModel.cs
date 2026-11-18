using System.Collections.ObjectModel;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>
/// The user's dashboard widgets: which plugin widgets are shown, in which order and grid span. Until the user changes the board for
/// the first time, every available widget is shown; afterwards only the chosen ones are, and the choice is saved after every change.
/// </summary>
internal sealed partial class WidgetBoardViewModel : ObservableObject
{
    private readonly IWidgetLayoutStore _store;
    private readonly INavigator _navigator;
    private IReadOnlyList<ContributionViewModel> _available = [];
    private List<WidgetPlacement>? _layout;
    private bool _loaded;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string? _layoutError;

    /// <summary>Creates the board.</summary>
    /// <param name="store">Where the layout is saved.</param>
    /// <param name="navigator">Opens a widget's plugin page.</param>
    public WidgetBoardViewModel(IWidgetLayoutStore store, INavigator navigator)
    {
        _store = store;
        _navigator = navigator;
        Picker = new WidgetPickerViewModel(this);
    }

    /// <summary>Gets the widgets on the dashboard, in display order.</summary>
    public ObservableCollection<WidgetItemViewModel> Items { get; } = [];

    /// <summary>Gets the widget picker.</summary>
    public WidgetPickerViewModel Picker { get; }

    /// <summary>Gets the widgets the plugins currently offer.</summary>
    public IReadOnlyList<ContributionViewModel> Available => _available;

    /// <summary>Gets whether any plugin offers a widget.</summary>
    public bool HasAvailable => _available.Count > 0;

    /// <summary>Gets whether at least one widget is on the dashboard.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Gets whether to invite the user to add a first widget.</summary>
    public bool ShowEmptyState => HasAvailable && !HasItems;

    /// <summary>Applies the widgets the plugins currently offer; widgets whose plugin is gone are skipped.</summary>
    /// <param name="available">All offered widgets.</param>
    public void Update(IEnumerable<ContributionViewModel> available)
    {
        _available = [.. available];
        if (!_loaded)
        {
            _layout = _store.Load()?.ToList();
            _loaded = true;
        }

        IEnumerable<WidgetPlacement> wanted = _layout ?? _available.Select(WidgetSpans.DefaultPlacement);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<WidgetItemViewModel> next = [];
        foreach (WidgetPlacement placement in wanted)
        {
            ContributionViewModel? contribution = _available.FirstOrDefault(c => c.Key == placement.Key);
            if (contribution is null || !seen.Add(placement.Key))
            {
                continue;
            }

            next.Add(Items.FirstOrDefault(i => i.Contribution == contribution) ?? new WidgetItemViewModel(contribution, placement.Cols, placement.Rows) { IsEditing = IsEditing });
        }

        if (!Items.SequenceEqual(next))
        {
            Items.Clear();
            foreach (WidgetItemViewModel item in next)
            {
                Items.Add(item);
            }
        }

        Changed();
    }

    /// <summary>Adds an available widget at the end of the board.</summary>
    /// <param name="contribution">The widget.</param>
    /// <returns><see langword="true"/> when it was added; <see langword="false"/> when it is already there or no longer offered.</returns>
    public bool Add(ContributionViewModel contribution)
    {
        if (Contains(contribution) || !_available.Contains(contribution))
        {
            return false;
        }

        WidgetPlacement placement = WidgetSpans.DefaultPlacement(contribution);
        Items.Add(new WidgetItemViewModel(contribution, placement.Cols, placement.Rows) { IsEditing = IsEditing });
        Commit();
        return true;
    }

    /// <summary>Moves a widget to another position; the order is saved.</summary>
    /// <param name="item">The widget.</param>
    /// <param name="newIndex">Target position, limited to the board.</param>
    /// <returns><see langword="true"/> when the order changed.</returns>
    public bool Move(WidgetItemViewModel item, int newIndex)
    {
        int from = Items.IndexOf(item);
        int to = Math.Clamp(newIndex, 0, Math.Max(0, Items.Count - 1));
        if (from < 0 || from == to)
        {
            return false;
        }

        Items.Move(from, to);
        Commit();
        return true;
    }

    /// <summary>Changes how many grid cells a widget spans.</summary>
    /// <param name="item">The widget.</param>
    /// <param name="cols">Columns, limited to 1 through <see cref="WidgetPlacement.MaxCols"/>.</param>
    /// <param name="rows">Rows, limited to 1 through <see cref="WidgetPlacement.MaxRows"/>.</param>
    /// <param name="commit">Whether to save now; pass <see langword="false"/> while a resize gesture is live and call <see cref="Commit"/> on release.</param>
    /// <returns><see langword="true"/> when the span changed.</returns>
    public bool Resize(WidgetItemViewModel item, int cols, int rows, bool commit = true)
    {
        cols = WidgetPlacement.ClampCols(cols);
        rows = WidgetPlacement.ClampRows(rows);
        if (!Items.Contains(item) || (item.Cols == cols && item.Rows == rows))
        {
            return false;
        }

        item.Cols = cols;
        item.Rows = rows;
        if (commit)
        {
            Commit();
        }

        return true;
    }

    /// <summary>Gets whether a widget is on the dashboard.</summary>
    /// <param name="contribution">The widget.</param>
    /// <returns><see langword="true"/> when shown.</returns>
    public bool Contains(ContributionViewModel contribution) => Items.Any(i => i.Contribution == contribution);

    partial void OnIsEditingChanged(bool value)
    {
        foreach (WidgetItemViewModel item in Items)
        {
            item.IsEditing = value;
        }
    }

    [RelayCommand]
    private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand]
    private void Remove(WidgetItemViewModel? item)
    {
        if (item is not null && Items.Remove(item))
        {
            Commit();
        }
    }

    [RelayCommand]
    private void Open(WidgetItemViewModel? item)
    {
        if (item is not null && !IsEditing)
        {
            _navigator.GoToPluginPage(item.Contribution.PluginId);
        }
    }

    /// <summary>Turns the visible items into the saved layout (keeping placements of widgets that are temporarily not offered) and saves it.</summary>
    public void Commit()
    {
        _layout =
        [
            .. Items.Select(i => new WidgetPlacement(i.Contribution.Key, i.Cols, i.Rows)),
            .. (_layout ?? []).Where(p => !_available.Any(c => c.Key == p.Key)),
        ];
        try
        {
            _store.Save(_layout);
            LayoutError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LayoutError = $"Could not save the widget layout: {ex.Message}";
        }

        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasAvailable));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmptyState));
        Picker.Refresh();
    }
}

/// <summary>A widget on the dashboard with its grid span and edit state.</summary>
internal sealed partial class WidgetItemViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpanLabel))]
    private int _cols;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpanLabel))]
    private int _rows;

    [ObservableProperty]
    private bool _isEditing;

    /// <summary>Creates the item.</summary>
    /// <param name="contribution">The plugin widget.</param>
    /// <param name="cols">Columns the widget spans.</param>
    /// <param name="rows">Rows the widget spans.</param>
    public WidgetItemViewModel(ContributionViewModel contribution, int cols, int rows)
    {
        Contribution = contribution;
        _cols = WidgetPlacement.ClampCols(cols);
        _rows = WidgetPlacement.ClampRows(rows);
        contribution.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContributionViewModel.Icon))
            {
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(HasIcon));
            }
        };
    }

    /// <summary>Gets the plugin widget.</summary>
    public ContributionViewModel Contribution { get; }

    /// <summary>Gets the SVG path data of the plugin icon, if it has one.</summary>
    public string? Icon => Contribution.Icon;

    /// <summary>Gets whether the plugin has an icon.</summary>
    public bool HasIcon => !string.IsNullOrEmpty(Contribution.Icon);

    /// <summary>Gets the span as <c>columns × rows</c>.</summary>
    public string SpanLabel => WidgetSpans.Label(Cols, Rows);
}
