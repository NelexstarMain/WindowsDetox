using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>
/// The "add widgets" side rail: a slim column with one item per plugin. Pointing at an item (or focusing it, or clicking it to pin it)
/// opens a flyout beside the rail that lists that plugin's widgets as live previews to add.
/// </summary>
internal sealed partial class WidgetPickerViewModel : ObservableObject
{
    private readonly WidgetBoardViewModel _board;
    private readonly List<WidgetPickerGroup> _all = [];
    private string _signature = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private WidgetPickerGroup? _hoveredGroup;

    [ObservableProperty]
    private WidgetPickerGroup? _selectedGroup;

    /// <summary>Creates the picker.</summary>
    /// <param name="board">The board widgets are added to.</param>
    public WidgetPickerViewModel(WidgetBoardViewModel board) => _board = board;

    /// <summary>Gets the plugins that offer widgets, one rail item each.</summary>
    public ObservableCollection<WidgetPickerGroup> Groups { get; } = [];

    /// <summary>Gets the plugin whose widgets the flyout lists; it keeps the last one while the flyout fades out.</summary>
    public WidgetPickerGroup? FlyoutGroup { get; private set; }

    /// <summary>Gets whether the flyout is showing: the rail is open and a plugin is hovered, focused or pinned.</summary>
    public bool HasFlyout { get; private set; }

    /// <summary>Rebuilds the rail items when the offered widgets changed and refreshes which widgets are already added.</summary>
    public void Refresh()
    {
        string signature = string.Join('\n', _board.Available.Select(c => c.Key));
        if (signature != _signature)
        {
            _signature = signature;
            Dictionary<ContributionViewModel, WidgetPickerEntry> known = _all.SelectMany(g => g.Entries).ToDictionary(e => e.Contribution);
            List<WidgetPickerGroup> next = [];
            foreach (IGrouping<string, ContributionViewModel> plugin in _board.Available.GroupBy(c => c.PluginId, StringComparer.Ordinal))
            {
                string? icon = plugin.Select(c => c.Icon).FirstOrDefault(i => !string.IsNullOrEmpty(i));
                List<WidgetPickerEntry> entries = [.. plugin.Select(c => known.GetValueOrDefault(c) ?? new WidgetPickerEntry(c))];
                next.Add(new WidgetPickerGroup(plugin.Key, plugin.First().Title, icon, entries));
            }

            WidgetPickerGroup? hovered = next.FirstOrDefault(g => g.PluginId == HoveredGroup?.PluginId);
            WidgetPickerGroup? selected = next.FirstOrDefault(g => g.PluginId == SelectedGroup?.PluginId);
            _all.Clear();
            _all.AddRange(next);
            if (!Groups.SequenceEqual(next))
            {
                Groups.Clear();
                foreach (WidgetPickerGroup group in next)
                {
                    Groups.Add(group);
                }
            }

            (HoveredGroup, SelectedGroup) = (hovered, selected);
            UpdateFlyout();
        }

        foreach (WidgetPickerEntry entry in _all.SelectMany(g => g.Entries))
        {
            entry.IsAdded = _board.Contains(entry.Contribution);
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
        {
            (HoveredGroup, SelectedGroup) = (null, null);
        }

        UpdateFlyout();
    }

    partial void OnHoveredGroupChanged(WidgetPickerGroup? value) => UpdateFlyout();

    partial void OnSelectedGroupChanged(WidgetPickerGroup? value) => UpdateFlyout();

    private void UpdateFlyout()
    {
        WidgetPickerGroup? active = IsOpen ? HoveredGroup ?? SelectedGroup : null;
        if (active is not null && !ReferenceEquals(FlyoutGroup, active))
        {
            FlyoutGroup = active;
            OnPropertyChanged(nameof(FlyoutGroup));
        }

        foreach (WidgetPickerGroup group in _all)
        {
            group.IsActive = ReferenceEquals(group, active);
        }

        bool has = active is not null;
        if (has != HasFlyout)
        {
            HasFlyout = has;
            OnPropertyChanged(nameof(HasFlyout));
        }
    }

    [RelayCommand]
    private void Open()
    {
        Refresh();
        (HoveredGroup, SelectedGroup) = (null, null);
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Pins a plugin's flyout open (for touch and keyboard); selecting the pinned plugin again unpins it.</summary>
    /// <param name="group">The plugin.</param>
    [RelayCommand]
    private void SelectGroup(WidgetPickerGroup? group) => SelectedGroup = ReferenceEquals(SelectedGroup, group) ? null : group;

    [RelayCommand]
    private void Add(WidgetPickerEntry? entry)
    {
        if (entry is not null && _board.Add(entry.Contribution))
        {
            entry.IsAdded = true;
        }
    }
}

/// <summary>The widgets of one plugin: an item in the picker rail whose flyout lists them.</summary>
internal sealed partial class WidgetPickerGroup : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

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
}

/// <summary>One widget in the picker, previewed at the span it is added with.</summary>
internal sealed partial class WidgetPickerEntry : ObservableObject
{
    /// <summary>Widest a preview is drawn; wider widgets are scaled down to it.</summary>
    public const double MaxPreviewWidth = 240;

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
