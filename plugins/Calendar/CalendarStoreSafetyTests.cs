using System.Text.Json;
using Blocker.Plugin.Testing;
using Xunit;

namespace CalendarPlanner.Tests;

/// <summary>Read-only mode for newer or unknown data, the history guarantee, the kept unreadable copy, id counters and entry validation.</summary>
public class CalendarStoreSafetyTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static string Day => Kit.D(2026, 1, 5).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<(FakePluginContext Ctx, CalendarStore Store)> LoadAsync(string? data = null, string? history = null)
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        if (data is not null)
        {
            await ctx.Storage.WriteAsync(CalendarStore.DataKey, data, Ct);
        }

        if (history is not null)
        {
            await ctx.Storage.WriteAsync(CalendarStore.HistoryKey, history, Ct);
        }

        var store = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(Ct);
        return (ctx, store);
    }

    private static async Task AssertNothingCanChangeAsync(FakePluginContext ctx, CalendarStore store, string expectedData)
    {
        Assert.True(store.ReadOnly);
        Assert.Null(await store.AddAsync(Kit.Task("new", Kit.D(2026, 1, 5)), Ct));
        Assert.False(await store.DeleteAsync(1, Ct));
        Assert.False(await store.SkipAsync(1, Kit.D(2026, 1, 5), Ct));
        Assert.False(await store.SetDoneAsync(1, Kit.D(2026, 1, 5), true, Ct));
        Assert.False(await store.ReplaceAsync(Replacement(), Ct));
        Assert.Equal(0, await store.ClearPastAsync(Kit.D(2030, 1, 1), Ct));
        Assert.False(await store.RecordAsync("1:1", SessionOutcome.Started, Kit.Start.ToUnixTimeSeconds(), Ct));
        await store.SetSettingsAsync(new CalendarSettings { AutoStart = false }, Ct);
        await store.FlushAsync(Ct);
        Assert.True(store.Settings.AutoStart);
        Assert.Equal(expectedData, await ctx.Storage.ReadAsync(CalendarStore.DataKey, Ct));
        Assert.Null(await ctx.Storage.ReadAsync(CalendarStore.HistoryKey, Ct));
        Assert.Null(await ctx.Storage.ReadAsync(CalendarStore.SettingsKey, Ct));
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Message.Contains("read-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_newer_document_version_is_read_only_and_never_overwritten()
    {
        string json = "{\"v\":2,\"n\":3,\"e\":[{\"i\":1,\"k\":1,\"t\":\"Monthly\",\"s\":" + Day + ",\"r\":2,\"m\":-1}]}";

        (FakePluginContext ctx, CalendarStore store) = await LoadAsync(json);

        Assert.Equal(["Monthly"], store.Entries.Select(e => e.Title));
        await AssertNothingCanChangeAsync(ctx, store, json);
    }

    [Fact]
    public async Task A_newer_document_that_no_longer_parses_is_read_only_not_replaced_and_not_copied()
    {
        string json = "{\"v\":7,\"n\":3,\"e\":{\"unknown\":\"shape\"}}";

        (FakePluginContext ctx, CalendarStore store) = await LoadAsync(json);

        Assert.False(store.RecoveredFromCorruption);
        Assert.Empty(store.Entries);
        await AssertNothingCanChangeAsync(ctx, store, json);
        Assert.Null(await ctx.Storage.ReadAsync(CalendarStore.DataKey + CalendarStore.CorruptSuffix, Ct));
    }

    [Theory]
    [InlineData("\"k\":9")]
    [InlineData("\"r\":7")]
    [InlineData("\"e\":5")]
    public async Task An_unknown_kind_repeat_or_end_makes_the_calendar_read_only_instead_of_dropping_the_entry(string unknown)
    {
        string json = "{\"v\":1,\"n\":4,\"e\":["
            + "{\"i\":1,\"k\":1,\"t\":\"Known\",\"s\":" + Day + ",\"m\":-1},"
            + "{\"i\":2,\"k\":1,\"t\":\"Future\",\"s\":" + Day + ",\"m\":-1," + unknown + "}]}";

        (FakePluginContext ctx, CalendarStore store) = await LoadAsync(json);

        Assert.Equal(["Known"], store.Entries.Select(e => e.Title));
        await AssertNothingCanChangeAsync(ctx, store, json);
    }

    [Fact]
    public async Task An_unknown_history_outcome_or_a_newer_history_version_makes_the_calendar_read_only()
    {
        (_, CalendarStore unknownOutcome) = await LoadAsync(history: "{\"v\":1,\"h\":[{\"k\":\"1:1\",\"o\":9,\"t\":" + Kit.Start.ToUnixTimeSeconds() + "}]}");
        (_, CalendarStore newer) = await LoadAsync(history: "{\"v\":3,\"h\":[]}");

        Assert.True(unknownOutcome.ReadOnly);
        Assert.True(newer.ReadOnly);
    }

    [Fact]
    public async Task Ordinary_data_is_not_read_only()
    {
        (_, CalendarStore store) = await LoadAsync("{\"v\":1,\"n\":2,\"e\":[{\"i\":1,\"k\":1,\"t\":\"Fine\",\"s\":" + Day + ",\"m\":-1}]}");

        Assert.False(store.ReadOnly);
        Assert.NotNull(await store.AddAsync(Kit.Task("more", Kit.D(2026, 1, 6)), Ct));
    }

    [Fact]
    public async Task A_history_record_that_cannot_be_written_is_refused_and_taken_back()
    {
        (FakePluginContext ctx, CalendarStore store) = await LoadAsync();
        long free = ctx.Storage.QuotaBytes - ctx.FakeStorage.UsedBytes;
        await ctx.Storage.WriteAsync("filler", new string('x', (int)(free - 20) - 6), Ct);

        bool recorded = await store.RecordAsync("1:20000", SessionOutcome.Started, Kit.Start.ToUnixTimeSeconds(), Ct);

        Assert.False(recorded);
        Assert.Empty(store.History);
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Message.Contains("could not be saved", StringComparison.Ordinal));
        _ = await ctx.Storage.DeleteAsync("filler", Ct);
        Assert.True(await store.RecordAsync("1:20000", SessionOutcome.Started, Kit.Start.ToUnixTimeSeconds(), Ct));
        Assert.NotNull(await ctx.Storage.ReadAsync(CalendarStore.HistoryKey, Ct));
    }

    [Fact]
    public async Task An_unreadable_copy_is_cut_to_64_KiB_as_the_storage_counts_it()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        string ascii = "{" + new string('x', 200_000);
        string polish = "{" + string.Concat(Enumerable.Repeat("żółć", 10_000));
        await ctx.Storage.WriteAsync(CalendarStore.DataKey, ascii, Ct);
        await ctx.Storage.WriteAsync(CalendarStore.HistoryKey, polish, Ct);

        await new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger).LoadAsync(Ct);

        string keptAscii = KeptText((await ctx.Storage.ReadAsync(CalendarStore.DataKey + CalendarStore.CorruptSuffix, Ct))!);
        string keptPolish = KeptText((await ctx.Storage.ReadAsync(CalendarStore.HistoryKey + CalendarStore.CorruptSuffix, Ct))!);
        Assert.Equal(CalendarLimits.MaxKeptUnreadableBytes, StoredBytes(keptAscii));
        Assert.StartsWith("{xxx", keptAscii, StringComparison.Ordinal);
        Assert.InRange(StoredBytes(keptPolish), CalendarLimits.MaxKeptUnreadableBytes - 6, CalendarLimits.MaxKeptUnreadableBytes);
        Assert.StartsWith("{żółć", keptPolish, StringComparison.Ordinal);
        Assert.False(keptPolish.Any(char.IsSurrogate));
    }

    [Fact]
    public async Task An_unreadable_copy_is_deleted_after_thirty_days_and_kept_before()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        long now = Kit.Start.ToUnixTimeSeconds();
        await ctx.Storage.WriteAsync(CalendarStore.DataKey + CalendarStore.CorruptSuffix, (now - (31 * 86400L)) + "\nold", Ct);
        await ctx.Storage.WriteAsync(CalendarStore.HistoryKey + CalendarStore.CorruptSuffix, (now - (29 * 86400L)) + "\nrecent", Ct);

        await new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger).LoadAsync(Ct);

        Assert.Null(await ctx.Storage.ReadAsync(CalendarStore.DataKey + CalendarStore.CorruptSuffix, Ct));
        Assert.Equal("recent", KeptText((await ctx.Storage.ReadAsync(CalendarStore.HistoryKey + CalendarStore.CorruptSuffix, Ct))!));
    }

    [Fact]
    public async Task A_copy_kept_by_an_older_version_gets_a_date_and_a_cut_instead_of_living_forever()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        await ctx.Storage.WriteAsync(CalendarStore.DataKey + CalendarStore.CorruptSuffix, "{ raw text " + new string('y', 100_000), Ct);

        await new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger).LoadAsync(Ct);

        string text = KeptText((await ctx.Storage.ReadAsync(CalendarStore.DataKey + CalendarStore.CorruptSuffix, Ct))!);
        Assert.Equal(Kit.Start.ToUnixTimeSeconds(), KeptAt((await ctx.Storage.ReadAsync(CalendarStore.DataKey + CalendarStore.CorruptSuffix, Ct))!));
        Assert.StartsWith("{ raw text", text, StringComparison.Ordinal);
        Assert.Equal(CalendarLimits.MaxKeptUnreadableBytes, StoredBytes(text));
    }

    [Fact]
    public async Task After_an_unreadable_entries_document_new_ids_stay_above_the_ids_the_history_mentions()
    {
        string history = "{\"v\":1,\"h\":[{\"k\":\"12:20450\",\"o\":0,\"t\":" + Kit.Start.ToUnixTimeSeconds() + "},{\"k\":\"3:20451\",\"o\":1,\"t\":" + Kit.Start.ToUnixTimeSeconds() + "},{\"k\":\"junk\",\"o\":0,\"t\":" + Kit.Start.ToUnixTimeSeconds() + "}]}";

        (_, CalendarStore store) = await LoadAsync("{ truncated", history);

        CalendarEntry added = (await store.AddAsync(Kit.Session("Fresh", Kit.D(2026, 1, 5), 600), Ct))!;
        Assert.True(store.RecoveredFromCorruption);
        Assert.Equal(13, added.Id);
    }

    [Fact]
    public async Task The_next_id_is_the_highest_of_the_stored_counter_the_entries_and_the_history()
    {
        string data = "{\"v\":1,\"n\":2,\"e\":[{\"i\":8,\"k\":1,\"t\":\"a\",\"s\":" + Day + ",\"m\":-1}]}";

        (_, CalendarStore store) = await LoadAsync(data);

        Assert.Equal(9, (await store.AddAsync(Kit.Task("b", Kit.D(2026, 1, 5)), Ct))!.Id);
    }

    [Fact]
    public async Task Adding_clamps_and_validates_every_field_like_loading_does()
    {
        (FakePluginContext ctx, CalendarStore store) = await LoadAsync();
        var wild = new CalendarEntry
        {
            Kind = EntryKind.Session,
            Title = "  bell\u0007 \n ",
            StartDay = Kit.D(2026, 1, 5),
            StartMinute = 600,
            Duration = 99_999,
            Repeat = RepeatKind.Weekly,
            Interval = 999,
            WeekdayMask = 0xFFFF,
            End = EndKind.OnDate,
            UntilDay = 1,
            Count = 0,
            Color = -8,
            Created = 5,
            Skipped = [1, 1, 2],
            DoneDays = [3, 3],
        };

        CalendarEntry stored = (await store.AddAsync(wild, Ct))!;

        Assert.Equal(("bell", CalendarLimits.MaxSessionMinutes, CalendarLimits.MaxInterval, 0x7F, 1), (stored.Title, stored.Duration, stored.Interval, stored.WeekdayMask, stored.Count));
        Assert.Equal(stored.StartDay, stored.UntilDay);
        Assert.InRange(stored.Color, 0, CalendarLimits.ColorCount - 1);
        Assert.Equal(Kit.Start.ToUnixTimeSeconds(), stored.Created);
        Assert.Equal([1, 2], stored.Skipped);
        Assert.Equal([3], stored.DoneDays);
        var reloaded = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await reloaded.LoadAsync(Ct);
        Assert.Equivalent(stored, reloaded.Entries.Single());
    }

    [Fact]
    public async Task Adding_refuses_entries_the_loader_would_drop()
    {
        (_, CalendarStore store) = await LoadAsync();

        Assert.Null(await store.AddAsync(Kit.Session("no time", Kit.D(2026, 1, 5), -1), Ct));
        Assert.Null(await store.AddAsync(Kit.Session("late", Kit.D(2026, 1, 5), 1440), Ct));
        Assert.Null(await store.AddAsync(Kit.Task("ancient", 5), Ct));
        Assert.Null(await store.AddAsync(new CalendarEntry { Kind = (EntryKind)9, Title = "x", StartDay = Kit.D(2026, 1, 5) }, Ct));
        Assert.Null(await store.AddAsync(new CalendarEntry { Kind = EntryKind.Task, Repeat = (RepeatKind)4, Title = "x", StartDay = Kit.D(2026, 1, 5) }, Ct));
        Assert.Null(await store.AddAsync(new CalendarEntry { Kind = EntryKind.Task, End = (EndKind)4, Title = "x", StartDay = Kit.D(2026, 1, 5) }, Ct));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task Short_sessions_are_lifted_to_fifteen_minutes_and_one_time_entries_lose_their_end_rule()
    {
        (_, CalendarStore store) = await LoadAsync();
        CalendarEntry once = Kit.Session("Short", Kit.D(2026, 1, 5), 600, 1);
        once.End = EndKind.AfterCount;
        once.Count = 4;

        CalendarEntry stored = (await store.AddAsync(once, Ct))!;

        Assert.Equal((CalendarLimits.MinSessionMinutes, EndKind.Never), (stored.Duration, stored.End));
    }

    [Fact]
    public async Task Replacing_validates_like_adding_and_an_invalid_replacement_changes_nothing()
    {
        (_, CalendarStore store) = await LoadAsync();
        CalendarEntry stored = (await store.AddAsync(Kit.Session("Deep", Kit.D(2026, 1, 5), 600), Ct))!;
        CalendarEntry wild = stored.Clone();
        wild.Duration = 99_999;
        wild.Interval = 0;
        wild.Title = new string('t', 200);
        CalendarEntry invalid = stored.Clone();
        invalid.StartMinute = -1;

        Assert.True(await store.ReplaceAsync(wild, Ct));
        Assert.False(await store.ReplaceAsync(invalid, Ct));

        CalendarEntry now = store.Find(stored.Id)!;
        Assert.Equal((CalendarLimits.MaxSessionMinutes, 1, CalendarLimits.MaxTitleLength, 600), (now.Duration, now.Interval, now.Title.Length, now.StartMinute));
    }

    [Fact]
    public async Task An_edited_occurrence_of_a_session_keeps_a_valid_start_and_length()
    {
        (_, CalendarStore store) = await LoadAsync();
        CalendarEntry series = (await store.AddAsync(Kit.Daily(Kit.Session("Daily", Kit.D(2026, 1, 5), 600)), Ct))!;

        Assert.True(await store.OverrideAsync(series.Id, new OccurrenceOverride { OriginalDay = series.StartDay, Day = series.StartDay, Minute = -1, Duration = 1, Title = "x" }, Ct));

        OccurrenceOverride edit = store.Find(series.Id)!.Overrides.Single();
        Assert.Equal((0, CalendarLimits.MinSessionMinutes), (edit.Minute, edit.Duration));
    }

    private static CalendarEntry Replacement()
    {
        CalendarEntry entry = Kit.Task("replaced", Kit.D(2026, 1, 5));
        entry.Id = 1;
        return entry;
    }

    private static int StoredBytes(string text) => JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length;

    // A kept copy is "<unix seconds>\n<text>".
    private static string KeptText(string stored) => stored[(stored.IndexOf('\n', StringComparison.Ordinal) + 1)..];

    private static long KeptAt(string stored) => long.Parse(stored[..stored.IndexOf('\n', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
}
