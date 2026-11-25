using System.Collections.ObjectModel;
using System.Globalization;
using Blocker.Contracts;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>One saved set as a chip.</summary>
internal sealed partial class AppSetItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Creates the chip.</summary>
    /// <param name="set">The saved set.</param>
    public AppSetItemViewModel(AppSetDto set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Set = set;
    }

    /// <summary>Gets the saved set.</summary>
    public AppSetDto Set { get; }

    /// <summary>Gets the name shown on the chip.</summary>
    public string Name => Set.Name;

    /// <summary>Gets the tooltip text: how many apps and websites the set holds.</summary>
    public string Summary => string.Create(
        CultureInfo.InvariantCulture,
        $"{Set.Apps.Count} {(Set.Apps.Count == 1 ? "app" : "apps")}, {Set.Domains.Count} {(Set.Domains.Count == 1 ? "website" : "websites")}");
}

/// <summary>Saved app sets above the picker: apply one to replace the selection, save the selection as a set, remove a set.</summary>
internal sealed partial class AppSetsViewModel : ServiceViewModel
{
    /// <summary>Longest set name.</summary>
    public const int MaxNameLength = 40;

    private readonly AllowlistViewModel _allowlist;
    private readonly IDialogService _dialogs;
    private bool _loaded;

    [ObservableProperty]
    private bool _isNaming;

    [ObservableProperty]
    private string _newName = string.Empty;

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="snackbar">Snackbars.</param>
    /// <param name="dialogs">Confirmation dialogs.</param>
    /// <param name="allowlist">The picker whose selection the sets read and replace.</param>
    public AppSetsViewModel(IServiceConnection connection, ISnackbarService snackbar, IDialogService dialogs, AllowlistViewModel allowlist)
        : base(connection, snackbar)
    {
        _dialogs = dialogs;
        _allowlist = allowlist;
        allowlist.SelectionChanged += (_, _) => RefreshCurrent();
        allowlist.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AllowlistViewModel.IsReadOnly))
            {
                OnLockChanged();
            }
        };
        connection.ConnectionChanged += (_, _) => _ = LoadOnceAsync();
        _ = LoadOnceAsync();
    }

    /// <summary>Gets the saved sets in the order the user arranged them.</summary>
    public ObservableCollection<AppSetItemViewModel> Sets { get; } = [];

    /// <summary>Gets whether no set is saved yet.</summary>
    public bool HasNoSets => Sets.Count == 0;

    /// <summary>Gets whether sets can be applied, saved and removed (not while a session runs).</summary>
    public bool CanEdit => !_allowlist.IsReadOnly;

    /// <summary>Gets whether <see cref="NewName"/> names an existing set, so saving overwrites it.</summary>
    public bool NameExists => FindByName(NewName) is not null;

    /// <summary>Reads the saved sets from the service once.</summary>
    /// <returns>A task that completes when the sets are shown.</returns>
    public async Task LoadOnceAsync()
    {
        if (_loaded || !Connection.IsConnected)
        {
            return;
        }

        AppSetsDto? stored = null;
        if (await TryAsync(async () => stored = await Connection.GetAppSetsAsync(CancellationToken.None), "Could not load the saved sets") && stored is not null)
        {
            _loaded = true;
            ShowSets(stored.Sets);
        }
    }

    partial void OnNewNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameExists));
        ConfirmSaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ApplySet(AppSetItemViewModel? item)
    {
        if (item is not null)
        {
            _allowlist.ApplySelection(item.Set.Apps, item.Set.Domains);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RemoveSetAsync(AppSetItemViewModel? item, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            string.Create(CultureInfo.InvariantCulture, $"Remove the set \"{item.Name}\"?"),
            "The apps and websites stay as they are; only the saved set is deleted.",
            "Remove set",
            "Keep it",
            cancellationToken);
        if (confirmed)
        {
            await PersistAsync([.. Sets.Where(s => s != item).Select(s => s.Set)], "Set removed", item.Name, cancellationToken);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void BeginSave()
    {
        NewName = string.Empty;
        IsNaming = true;
    }

    [RelayCommand]
    private void CancelSave() => IsNaming = false;

    [RelayCommand(CanExecute = nameof(CanConfirmSave))]
    private async Task ConfirmSaveAsync(CancellationToken cancellationToken)
    {
        string name = NewName.Trim();
        AppSetItemViewModel? existing = FindByName(name);
        if (existing is not null)
        {
            bool overwrite = await _dialogs.ConfirmAsync(
                string.Create(CultureInfo.InvariantCulture, $"Replace the set \"{existing.Name}\"?"),
                "A set with this name already exists. It will hold the apps and websites selected now.",
                "Replace",
                "Cancel",
                cancellationToken);
            if (!overwrite)
            {
                return;
            }
        }

        AppSetDto set = new(existing?.Name ?? name, _allowlist.BuildSelectedEntries(), [.. _allowlist.Domains]);
        List<AppSetDto> next = [.. Sets.Select(s => s.Set)];
        if (existing is null)
        {
            next.Add(set);
        }
        else
        {
            next[Sets.IndexOf(existing)] = set;
        }

        if (await PersistAsync(next, "Set saved", set.Name, cancellationToken))
        {
            IsNaming = false;
        }
    }

    private bool CanConfirmSave() => CanEdit && NewName.Trim().Length > 0;

    private AppSetItemViewModel? FindByName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Length == 0 ? null : Sets.FirstOrDefault(s => string.Equals(s.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
    }

    private async Task<bool> PersistAsync(IReadOnlyList<AppSetDto> sets, string successTitle, string name, CancellationToken cancellationToken)
    {
        OperationResponse? response = null;
        bool done = await TryAsync(async () => response = await Connection.SetAppSetsAsync(new AppSetsDto(sets), cancellationToken), "Could not save the sets");
        if (!done || response is null)
        {
            return false;
        }

        if (!response.Success)
        {
            Snackbar.Show("Sets not saved", string.Join(' ', response.Errors), NotificationSeverity.Error);
            return false;
        }

        ShowSets(sets);
        Snackbar.Show(successTitle, name, NotificationSeverity.Success);
        return true;
    }

    private void ShowSets(IReadOnlyList<AppSetDto> sets)
    {
        Sets.Clear();
        foreach (AppSetDto set in sets)
        {
            Sets.Add(new AppSetItemViewModel(set));
        }

        OnPropertyChanged(nameof(HasNoSets));
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        foreach (AppSetItemViewModel item in Sets)
        {
            item.IsCurrent = _allowlist.SelectionEquals(item.Set.Apps, item.Set.Domains);
        }
    }

    private void OnLockChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        if (!CanEdit)
        {
            IsNaming = false;
        }

        ApplySetCommand.NotifyCanExecuteChanged();
        RemoveSetCommand.NotifyCanExecuteChanged();
        BeginSaveCommand.NotifyCanExecuteChanged();
        ConfirmSaveCommand.NotifyCanExecuteChanged();
    }
}
