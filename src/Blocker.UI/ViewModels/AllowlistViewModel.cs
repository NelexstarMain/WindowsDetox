using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Blocker.Contracts;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>What the user chose to keep available; read by the setup page when a session starts.</summary>
internal interface IAllowlistSelection
{
    /// <summary>Raised when the selected apps or domains change.</summary>
    event EventHandler? SelectionChanged;

    /// <summary>Gets the number of selected apps.</summary>
    int SelectedCount { get; }

    /// <summary>Gets the allowed domain patterns.</summary>
    IReadOnlyList<string> Domains { get; }

    /// <summary>Builds the wire entries of the selected apps.</summary>
    /// <returns>The entries.</returns>
    IReadOnlyList<AllowlistEntryDto> BuildSelectedEntries();
}

/// <summary>Allowlist page: searchable app picker and web domain editor. Read-only while a session runs.</summary>
internal sealed partial class AllowlistViewModel : ServiceViewModel, IAllowlistSelection
{
    private readonly List<AppEntryViewModel> _allApps = [];
    private bool _loadedOnce;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _newDomain = string.Empty;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _areAllFilteredSelected;

    [ObservableProperty]
    private string _selectionSummary = "No apps selected";

    [ObservableProperty]
    private string _domainHint = DomainPatternValidator.ExampleHint;

    [ObservableProperty]
    private bool _isDomainValid;

    [ObservableProperty]
    private bool _hasDomainError;

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="snackbar">Snackbars.</param>
    public AllowlistViewModel(IServiceConnection connection, ISnackbarService snackbar)
        : base(connection, snackbar)
    {
        Domains.CollectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        connection.SessionChanged += (_, _) => RefreshReadOnly();
        connection.ConnectionChanged += OnConnectionChanged;
        RefreshReadOnly();
        OnConnectionChanged(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? SelectionChanged;

    /// <summary>Gets the apps that match the search text.</summary>
    public ObservableCollection<AppEntryViewModel> FilteredApps { get; } = [];

    /// <summary>Gets the allowed domain patterns.</summary>
    public ObservableCollection<string> Domains { get; } = [];

    /// <summary>Gets the banner text shown while the page is locked.</summary>
    public string LockMessage => "The allowlist is locked while a session is running. It unlocks when the session ends.";

    /// <summary>Gets whether the lock banner is visible.</summary>
    public bool IsLocked => IsReadOnly;

    /// <summary>Gets whether the selection and the domain list can be edited.</summary>
    public bool CanEditSelection => !IsReadOnly;

    /// <summary>Gets whether the app list is empty (before the first scan or when nothing matches).</summary>
    public bool HasNoApps => FilteredApps.Count == 0;

    /// <inheritdoc />
    IReadOnlyList<string> IAllowlistSelection.Domains => Domains;

    /// <inheritdoc />
    public IReadOnlyList<AllowlistEntryDto> BuildSelectedEntries() => [.. _allApps.Where(a => a.IsSelected).Select(a => a.ToEntry())];

    /// <summary>Replaces the app list with scan results, keeping the current selection where apps still match.</summary>
    /// <param name="apps">Scanned applications.</param>
    /// <param name="stored">Entries that must stay selected even when the scan did not find them.</param>
    public void LoadApps(IEnumerable<InstalledAppDto> apps, IReadOnlyList<AllowlistEntryDto> stored)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(stored);
        List<AllowlistEntryDto> keep = [.. _allApps.Where(a => a.IsSelected).Select(a => a.ToEntry()), .. stored];
        foreach (AppEntryViewModel old in _allApps)
        {
            old.PropertyChanged -= OnAppPropertyChanged;
        }

        _allApps.Clear();
        _allApps.AddRange(apps.Select(a => new AppEntryViewModel(a)));
        foreach (AllowlistEntryDto entry in keep)
        {
            AppEntryViewModel? match = _allApps.FirstOrDefault(a => a.Matches(entry));
            if (match is null)
            {
                match = new AppEntryViewModel(entry);
                _allApps.Add(match);
            }

            match.IsSelected = true;
        }

        _allApps.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        foreach (AppEntryViewModel app in _allApps)
        {
            app.PropertyChanged += OnAppPropertyChanged;
        }

        ApplyFilter();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the domain list.</summary>
    /// <param name="domains">Stored domain patterns.</param>
    public void LoadDomains(IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        Domains.Clear();
        foreach (string domain in domains)
        {
            Domains.Add(domain);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnNewDomainChanged(string value)
    {
        DomainValidation result = DomainPatternValidator.Validate(value);
        bool duplicate = result.IsValid && Domains.Contains(result.Normalized, StringComparer.OrdinalIgnoreCase);
        IsDomainValid = result.IsValid && !duplicate;
        HasDomainError = (!result.IsValid && value.Trim().Length > 0) || duplicate;
        DomainHint = duplicate ? "That domain is already in the list." : result.Message ?? DomainPatternValidator.ExampleHint;
        AddDomainCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsReadOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CanEditSelection));
        AddDomainCommand.NotifyCanExecuteChanged();
        RemoveDomainCommand.NotifyCanExecuteChanged();
        SelectAllFilteredCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddDomain))]
    private void AddDomain()
    {
        DomainValidation result = DomainPatternValidator.Validate(NewDomain);
        if (!result.IsValid || Domains.Contains(result.Normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Domains.Add(result.Normalized);
        NewDomain = string.Empty;
    }

    private bool CanAddDomain() => !IsReadOnly && IsDomainValid;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveDomain(string? domain)
    {
        if (domain is not null)
        {
            Domains.Remove(domain);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SelectAllFiltered()
    {
        bool select = !AreAllFilteredSelected;
        foreach (AppEntryViewModel app in FilteredApps)
        {
            app.IsSelected = select;
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private bool CanEdit() => !IsReadOnly;

    /// <summary>Scans installed apps and reads the stored allowlist.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the picker is filled.</returns>
    [RelayCommand]
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        try
        {
            ScanAppsResponse? scan = null;
            AllowlistDto? stored = null;
            bool done = await TryAsync(
                async () =>
                {
                    scan = await Connection.ScanAppsAsync(cancellationToken);
                    stored = await Connection.GetAllowlistAsync(cancellationToken);
                },
                "Could not scan apps");
            if (done && scan is not null && stored is not null)
            {
                // The stored list seeds the first load; later scans keep what the user ticked or unticked since.
                bool first = !_loadedOnce;
                _loadedOnce = true;
                LoadApps(scan.Apps, first || IsReadOnly ? stored.Apps : []);
                if (first || IsReadOnly)
                {
                    LoadDomains(stored.Domains);
                }
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        AllowlistDto allowlist = new(BuildSelectedEntries(), [.. Domains]);
        OperationResponse? response = null;
        bool done = await TryAsync(async () => response = await Connection.SetAllowlistAsync(allowlist, cancellationToken), "Could not save the allowlist");
        if (!done || response is null)
        {
            return;
        }

        if (response.Success)
        {
            Snackbar.Show("Allowlist saved", string.Create(CultureInfo.InvariantCulture, $"{SelectedCount} apps and {Domains.Count} domains."), NotificationSeverity.Success);
        }
        else
        {
            Snackbar.Show("Allowlist not saved", string.Join(' ', response.Errors), NotificationSeverity.Error);
        }
    }

    private bool CanSave() => !IsReadOnly && Connection.IsConnected;

    private void ApplyFilter()
    {
        string query = SearchText.Trim();
        IEnumerable<AppEntryViewModel> matches = query.Length == 0
            ? _allApps
            : _allApps.Where(a =>
                a.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || a.Subtitle.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        List<AppEntryViewModel> next = [.. matches];
        FilteredApps.Clear();
        foreach (AppEntryViewModel app in next)
        {
            FilteredApps.Add(app);
        }

        OnPropertyChanged(nameof(HasNoApps));
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        SelectedCount = _allApps.Count(a => a.IsSelected);
        AreAllFilteredSelected = FilteredApps.Count > 0 && FilteredApps.All(a => a.IsSelected);
        SelectionSummary = SelectedCount == 0
            ? "No apps selected"
            : string.Create(CultureInfo.InvariantCulture, $"{SelectedCount} of {_allApps.Count} apps selected");
    }

    private void OnAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppEntryViewModel.IsSelected))
        {
            UpdateSelectionState();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshReadOnly() => IsReadOnly = Connection.Session.IsSessionRunning;

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        SaveCommand.NotifyCanExecuteChanged();
        if (Connection.IsConnected && !_loadedOnce)
        {
            _ = ScanAsync(CancellationToken.None);
        }
    }
}
