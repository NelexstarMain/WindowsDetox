using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>
/// The "add widgets" bottom sheet: plugins as rows (one open at a time) that expand to a strip of live widget previews, and a search
/// box that filters by plugin or widget name.
/// </summary>
internal sealed partial class WidgetPickerViewModel : ObservableObject
{
    private readonly WidgetBoardViewModel _board;
    private readonly List<WidgetPickerGroup> _all = [];
    private string _signature = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Creates the picker.</summary>
    /// <param name="board">The board widgets are added to.</param>
    public WidgetPickerViewModel(WidgetBoardViewModel board) => _board = board;

    /// <summary>Gets the plugins that match the search, with their widgets.</summary>
    public ObservableCollection<WidgetPickerGroup> Groups { get; } = [];

    /// <summary>Gets whether a search matches no plugin or widget.</summary>
    public bool HasNoResults => Groups.Count == 0 && _all.Count > 0;

    /// <summary>Rebuilds the groups when the offered widgets changed and refreshes which ones are already added.</summary>
    public void Refresh()
    {
        string signature = string.Join('\n', _board.Available.Select(c => c.Key));
        if (signature != _signature)
        {
            _signature = signature;
            Dictionary<ContributionViewModel, WidgetPickerEntry> known = _all.SelectMany(g => g.Entries).ToDictionary(e => e.Contribution);
            Dictionary<string, WidgetPickerGroup> groups = _all.ToDictionary(g => g.PluginId, StringComparer.Ordinal);
            List<WidgetPickerGroup> next = [];
            foreach (IGrouping<string, ContributionViewModel> plugin in _board.Available.GroupBy(c => c.PluginId, StringComparer.Ordinal))
            {
                string? icon = plugin.Select(c => c.Icon).FirstOrDefault(i => !string.IsNullOrEmpty(i));
                List<WidgetPickerEntry> entries = [.. plugin.Select(c => known.GetValueOrDefault(c) ?? new WidgetPickerEntry(c))];
                next.Add(new WidgetPickerGroup(plugin.Key, plugin.First().Title, icon, entries) { IsExpanded = groups.TryGetValue(plugin.Key, out WidgetPickerGroup? old) && old.IsExpanded });
            }

            _all.Clear();
            _all.AddRange(next);
            if (_all.Count > 0 && !_all.Any(g => g.IsExpanded))
            {
                _all[0].IsExpanded = true;
            }

            ApplyFilter();
        }

        foreach (WidgetPickerEntry entry in _all.SelectMany(g => g.Entries))
        {
            entry.IsAdded = _board.Contains(entry.Contribution);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string term = SearchText.Trim();
        List<WidgetPickerGroup> shown = [];
        foreach (WidgetPickerGroup group in _all)
        {
            bool pluginMatches = term.Length == 0 || group.Title.Contains(term, StringComparison.OrdinalIgnoreCase);
            group.Visible = [.. group.Entries.Where(e => pluginMatches || e.Title.Contains(term, StringComparison.OrdinalIgnoreCase))];
            if (group.Visible.Count > 0)
            {
                shown.Add(group);
                if (term.Length > 0)
                {
                    group.IsExpanded = true;
                }
            }
        }

        if (!Groups.SequenceEqual(shown))
        {
            Groups.Clear();
            foreach (WidgetPickerGroup group in shown)
            {
                Groups.Add(group);
            }
        }

        OnPropertyChanged(nameof(HasNoResults));
    }

    [RelayCommand]
    private void Open()
    {
        SearchText = string.Empty;
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void ToggleGroup(WidgetPickerGroup? group)
    {
        if (group is null)
        {
            return;
        }

        bool expand = !group.IsExpanded;
        foreach (WidgetPickerGroup other in _all)
        {
            other.IsExpanded = false;
        }

        group.IsExpanded = expand;
    }

    [RelayCommand]
    private void Add(WidgetPickerEntry? entry)
    {
        if (entry is not null && _board.Add(entry.Contribution))
        {
            entry.IsAdded = true;
        }
    }
}

/// <summary>The widgets of one plugin: a row in the picker that expands to its previews.</summary>
internal sealed partial class WidgetPickerGroup : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private IReadOnlyList<WidgetPickerEntry> _visible;

    /// <summary>Creates the group.</summary>
    /// <param name="pluginId">Plugin id.</param>
    /// <param name="title">Plugin name.</param>
    /// <param name="icon">SVG path data of the plugin icon, if it has one.</param>
    /// <param name="entries">Its widgets.</param>
    public WidgetPickerGroup(string pluginId, string title, string? icon, IReadOnlyList<WidgetPickerEntry> entries)
    {
        PluginId = pluginId;
        Title = title;
        Icon = icon;
        Entries = entries;
        _visible = entries;
    }

    /// <summary>Gets the plugin id.</summary>
    public string PluginId { get; }

    /// <summary>Gets the plugin name.</summary>
    public string Title { get; }

    /// <summary>Gets the SVG path data of the plugin icon, if it has one.</summary>
    public string? Icon { get; }

    /// <summary>Gets all widgets of the plugin.</summary>
    public IReadOnlyList<WidgetPickerEntry> Entries { get; }

    /// <summary>Gets whether the plugin has an icon.</summary>
    public bool HasIcon => !string.IsNullOrEmpty(Icon);

    /// <summary>Gets "1 widget" or "N widgets".</summary>
    public string CountText => Entries.Count == 1 ? "1 widget" : $"{Entries.Count} widgets";
}

/// <summary>One widget in the picker, previewed at the span it is added with.</summary>
internal sealed partial class WidgetPickerEntry : ObservableObject
{
    /// <summary>Widest a preview is drawn; wider widgets are scaled down to it.</summary>
    public const double MaxPreviewWidth = 220;

    [ObservableProperty]
    private bool _isAdded;

    /// <summary>Creates the entry.</summary>
    /// <param name="contribution">The widget.</param>
    public WidgetPickerEntry(ContributionViewModel contribution)
    {
        Contribution = contribution;
        Cols = 1;
        Rows = WidgetSpans.DefaultRows(contribution.Node);
        SlotWidth = WidgetSpans.SlotWidth(Cols);
        SlotHeight = WidgetSpans.SlotHeight(Rows);
        double scale = Math.Min(1, MaxPreviewWidth / SlotWidth);
        PreviewWidth = SlotWidth * scale;
        PreviewHeight = SlotHeight * scale;
    }

    /// <summary>Gets the widget, rendered as the preview.</summary>
    public ContributionViewModel Contribution { get; }

    /// <summary>Gets the widget name.</summary>
    public string Title => Contribution.Title;

    /// <summary>Gets the columns the widget is added with.</summary>
    public int Cols { get; }

    /// <summary>Gets the rows the widget is added with.</summary>
    public int Rows { get; }

    /// <summary>Gets the span as <c>columns × rows</c>.</summary>
    public string SpanLabel => WidgetSpans.Label(Cols, Rows);

    /// <summary>Gets the width the widget has on the board, in DIPs; the preview lays it out at this size and scales it down.</summary>
    public double SlotWidth { get; }

    /// <summary>Gets the height the widget has on the board, in DIPs.</summary>
    public double SlotHeight { get; }

    /// <summary>Gets the width of the scaled preview.</summary>
    public double PreviewWidth { get; }

    /// <summary>Gets the height of the scaled preview.</summary>
    public double PreviewHeight { get; }
}
