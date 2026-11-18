using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.UI.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blocker.UI.ViewModels;

/// <summary>One validated plugin contribution (dashboard widget, page, settings section) ready to be rendered.</summary>
internal sealed partial class ContributionViewModel : ObservableObject
{
    private string _json;

    [ObservableProperty]
    private UiNode _node;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string? _icon;

    private ContributionViewModel(UiContributionDto dto, UiNode node)
    {
        PluginId = dto.PluginId;
        ContributionId = dto.ContributionId;
        _title = dto.Title ?? dto.PluginId;
        _json = dto.Json;
        _icon = dto.Icon;
        _node = node;
    }

    /// <summary>Gets the owning plugin.</summary>
    public string PluginId { get; }

    /// <summary>Gets the contribution id within the plugin.</summary>
    public string ContributionId { get; }

    /// <summary>Gets a key that is unique across plugins.</summary>
    public string Key => $"{PluginId}/{ContributionId}";

    /// <summary>
    /// Deserializes and validates a contribution. Trees that are malformed or violate the host's limits are rejected, never rendered.
    /// </summary>
    /// <param name="dto">Wire contribution.</param>
    /// <param name="contribution">The contribution when valid.</param>
    /// <returns><see langword="true"/> when the tree can be rendered.</returns>
    public static bool TryCreate(UiContributionDto dto, [NotNullWhen(true)] out ContributionViewModel? contribution)
    {
        contribution = null;
        UiNode node;
        try
        {
            node = UiJson.Deserialize(dto.Json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (!UiTreeValidator.Validate(node).IsValid)
        {
            return false;
        }

        contribution = new ContributionViewModel(dto, node);
        return true;
    }

    /// <summary>Applies a newer version of the same contribution without recreating the view.</summary>
    /// <param name="dto">Wire contribution with the same key.</param>
    /// <param name="node">Its validated tree.</param>
    public void Update(UiContributionDto dto, UiNode node)
    {
        Title = dto.Title ?? dto.PluginId;
        Icon = dto.Icon;
        if (!string.Equals(_json, dto.Json, StringComparison.Ordinal))
        {
            _json = dto.Json;
            Node = node;
        }
    }

    /// <summary>
    /// Brings <paramref name="target"/> in line with <paramref name="items"/>: keeps existing instances (so open views do not flicker),
    /// adds new ones, removes those that disappeared and drops invalid trees.
    /// </summary>
    /// <param name="target">Collection bound to the UI.</param>
    /// <param name="items">Contributions received from the service.</param>
    public static void Synchronize(IList<ContributionViewModel> target, IEnumerable<UiContributionDto> items)
    {
        List<ContributionViewModel> next = [];
        foreach (UiContributionDto dto in items)
        {
            if (!TryCreate(dto, out ContributionViewModel? created))
            {
                continue;
            }

            ContributionViewModel? existing = target.FirstOrDefault(c => c.Key == created.Key);
            if (existing is null)
            {
                next.Add(created);
            }
            else
            {
                existing.Update(dto, created.Node);
                next.Add(existing);
            }
        }

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!next.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < next.Count; i++)
        {
            int current = target.IndexOf(next[i]);
            if (current < 0)
            {
                target.Insert(i, next[i]);
            }
            else if (current != i)
            {
                if (target is System.Collections.ObjectModel.ObservableCollection<ContributionViewModel> observable)
                {
                    observable.Move(current, i);
                }
                else
                {
                    target.RemoveAt(current);
                    target.Insert(i, next[i]);
                }
            }
        }
    }
}

/// <summary>A user interaction with a rendered contribution.</summary>
/// <param name="Contribution">The contribution the user interacted with.</param>
/// <param name="Action">The interaction.</param>
internal sealed record ContributionAction(ContributionViewModel Contribution, UiAction Action);
