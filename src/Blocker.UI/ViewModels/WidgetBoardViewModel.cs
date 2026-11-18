using System.Collections.ObjectModel;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>
/// The user's dashboard widgets: which plugin widgets are shown, in which order and size. Until the user changes the board for the
/// first time, every available widget is shown; afterwards only the chosen ones are, and the choice is saved after every change.
/// </summary>
internal sealed partial class WidgetBoardViewModel : ObservableObject
{
    private readonly IWidgetLayoutStore _store;
    private IReadOnlyList<ContributionViewModel> _available = [];
    private List<WidgetPlacement>? _layout;
    private bool _loaded;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string? _layoutError;

    /// <summary>Creates the board.</summary>
    /// <param name="store">Where the layout is saved.</param>
    public WidgetBoardViewModel(IWidgetLayoutStore store)
    {
        _store = store;
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

        IEnumerable<WidgetPlacement> wanted = _layout ?? _available.Select(c => new WidgetPlacement(c.Key));
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<WidgetItemViewModel> next = [];
        foreach (WidgetPlacement placement in wanted)
        {
            ContributionViewModel? contribution = _available.FirstOrDefault(c => c.Key == placement.Key);
            if (contribution is null || !seen.Add(placement.Key))
            {
                continue;
            }

            WidgetItemViewModel item = Items.FirstOrDefault(i => i.Contribution == contribution) ?? new WidgetItemViewModel(contribution, placement.Size) { IsEditing = IsEditing };
            item.Size = placement.Size;
            next.Add(item);
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

        Items.Add(new WidgetItemViewModel(contribution, WidgetSize.Small) { IsEditing = IsEditing });
        Commit();
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

    [RelayCommand(CanExecute = nameof(CanMoveEarlier))]
    private void MoveEarlier(WidgetItemViewModel? item) => Move(item, -1);

    [RelayCommand(CanExecute = nameof(CanMoveLater))]
    private void MoveLater(WidgetItemViewModel? item) => Move(item, 1);

    [RelayCommand]
    private void ToggleSize(WidgetItemViewModel? item)
    {
        if (item is null || !Items.Contains(item))
        {
            return;
        }

        item.Size = item.IsWide ? WidgetSize.Small : WidgetSize.Wide;
        Commit();
    }

    private bool CanMoveEarlier(WidgetItemViewModel? item) => item is not null && Items.IndexOf(item) > 0;

    private bool CanMoveLater(WidgetItemViewModel? item) => item is not null && Items.IndexOf(item) is var index && index >= 0 && index < Items.Count - 1;

    private void Move(WidgetItemViewModel? item, int offset)
    {
        int from = item is null ? -1 : Items.IndexOf(item);
        int to = from + offset;
        if (from < 0 || to < 0 || to >= Items.Count)
        {
            return;
        }

        Items.Move(from, to);
        Commit();
    }

    /// <summary>Turns the visible items into the saved layout (keeping placements of widgets that are temporarily not offered) and saves it.</summary>
    private void Commit()
    {
        _layout =
        [
            .. Items.Select(i => new WidgetPlacement(i.Contribution.Key, i.Size)),
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
        MoveEarlierCommand.NotifyCanExecuteChanged();
        MoveLaterCommand.NotifyCanExecuteChanged();
        Picker.Refresh();
    }
}

/// <summary>A widget on the dashboard with its size and edit state.</summary>
internal sealed partial class WidgetItemViewModel : ObservableObject
{
    /// <summary>Card width of a one-column widget.</summary>
    public const double SmallWidth = 300;

    /// <summary>Card width of a two-column widget (two small cards and the gap between them).</summary>
    public const double WideWidth = (SmallWidth * 2) + 16;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWide), nameof(Width), nameof(ToggleSizeText))]
    private WidgetSize _size;

    [ObservableProperty]
    private bool _isEditing;

    /// <summary>Creates the item.</summary>
    /// <param name="contribution">The plugin widget.</param>
    /// <param name="size">Initial size.</param>
    public WidgetItemViewModel(ContributionViewModel contribution, WidgetSize size)
    {
        Contribution = contribution;
        _size = size;
    }

    /// <summary>Gets the plugin widget.</summary>
    public ContributionViewModel Contribution { get; }

    /// <summary>Gets whether the widget spans two columns.</summary>
    public bool IsWide => Size == WidgetSize.Wide;

    /// <summary>Gets the card width.</summary>
    public double Width => IsWide ? WideWidth : SmallWidth;

    /// <summary>Gets the label of the size the toggle switches to.</summary>
    public string ToggleSizeText => IsWide ? "Small" : "Wide";
}
