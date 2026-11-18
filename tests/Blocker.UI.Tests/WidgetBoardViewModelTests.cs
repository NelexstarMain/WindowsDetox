using Blocker.Contracts;
using Blocker.UI.Sdk;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class WidgetBoardViewModelTests
{
    private readonly InMemoryWidgetLayoutStore _store = new();
    private readonly Navigator _navigator = new();
    private readonly List<string> _opened = [];

    public WidgetBoardViewModelTests() => _navigator.PluginPageRequested += (_, pluginId) => _opened.Add(pluginId);

    private static ContributionViewModel Widget(string plugin, string id, string? icon = null, string? title = null, UiNode? tree = null)
    {
        UiContributionDto dto = new(plugin, id, title ?? plugin, UiJson.Serialize(tree ?? Ui.Card(Ui.Text(id))), icon);
        Assert.True(ContributionViewModel.TryCreate(dto, out ContributionViewModel? widget));
        return widget;
    }

    private static UiNode Tall() => Ui.Card(
        Ui.Stat("Today", "0 / 3 tasks", "3 to go"),
        Ui.Progress(0, 3),
        Ui.Toggle("t-1", "a", false),
        Ui.Toggle("t-2", "b", false),
        Ui.Toggle("t-3", "c", false));

    private WidgetBoardViewModel Create(params ContributionViewModel[] available)
    {
        WidgetBoardViewModel board = new(_store, _navigator);
        board.Update(available);
        return board;
    }

    private static string[] Keys(WidgetBoardViewModel board) => [.. board.Items.Select(i => i.Contribution.Key)];

    [Fact]
    public void First_run_shows_every_available_widget_without_saving()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"));

        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.All(board.Items, i => Assert.Equal((1, 1), (i.Cols, i.Rows)));
        Assert.Equal(0, _store.SaveCount);
        Assert.False(board.ShowEmptyState);
    }

    [Fact]
    public void First_run_follows_widgets_that_appear_later()
    {
        WidgetBoardViewModel board = new(_store, _navigator);
        board.Update([]);
        Assert.False(board.HasAvailable);
        Assert.False(board.ShowEmptyState);

        board.Update([Widget("a", "1")]);

        Assert.Equal(["a/1"], Keys(board));
    }

    [Fact]
    public void Widgets_start_with_as_many_rows_as_their_content_needs()
    {
        ContributionViewModel small = Widget("a", "1", tree: Ui.Card(Ui.Stat("Focus today", "30 min", "Last 7 days: 30 min")));
        ContributionViewModel tall = Widget("b", "2", tree: Tall());

        WidgetBoardViewModel board = Create(small, tall);

        Assert.Equal([(1, 1), (1, 2)], board.Items.Select(i => (i.Cols, i.Rows)));
        Assert.Equal("1 × 2", board.Items[1].SpanLabel);
    }

    [Fact]
    public void Saved_layout_decides_which_widgets_show_and_in_which_order_and_span()
    {
        _store.Saved = [new("b/2", 2, 3), new("a/1", 1, 1)];

        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"), Widget("c", "3"));

        Assert.Equal(["b/2", "a/1"], Keys(board));
        Assert.Equal([(2, 3), (1, 1)], board.Items.Select(i => (i.Cols, i.Rows)));
        Assert.Equal("2 × 3", board.Items[0].SpanLabel);
    }

    [Fact]
    public void Widgets_of_missing_plugins_are_skipped_but_kept_in_the_saved_layout()
    {
        _store.Saved = [new("gone/1", 2, 2), new("a/1")];
        ContributionViewModel a2 = Widget("a", "2");
        WidgetBoardViewModel board = Create(Widget("a", "1"), a2);

        Assert.Equal(["a/1"], Keys(board));

        Assert.True(board.Add(a2));

        Assert.Equal(["a/1", "a/2"], Keys(board));
        Assert.Equal([new WidgetPlacement("a/1"), new WidgetPlacement("a/2"), new WidgetPlacement("gone/1", 2, 2)], _store.Saved);
    }

    [Fact]
    public void A_widget_that_returns_reappears_where_it_was_saved()
    {
        _store.Saved = [new("a/1"), new("b/2", 2, 1)];
        ContributionViewModel a1 = Widget("a", "1");
        WidgetBoardViewModel board = Create(a1);
        Assert.Equal(["a/1"], Keys(board));

        board.Update([a1, Widget("b", "2")]);

        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.Equal((2, 1), (board.Items[1].Cols, board.Items[1].Rows));
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

    [Theory]
    [InlineData(0, 2, new[] { "b/2", "c/3", "a/1" })]
    [InlineData(0, 1, new[] { "b/2", "a/1", "c/3" })]
    [InlineData(2, 0, new[] { "c/3", "a/1", "b/2" })]
    [InlineData(1, 0, new[] { "b/2", "a/1", "c/3" })]
    [InlineData(0, 99, new[] { "b/2", "c/3", "a/1" })]
    [InlineData(2, -5, new[] { "c/3", "a/1", "b/2" })]
    public void Move_puts_the_widget_at_the_new_index_and_saves(int from, int to, string[] expected)
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"), Widget("c", "3"));

        Assert.True(board.Move(board.Items[from], to));

        Assert.Equal(expected, Keys(board));
        Assert.Equal(expected, _store.Saved!.Select(p => p.Key));
        Assert.Equal(1, _store.SaveCount);
    }

    [Fact]
    public void Move_to_the_same_place_or_of_an_unknown_widget_does_nothing()
    {
        ContributionViewModel a1 = Widget("a", "1");
        WidgetBoardViewModel board = Create(a1, Widget("b", "2"));
        WidgetItemViewModel stranger = new(Widget("c", "3"), 1, 1);

        Assert.False(board.Move(board.Items[0], 0));
        Assert.False(board.Move(stranger, 0));

        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void Moving_keeps_every_widgets_span()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"));
        board.Resize(board.Items[0], 2, 2);

        board.Move(board.Items[0], 1);

        Assert.Equal([new WidgetPlacement("b/2"), new WidgetPlacement("a/1", 2, 2)], _store.Saved);
    }

    [Fact]
    public void Resize_changes_the_span_and_saves()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"));
        WidgetItemViewModel item = board.Items[0];

        Assert.True(board.Resize(item, 2, 3));

        Assert.Equal((2, 3), (item.Cols, item.Rows));
        Assert.Equal("2 × 3", item.SpanLabel);
        Assert.Equal([new WidgetPlacement("a/1", 2, 3)], _store.Saved);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(9, 9, 2, 3)]
    [InlineData(-3, 2, 1, 2)]
    public void Resize_limits_the_span_to_the_grid(int cols, int rows, int expectedCols, int expectedRows)
    {
        WidgetBoardViewModel board = Create(Widget("a", "1", tree: Tall()));
        board.Resize(board.Items[0], 2, 1);

        board.Resize(board.Items[0], cols, rows);

        Assert.Equal((expectedCols, expectedRows), (board.Items[0].Cols, board.Items[0].Rows));
    }

    [Fact]
    public void A_live_resize_is_saved_only_when_committed()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"));
        WidgetItemViewModel item = board.Items[0];

        Assert.True(board.Resize(item, 2, 1, commit: false));
        Assert.True(board.Resize(item, 2, 2, commit: false));
        Assert.Equal((2, 2), (item.Cols, item.Rows));
        Assert.Equal(0, _store.SaveCount);

        board.Commit();

        Assert.Equal([new WidgetPlacement("a/1", 2, 2)], _store.Saved);
        Assert.Equal(1, _store.SaveCount);
    }

    [Fact]
    public void A_cancelled_live_resize_can_restore_the_start_span_without_saving()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"));
        WidgetItemViewModel item = board.Items[0];
        board.Resize(item, 2, 3, commit: false);

        board.Resize(item, 1, 1, commit: false);

        Assert.Equal((1, 1), (item.Cols, item.Rows));
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void Resizing_to_the_current_span_or_an_unknown_widget_does_nothing()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"));

        Assert.False(board.Resize(board.Items[0], 1, 1));
        Assert.False(board.Resize(new WidgetItemViewModel(Widget("c", "3"), 1, 1), 2, 2));

        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void Span_survives_a_refresh()
    {
        ContributionViewModel a1 = Widget("a", "1");
        WidgetBoardViewModel board = Create(a1);
        board.Resize(board.Items[0], 2, 2);

        board.Update([a1]);

        Assert.Equal((2, 2), (board.Items[0].Cols, board.Items[0].Rows));
    }

    [Fact]
    public void An_uncommitted_resize_survives_a_refresh()
    {
        ContributionViewModel a1 = Widget("a", "1");
        WidgetBoardViewModel board = Create(a1);
        board.Resize(board.Items[0], 2, 2, commit: false);

        board.Update([a1]);

        Assert.Equal((2, 2), (board.Items[0].Cols, board.Items[0].Rows));
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
    public void Add_appends_a_widget_once_with_its_default_span_and_ignores_unknown_ones()
    {
        ContributionViewModel a1 = Widget("a", "1");
        ContributionViewModel b2 = Widget("b", "2", tree: Tall());
        _store.Saved = [new("a/1", 2, 1)];
        WidgetBoardViewModel board = Create(a1, b2);

        Assert.True(board.Add(b2));
        Assert.False(board.Add(b2));
        Assert.False(board.Add(Widget("c", "3")));

        Assert.Equal(["a/1", "b/2"], Keys(board));
        Assert.Equal([new WidgetPlacement("a/1", 2, 1), new WidgetPlacement("b/2", 1, 2)], _store.Saved);
        Assert.Equal(1, _store.SaveCount);
    }

    [Fact]
    public void Layout_is_loaded_once_and_the_first_change_materializes_the_first_run_layout()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1"), Widget("b", "2"));

        board.Resize(board.Items[1], 2, 1);

        Assert.Equal([new WidgetPlacement("a/1"), new WidgetPlacement("b/2", 2, 1)], _store.Saved);
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
        board.Resize(board.Items[0], 2, 1);
        Assert.Null(board.LayoutError);
    }

    [Fact]
    public void Opening_a_widget_asks_the_shell_for_its_plugin_page()
    {
        WidgetBoardViewModel board = Create(Widget("goals", "widget"), Widget("insights", "widget"));

        board.OpenCommand.Execute(board.Items[1]);

        Assert.Equal(["insights"], _opened);
    }

    [Fact]
    public void Opening_a_widget_does_nothing_in_edit_mode()
    {
        WidgetBoardViewModel board = Create(Widget("goals", "widget"));
        board.ToggleEditCommand.Execute(null);

        board.OpenCommand.Execute(board.Items[0]);
        board.OpenCommand.Execute(null);

        Assert.Empty(_opened);
    }

    [Fact]
    public void Items_expose_the_plugin_icon_and_follow_changes_to_it()
    {
        ContributionViewModel a1 = Widget("a", "1", "M0 0L1 1");
        ContributionViewModel b2 = Widget("b", "2");
        WidgetBoardViewModel board = Create(a1, b2);
        List<string?> changed = [];
        board.Items[1].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(board.Items[0].HasIcon);
        Assert.False(board.Items[1].HasIcon);

        b2.Update(new UiContributionDto("b", "2", "b", UiJson.Serialize(Ui.Card(Ui.Text("2"))), "M0 0L2 2"), b2.Node);

        Assert.True(board.Items[1].HasIcon);
        Assert.Contains(nameof(WidgetItemViewModel.HasIcon), changed);
    }
}

public sealed class WidgetPickerViewModelTests
{
    private readonly InMemoryWidgetLayoutStore _store = new();

    private static ContributionViewModel Widget(string plugin, string id, string title, string? icon = null, UiNode? tree = null)
    {
        UiContributionDto dto = new(plugin, id, title, UiJson.Serialize(tree ?? Ui.Card(Ui.Text(id))), icon);
        Assert.True(ContributionViewModel.TryCreate(dto, out ContributionViewModel? widget));
        return widget;
    }

    private WidgetBoardViewModel Create(params ContributionViewModel[] available)
    {
        WidgetBoardViewModel board = new(_store, new Navigator());
        board.Update(available);
        return board;
    }

    private static string[] Titles(WidgetPickerViewModel picker) => [.. picker.Groups.Select(g => g.Title)];

    [Fact]
    public void Picker_groups_widgets_by_plugin_and_marks_added_ones()
    {
        ContributionViewModel a1 = Widget("a", "1", "Alpha", "M0 0L1 1");
        ContributionViewModel b2 = Widget("b", "2", "Beta");
        ContributionViewModel a3 = Widget("a", "3", "Alpha");
        _store.Saved = [new("a/1")];
        WidgetBoardViewModel board = Create(a1, b2, a3);

        board.Picker.OpenCommand.Execute(null);

        Assert.True(board.Picker.IsOpen);
        Assert.Equal(["Alpha", "Beta"], Titles(board.Picker));
        WidgetPickerGroup groupA = board.Picker.Groups[0];
        Assert.True(groupA.HasIcon);
        Assert.False(board.Picker.Groups[1].HasIcon);
        Assert.Equal(["a/1", "a/3"], groupA.Entries.Select(e => e.Contribution.Key));
        Assert.Equal(["2 widgets", "1 widget"], board.Picker.Groups.Select(g => g.CountText));
        Assert.True(groupA.Entries[0].IsAdded);
        Assert.False(groupA.Entries[1].IsAdded);
    }

    [Fact]
    public void The_first_plugin_starts_expanded_and_one_row_is_open_at_a_time()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1", "Alpha"), Widget("b", "2", "Beta"));
        board.Picker.OpenCommand.Execute(null);
        WidgetPickerGroup alpha = board.Picker.Groups[0];
        WidgetPickerGroup beta = board.Picker.Groups[1];
        Assert.Equal([true, false], [alpha.IsExpanded, beta.IsExpanded]);

        board.Picker.ToggleGroupCommand.Execute(beta);
        Assert.Equal([false, true], [alpha.IsExpanded, beta.IsExpanded]);

        board.Picker.ToggleGroupCommand.Execute(beta);
        Assert.Equal([false, false], [alpha.IsExpanded, beta.IsExpanded]);
    }

    [Fact]
    public void Previews_use_the_default_span_and_never_exceed_the_maximum_width()
    {
        ContributionViewModel small = Widget("a", "1", "Alpha", tree: Ui.Card(Ui.Stat("Focus today", "30 min")));
        ContributionViewModel tall = Widget("b", "2", "Beta", tree: Ui.Card(
            Ui.Stat("Today", "0 / 3 tasks", "3 to go"),
            Ui.Progress(0, 3),
            Ui.Toggle("t-1", "a", false),
            Ui.Toggle("t-2", "b", false),
            Ui.Toggle("t-3", "c", false)));
        WidgetBoardViewModel board = Create(small, tall);

        board.Picker.OpenCommand.Execute(null);

        WidgetPickerEntry first = board.Picker.Groups[0].Entries[0];
        WidgetPickerEntry second = board.Picker.Groups[1].Entries[0];
        Assert.Equal(["1 × 1", "1 × 2"], [first.SpanLabel, second.SpanLabel]);
        Assert.Equal((WidgetSpans.SlotWidth(1), WidgetSpans.SlotHeight(2)), (second.SlotWidth, second.SlotHeight));
        Assert.All([first, second], e => Assert.True(e.PreviewWidth <= WidgetPickerEntry.MaxPreviewWidth + 0.001));
        Assert.Equal(first.PreviewWidth / first.PreviewHeight, first.SlotWidth / first.SlotHeight, 6);
    }

    [Fact]
    public void Search_filters_by_plugin_or_widget_name_and_expands_the_matches()
    {
        WidgetBoardViewModel board = Create(
            Widget("goals", "w", "Goals & Tasks"),
            Widget("insights", "w1", "Focus Insights"),
            Widget("insights", "w2", "Weekly Chart"));
        board.Picker.OpenCommand.Execute(null);

        board.Picker.SearchText = "chart";
        Assert.Equal(["Focus Insights"], Titles(board.Picker));
        Assert.Equal(["Weekly Chart"], board.Picker.Groups[0].Visible.Select(e => e.Title));
        Assert.True(board.Picker.Groups[0].IsExpanded);

        board.Picker.SearchText = "GOALS";
        Assert.Equal(["Goals & Tasks"], Titles(board.Picker));

        board.Picker.SearchText = "focus";
        Assert.Equal(["Focus Insights", "Weekly Chart"], board.Picker.Groups[0].Visible.Select(e => e.Title));

        board.Picker.SearchText = "nothing like this";
        Assert.Empty(board.Picker.Groups);
        Assert.True(board.Picker.HasNoResults);

        board.Picker.SearchText = string.Empty;
        Assert.Equal(["Goals & Tasks", "Focus Insights"], Titles(board.Picker));
        Assert.False(board.Picker.HasNoResults);
    }

    [Fact]
    public void Opening_the_picker_clears_the_search()
    {
        WidgetBoardViewModel board = Create(Widget("a", "1", "Alpha"));
        board.Picker.OpenCommand.Execute(null);
        board.Picker.SearchText = "zzz";
        board.Picker.CloseCommand.Execute(null);

        board.Picker.OpenCommand.Execute(null);

        Assert.Equal(string.Empty, board.Picker.SearchText);
        Assert.Single(board.Picker.Groups);
    }

    [Fact]
    public void Picker_add_adds_the_widget_marks_it_added_and_stays_open()
    {
        ContributionViewModel a1 = Widget("a", "1", "Alpha");
        ContributionViewModel b2 = Widget("b", "2", "Beta");
        _store.Saved = [new("a/1")];
        WidgetBoardViewModel board = Create(a1, b2);
        board.Picker.OpenCommand.Execute(null);

        board.Picker.AddCommand.Execute(board.Picker.Groups[0].Entries[0]);
        Assert.Equal(["a/1"], board.Items.Select(i => i.Contribution.Key));

        WidgetPickerEntry entry = board.Picker.Groups[1].Entries[0];
        board.Picker.AddCommand.Execute(entry);

        Assert.True(board.Picker.IsOpen);
        Assert.Equal(["a/1", "b/2"], board.Items.Select(i => i.Contribution.Key));
        Assert.True(entry.IsAdded);
    }

    [Fact]
    public void Picker_keeps_entries_and_expansion_across_refreshes_and_follows_removals()
    {
        ContributionViewModel a1 = Widget("a", "1", "Alpha");
        ContributionViewModel b2 = Widget("b", "2", "Beta");
        WidgetBoardViewModel board = Create(a1, b2);
        board.Picker.OpenCommand.Execute(null);
        board.Picker.ToggleGroupCommand.Execute(board.Picker.Groups[1]);
        WidgetPickerEntry entry = board.Picker.Groups[0].Entries[0];
        Assert.True(entry.IsAdded);

        board.Update([a1, b2, Widget("c", "3", "Gamma")]);

        Assert.Same(entry, board.Picker.Groups[0].Entries[0]);
        Assert.Equal(["Alpha", "Beta", "Gamma"], Titles(board.Picker));
        Assert.True(board.Picker.Groups[1].IsExpanded);

        board.RemoveCommand.Execute(board.Items[0]);
        Assert.False(entry.IsAdded);

        board.Picker.CloseCommand.Execute(null);
        Assert.False(board.Picker.IsOpen);
    }
}
