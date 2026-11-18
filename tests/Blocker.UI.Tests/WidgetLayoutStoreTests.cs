using Blocker.UI.Services;

namespace Blocker.UI.Tests;

/// <summary>Layout store that never touches the disk.</summary>
internal sealed class InMemoryWidgetLayoutStore : IWidgetLayoutStore
{
    public IReadOnlyList<WidgetPlacement>? Saved { get; set; }

    public int SaveCount { get; private set; }

    public Exception? FailWith { get; set; }

    public IReadOnlyList<WidgetPlacement>? Load() => Saved;

    public void Save(IReadOnlyList<WidgetPlacement> layout)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Saved = layout;
        SaveCount++;
    }
}

public sealed class WidgetLayoutStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "widget-store-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "nested", "widgets.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_means_first_run()
    {
        Assert.Null(new WidgetLayoutStore(FilePath).Load());
    }

    [Fact]
    public void Saved_layout_round_trips_in_order_and_creates_the_directory()
    {
        WidgetLayoutStore store = new(FilePath);
        WidgetPlacement[] layout = [new("b/two", 2, 3), new("a/one", 1, 1)];

        store.Save(layout);

        Assert.Equal(layout, new WidgetLayoutStore(FilePath).Load());
        string json = File.ReadAllText(FilePath);
        Assert.Contains("\"cols\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"rows\": 3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_again_replaces_the_previous_layout()
    {
        WidgetLayoutStore store = new(FilePath);
        store.Save([new("a/one"), new("a/two")]);

        store.Save([new("a/two", 2, 1)]);

        Assert.Equal([new WidgetPlacement("a/two", 2, 1)], store.Load());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void An_empty_layout_is_kept_distinct_from_a_missing_file()
    {
        WidgetLayoutStore store = new(FilePath);

        store.Save([]);

        Assert.Empty(store.Load()!);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"widgets\": []}")]
    [InlineData("[{\"key\":\"a/one\",\"size\":\"Gigantic\"}]")]
    public void Unreadable_file_is_treated_as_first_run(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        Assert.Null(new WidgetLayoutStore(FilePath).Load());
    }

    [Fact]
    public void Blank_and_duplicate_keys_are_dropped_on_load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """[{"key":"a/one"},{"key":" "},{"key":"a/one","cols":2},{"key":"a/two","cols":2,"rows":2}]""");

        Assert.Equal([new WidgetPlacement("a/one"), new WidgetPlacement("a/two", 2, 2)], new WidgetLayoutStore(FilePath).Load());
    }

    [Fact]
    public void Files_from_before_spans_existed_read_small_as_one_by_one_and_wide_as_two_by_one()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """[{"key":"a/one","size":"Small"},{"key":"a/two","size":"Wide"},{"key":"a/three","size":"wide"},{"key":"a/four"}]""");

        Assert.Equal(
            [new WidgetPlacement("a/one"), new WidgetPlacement("a/two", 2, 1), new WidgetPlacement("a/three", 2, 1), new WidgetPlacement("a/four")],
            new WidgetLayoutStore(FilePath).Load());
    }

    [Fact]
    public void Explicit_spans_win_over_a_legacy_size_and_are_limited_to_the_grid()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """[{"key":"a/one","size":"Wide","cols":1,"rows":2},{"key":"a/two","cols":9,"rows":9},{"key":"a/three","cols":0,"rows":-1}]""");

        Assert.Equal(
            [new WidgetPlacement("a/one", 1, 2), new WidgetPlacement("a/two", 2, 3), new WidgetPlacement("a/three")],
            new WidgetLayoutStore(FilePath).Load());
    }
}
