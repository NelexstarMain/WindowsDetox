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
        WidgetPlacement[] layout = [new("b/two", WidgetSize.Wide), new("a/one", WidgetSize.Small)];

        store.Save(layout);

        Assert.Equal(layout, new WidgetLayoutStore(FilePath).Load());
        Assert.Contains("\"Wide\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_again_replaces_the_previous_layout()
    {
        WidgetLayoutStore store = new(FilePath);
        store.Save([new("a/one", WidgetSize.Small), new("a/two", WidgetSize.Small)]);

        store.Save([new("a/two", WidgetSize.Wide)]);

        Assert.Equal([new WidgetPlacement("a/two", WidgetSize.Wide)], store.Load());
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
        File.WriteAllText(FilePath, """[{"key":"a/one"},{"key":" "},{"key":"a/one","size":"Wide"},{"key":"a/two","size":"wide"}]""");

        Assert.Equal([new WidgetPlacement("a/one"), new WidgetPlacement("a/two", WidgetSize.Wide)], new WidgetLayoutStore(FilePath).Load());
    }
}
