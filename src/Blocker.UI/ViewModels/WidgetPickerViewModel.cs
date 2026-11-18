using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>The "add widgets" bottom sheet: every available widget grouped by plugin, with a live preview.</summary>
internal sealed partial class WidgetPickerViewModel : ObservableObject
{
    private readonly WidgetBoardViewModel _board;
    private string _signature = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Creates the picker.</summary>
    /// <param name="board">The board widgets are added to.</param>
    public WidgetPickerViewModel(WidgetBoardViewModel board) => _board = board;

    /// <summary>Gets the available widgets grouped by plugin.</summary>
    public ObservableCollection<WidgetPickerGroup> Groups { get; } = [];

    /// <summary>Rebuilds the groups when the offered widgets changed and refreshes which ones are already added.</summary>
    public void Refresh()
    {
        string signature = string.Join('\n', _board.Available.Select(c => c.Key));
        if (signature != _signature)
        {
            _signature = signature;
            Dictionary<ContributionViewModel, WidgetPickerEntry> known = Groups.SelectMany(g => g.Entries).ToDictionary(e => e.Contribution);
            Groups.Clear();
            foreach (IGrouping<string, ContributionViewModel> plugin in _board.Available.GroupBy(c => c.PluginId, StringComparer.Ordinal))
            {
                List<WidgetPickerEntry> entries = [.. plugin.Select(c => known.GetValueOrDefault(c) ?? new WidgetPickerEntry(c))];
                Groups.Add(new WidgetPickerGroup(plugin.First().Title, plugin.Select(c => c.Icon).FirstOrDefault(i => !string.IsNullOrEmpty(i)), entries));
            }
        }

        foreach (WidgetPickerEntry entry in Groups.SelectMany(g => g.Entries))
        {
            entry.IsAdded = _board.Contains(entry.Contribution);
        }
    }

    [RelayCommand]
    private void Open()
    {
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void Add(WidgetPickerEntry? entry)
    {
        if (entry is not null && _board.Add(entry.Contribution))
        {
            IsOpen = false;
        }
    }
}

/// <summary>The widgets of one plugin.</summary>
/// <param name="Title">Plugin name.</param>
/// <param name="Icon">SVG path data of the plugin icon, if it has one.</param>
/// <param name="Entries">Its widgets.</param>
internal sealed record WidgetPickerGroup(string Title, string? Icon, IReadOnlyList<WidgetPickerEntry> Entries)
{
    /// <summary>Gets whether the plugin has an icon.</summary>
    public bool HasIcon => !string.IsNullOrEmpty(Icon);
}

/// <summary>One widget in the picker.</summary>
internal sealed partial class WidgetPickerEntry : ObservableObject
{
    [ObservableProperty]
    private bool _isAdded;

    /// <summary>Creates the entry.</summary>
    /// <param name="contribution">The widget.</param>
    public WidgetPickerEntry(ContributionViewModel contribution) => Contribution = contribution;

    /// <summary>Gets the widget, rendered as the preview.</summary>
    public ContributionViewModel Contribution { get; }

    /// <summary>Gets the size the widget is added with.</summary>
    public string SizeLabel => "Small";
}
