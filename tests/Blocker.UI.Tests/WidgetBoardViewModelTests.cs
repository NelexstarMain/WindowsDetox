using Blocker.Contracts;
using Blocker.UI.Sdk;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class WidgetBoardViewModelTests
{
    private readonly InMemoryWidgetLayoutStore _store = new();

    private static ContributionViewModel Widget(string plugin, string id, string? icon = null)
    {
        UiContributionDto dto = new(plugin, id, id, UiJson.Serialize(new UiCard([new UiText(id)], id)), icon);
        Assert.True(ContributionViewModel.TryCreate(dto, out ContributionViewModel? widget));
        return widget;
    }

    private WidgetBoardViewModel Create(params ContributionViewModel[] available)
    {
        WidgetBoardViewModel board = new(_store);
        board.Update(available);
        return board;
    }

    private static string[] Keys(WidgetBoardViewModel board) => [.. board.Items.Select(i => i.Contribution.Key)];

    [Fact]
    public void First_run_shows_every_available_widget_without_saving()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"));

        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.All(board.Items, i => Assert.Equal(WidgetSize.Small, i.Size));
        Assert.Equal(0, _store.SaveCount);
        Assert.False(board.ShowEmptyState);
    }

    [Fact]
    public void First_run_follows_widgets_that_appear_later()
    {
        WidgetBoardViewModel board = new(_store);
        board.Update([]);
        Assert.False(board.HasAvailable);
        Assert.False(board.ShowEmptyState);

        board.Update([Widget("a", "1")]);

        Assert.Equal(["a/1"], Keys(board));
    }

    [Fact]
    public void Saved_layout_decides_which_widgets_show_and_in_which_order_and_size()
    {
        _store.Saved = [new("b/2", WidgetSize.Wide), new("a/1", WidgetSize.Small)];

        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"), Widget("c", "3"));

        Assert.Equal(["b/2", "a/1"], Keys(board));
        Assert.True(board.Items[0].IsWide);
        Assert.Equal(WidgetItemViewModel.WideWidth, board.Items[0].Width);
        Assert.Equal(WidgetItemViewModel.SmallWidth, board.Items[1].Width);
    }

    [Fact]
    public void Widgets_of_missing_plugins_are_skipped_but_kept_in_the_saved_layout()
    {
        _store.Saved = [new("gone/1", WidgetSize.Wide), new("a/1", WidgetSize.Small)];
        ContributionViewModel a2 = Widget("a", "2");
        WidgetBoardViewModel board = Create(Widget("a", "1"), a2);

        Assert.Equal(["a/1"], Keys(board));

        Assert.True(board.Add(a2));

        Assert.Equal(["a/1", "a/2"], Keys(board));
        Assert.Equal(
            [new WidgetPlacement("a/1"), new WidgetPlacement("a/2"), new WidgetPlacement("gone/1", WidgetSize.Wide)],
            _store.Saved);
    }

    [Fact]
    public void A_widget_that_returns_reappears_where_it_was_saved()
    {
        _store.Saved = [new("a/1", WidgetSize.Small), new("b/2", WidgetSize.Wide)];
        ContributionViewModel a1 = Widget("a", "1");
        WidgetBoardViewModel board = Create(a1);
        Assert.Equal(["a/1"], Keys(board));

        board.Update([a1, Widget("b", "2")]);

        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.True(board.Items[1].IsWide);
    }

    [Fact]
    public void Remove_hides_the_widget_and_saves()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"));

        board.RemoveCommand.Execute(board.Items[0]);

        Assert.Equal(["b/2"], Keys(board));
        Assert.Equal([new WidgetPlacement("b/2")], _store.Saved);
        Assert.Equal(1, _store.SaveCount);
    }

    [Fact]
    public void Removed_widget_stays_removed_when_the_widgets_refresh()
    {
        ContributionViewModel a1 = Widget("a", "1");
        ContributionViewModel b2 = Widget("b", "2");
        WidgetBoardViewModel board = Create(a1, b2);
        board.RemoveCommand.Execute(board.Items[0]);

        board.Update([a1, b2]);

        Assert.Equal(["b/2"], Keys(board));
    }

    [Fact]
    public void Removing_every_widget_shows_the_empty_state()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"));

        board.RemoveCommand.Execute(board.Items[0]);

        Assert.False(board.HasItems);
        Assert.True(board.ShowEmptyState);
        Assert.Empty(_store.Saved!);
    }

    [Fact]
    public void Move_reorders_within_bounds_and_saves()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"), Widget("c", "3"));
        WidgetItemViewModel first = board.Items[0];

        Assert.False(board.MoveEarlierCommand.CanExecute(first));
        Assert.True(board.MoveLaterCommand.CanExecute(first));
        board.MoveLaterCommand.Execute(first);

        Assert.Equal(["b/2", "a/1", "c/3"], Keys(board));
        Assert.Equal(["b/2", "a/1", "c/3"], _store.Saved!.Select(p => p.Key));

        board.MoveEarlierCommand.Execute(board.Items[2]);
        Assert.Equal(["b/2", "c/3", "a/1"], Keys(board));
        Assert.False(board.MoveLaterCommand.CanExecute(board.Items[2]));
    }

    [Fact]
    public void Moving_past_the_end_does_nothing()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"));

        board.MoveLaterCommand.Execute(board.Items[1]);
        board.MoveEarlierCommand.Execute(board.Items[0]);

        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void Toggle_size_switches_between_small_and_wide_and_saves()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"));
        WidgetItemViewModel item = board.Items[0];
        Assert.Equal("Wide", item.ToggleSizeText);

        board.ToggleSizeCommand.Execute(item);
        Assert.Equal(WidgetSize.Wide, item.Size);
        Assert.Equal(WidgetItemViewModel.WideWidth, item.Width);
        Assert.Equal("Small", item.ToggleSizeText);
        Assert.Equal([new WidgetPlacement("a/1", WidgetSize.Wide)], _store.Saved);

        board.ToggleSizeCommand.Execute(item);
        Assert.Equal(WidgetSize.Small, item.Size);
        Assert.Equal([new WidgetPlacement("a/1", WidgetSize.Small)], _store.Saved);
    }

    [Fact]
    public void Size_survives_a_refresh()
    {
        ContributionViewModel a1 = Widget("a", "1");
        WidgetBoardViewModel board = Create(a1);
        board.ToggleSizeCommand.Execute(board.Items[0]);

        board.Update([a1]);

        Assert.True(board.Items[0].IsWide);
    }

    [Fact]
    public void Refresh_keeps_the_same_item_instances()
    {
        ContributionViewModel a1 = Widget("a", "1");
        WidgetBoardViewModel board = Create(a1);
        WidgetItemViewModel item = board.Items[0];

        board.Update([a1]);

        Assert.Same(item, Assert.Single(board.Items));
    }

    [Fact]
    public void Add_appends_a_small_widget_once_and_ignores_unknown_ones()
    {
        ContributionViewModel a1 = Widget("a", "1");
        ContributionViewModel b2 = Widget("b", "2");
        _store.Saved = [new("a/1", WidgetSize.Wide)];
        WidgetBoardViewModel board = Create(a1, b2);

        Assert.True(board.Add(b2));
        Assert.False(board.Add(b2));
        Assert.False(board.Add(Widget("c", "3")));

        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.Equal([new WidgetPlacement("a/1", WidgetSize.Wide), new WidgetPlacement("b/2")], _store.Saved);
        Assert.Equal(1, _store.SaveCount);
    }

    [Fact]
    public void Layout_is_loaded_once_and_the_first_change_materializes_the_first_run_layout()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"));

        board.ToggleSizeCommand.Execute(board.Items[1]);

        Assert.Equal([new WidgetPlacement("a/1"), new WidgetPlacement("b/2", WidgetSize.Wide)], _store.Saved);
    }

    [Fact]
    public void Edit_mode_applies_to_existing_and_new_items()
    {
        ContributionViewModel a1 = Widget("a", "1");
        ContributionViewModel b2 = Widget("b", "2");
        _store.Saved = [new("a/1")];
        WidgetBoardViewModel board = Create(a1, b2);

        board.ToggleEditCommand.Execute(null);
        board.Add(b2);

        Assert.True(board.IsEditing);
        Assert.All(board.Items, i => Assert.True(i.IsEditing));

        board.ToggleEditCommand.Execute(null);
        Assert.All(board.Items, i => Assert.False(i.IsEditing));
    }

    [Fact]
    public void Save_failure_is_reported_and_the_board_keeps_working()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"));
        _store.FailWith = new IOException("disk full");

        board.RemoveCommand.Execute(board.Items[0]);

        Assert.Equal(["b/2"], Keys(board));
        Assert.Contains("disk full", board.LayoutError, StringComparison.Ordinal);

        _store.FailWith = null;
        board.ToggleSizeCommand.Execute(board.Items[0]);
        Assert.Null(board.LayoutError);
    }

    [Fact]
    public void Picker_groups_widgets_by_plugin_and_marks_added_ones()
    {
        ContributionViewModel a1 = Widget("a", "1", "M0 0L1 1");
        ContributionViewModel b2 = Widget("b", "2");
        ContributionViewModel a3 = Widget("a", "3");
        _store.Saved = [new("a/1")];
        WidgetBoardViewModel board = Create(a1, b2, a3);

        board.Picker.OpenCommand.Execute(null);

        Assert.True(board.Picker.IsOpen);
        Assert.Equal(["a", "b"], board.Picker.Groups.Select(g => g.Title));
        WidgetPickerGroup groupA = board.Picker.Groups[0];
        Assert.True(groupA.HasIcon);
        Assert.False(board.Picker.Groups[1].HasIcon);
        Assert.Equal(["a/1", "a/3"], groupA.Entries.Select(e => e.Contribution.Key));
        Assert.True(groupA.Entries[0].IsAdded);
        Assert.False(groupA.Entries[1].IsAdded);
    }

    [Fact]
    public void Picker_add_adds_the_widget_and_closes_but_ignores_added_ones()
    {
        ContributionViewModel a1 = Widget("a", "1");
        ContributionViewModel b2 = Widget("b", "2");
        _store.Saved = [new("a/1")];
        WidgetBoardViewModel board = Create(a1, b2);
        board.Picker.OpenCommand.Execute(null);

        board.Picker.AddCommand.Execute(board.Picker.Groups[0].Entries[0]);
        Assert.True(board.Picker.IsOpen);
        Assert.Equal(["a/1"], Keys(board));

        WidgetPickerEntry entry = board.Picker.Groups[1].Entries[0];
        board.Picker.AddCommand.Execute(entry);

        Assert.False(board.Picker.IsOpen);
        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.True(entry.IsAdded);
    }

    [Fact]
    public void Picker_keeps_entries_across_refreshes_and_follows_removals()
    {
        ContributionViewModel a1 = Widget("a", "1");
        WidgetBoardViewModel board = Create(a1);
        board.Picker.OpenCommand.Execute(null);
        WidgetPickerEntry entry = board.Picker.Groups[0].Entries[0];
        Assert.True(entry.IsAdded);

        board.Update([a1]);
        Assert.Same(entry, board.Picker.Groups[0].Entries[0]);

        board.RemoveCommand.Execute(board.Items[0]);
        Assert.False(entry.IsAdded);

        board.Picker.CloseCommand.Execute(null);
        Assert.False(board.Picker.IsOpen);
    }
}
