using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;
using Xunit;

namespace CalendarPlanner.Tests;

/// <summary>Shared helpers of the Calendar tests.</summary>
public static class Kit
{
    /// <summary>The fake clock's start: 2026-01-01 09:00 UTC, a Thursday.</summary>
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public static TimeZoneInfo Warsaw => CalTime.FindZone("Europe/Warsaw", "Central European Standard Time")!;

    public static TimeZoneInfo NewYork => CalTime.FindZone("America/New_York", "Eastern Standard Time")!;

    public static int D(int year, int month, int day) => new DateOnly(year, month, day).DayNumber;

    public static CalendarEntry Task(string title, int day, int minute = -1) =>
        new() { Kind = EntryKind.Task, Title = title, StartDay = day, StartMinute = minute };

    public static CalendarEntry Session(string title, int day, int minute, int length = 60) =>
        new() { Kind = EntryKind.Session, Title = title, StartDay = day, StartMinute = minute, Duration = length };

    public static CalendarEntry Weekly(CalendarEntry entry, int mask, int every = 1)
    {
        entry.Repeat = RepeatKind.Weekly;
        entry.WeekdayMask = mask;
        entry.Interval = every;
        return entry;
    }

    public static CalendarEntry Daily(CalendarEntry entry, int every = 1)
    {
        entry.Repeat = RepeatKind.Daily;
        entry.Interval = every;
        return entry;
    }

    public static int[] Days(IEnumerable<Occurrence> occurrences) => [.. occurrences.Select(o => o.Day)];

    public static async Task<(FakePluginContext Context, CalendarStore Store)> NewStoreAsync(TimeZoneInfo? zone = null)
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        if (zone is not null)
        {
            ctx.FakeTime.SetLocalTimeZone(zone);
        }

        var store = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);
        return (ctx, store);
    }

    public static async Task<FakePluginContext> StartPluginAsync(params PluginCapability[] capabilities)
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar", capabilities);
        await new CalendarPlugin().InitializeAsync(ctx, CancellationToken.None);
        return ctx;
    }

    public static IEnumerable<UiNode> Descendants(UiNode node)
    {
        yield return node;
        IReadOnlyList<UiNode> children = node switch
        {
            UiPage page => page.Children,
            UiStack stack => stack.Children,
            UiGrid grid => grid.Children,
            UiCard card => card.Children,
            _ => Array.Empty<UiNode>(),
        };
        foreach (UiNode child in children)
        {
            foreach (UiNode inner in Descendants(child))
            {
                yield return inner;
            }
        }
    }

    public static UiButton Button(UiNode root, string id) => Descendants(root).OfType<UiButton>().Single(b => b.Id == id);

    public static bool HasButton(UiNode root, string id) => Descendants(root).OfType<UiButton>().Any(b => b.Id == id);

    public static bool HasText(UiNode root, string text) => Descendants(root).OfType<UiText>().Any(t => t.Text == text);

    public static bool HasTextContaining(UiNode root, string text) => Descendants(root).OfType<UiText>().Any(t => t.Text.Contains(text, StringComparison.Ordinal));

    public static UiStatTile[] Tiles(UiPage page) => [.. Assert.IsType<UiGrid>(page.Children.OfType<UiGrid>().First()).Children.Cast<UiStatTile>()];

    public static SessionSnapshot Running() =>
        new(SessionPhase.Active, Guid.NewGuid(), TimeSpan.FromHours(1), null, 2, 2, TimeSpan.FromMinutes(10));

    public static Dictionary<string, string> Form(
        string kind = "session",
        string title = "Deep work",
        string date = "2026-01-02",
        string time = "10:00",
        string length = "60",
        string repeat = "once",
        string every = "1",
        string end = "never",
        string until = "",
        string count = "10",
        string color = "0",
        string? scope = null,
        params int[] weekdays)
    {
        Dictionary<string, string> values = new()
        {
            [CalendarForm.KindId] = kind,
            [CalendarForm.TitleId] = title,
            [CalendarForm.DateId] = date,
            [CalendarForm.TimeId] = time,
            [CalendarForm.LengthId] = length,
            [CalendarForm.RepeatId] = repeat,
            [CalendarForm.EveryId] = every,
            [CalendarForm.EndId] = end,
            [CalendarForm.UntilId] = until,
            [CalendarForm.CountId] = count,
            [CalendarForm.ColorId] = color,
        };
        for (int i = 0; i < 7; i++)
        {
            values[CalendarForm.WeekdayPrefix + i] = weekdays.Contains(i) ? "true" : "false";
        }

        if (scope is not null)
        {
            values[CalendarForm.ScopeId] = scope;
        }

        return values;
    }

    public static FormOutcome Submit(Dictionary<string, string> values, IReadOnlyList<CalendarEntry>? entries = null, CalendarDraft? draft = null, DateTimeOffset? now = null, TimeZoneInfo? zone = null) =>
        CalendarForm.Submit(values, draft ?? new CalendarDraft { Day = D(2026, 1, 2) }, entries ?? [], now ?? Start, zone ?? TimeZoneInfo.Utc, new CalendarSettings());
}

public class CalendarTimeTests
{
    [Fact]
    public void Day_numbers_are_monday_based()
    {
        Assert.Equal(3, CalTime.Weekday(Kit.D(2026, 1, 1)));
        Assert.Equal("Thu", CalTime.DayName(Kit.D(2026, 1, 1)));
        Assert.Equal(0, CalTime.Weekday(Kit.D(2026, 1, 5)));
        Assert.Equal(6, CalTime.Weekday(Kit.D(2026, 3, 1)));
    }

    [Theory]
    [InlineData("2026-03-09", 2026, 3, 9)]
    [InlineData("2026/03/09", 2026, 3, 9)]
    [InlineData("2026.03.09", 2026, 3, 9)]
    [InlineData("  2026-3-9 ", 2026, 3, 9)]
    [InlineData("2026-02-31", 2026, 2, 28)]
    [InlineData("2026-02-29", 2026, 2, 28)]
    [InlineData("2028-02-30", 2028, 2, 29)]
    [InlineData("2026-04-31", 2026, 4, 30)]
    [InlineData("2026-12-99", 2026, 12, 31)]
    public void Dates_parse_and_impossible_days_are_clamped_to_the_end_of_the_month(string text, int year, int month, int day)
    {
        Assert.True(CalTime.TryParseDate(text, out int result));
        Assert.Equal(Kit.D(year, month, day), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("2026-13-01")]
    [InlineData("2026-00-10")]
    [InlineData("2026-03-00")]
    [InlineData("1999-12-31")]
    [InlineData("2101-01-01")]
    [InlineData("26-03-09")]
    [InlineData("2026-03")]
    [InlineData("tomorrow")]
    [InlineData("2026-03-09-01")]
    [InlineData("2026-x-09")]
    [InlineData("-2026-03-09")]
    public void Invalid_dates_are_rejected(string? text) => Assert.False(CalTime.TryParseDate(text, out _));

    [Theory]
    [InlineData("9:05", 545)]
    [InlineData("09:05", 545)]
    [InlineData("00:00", 0)]
    [InlineData("23:59", 1439)]
    [InlineData("17:30", 1050)]
    [InlineData("9:05 pm", 1265)]
    [InlineData("9:05PM", 1265)]
    [InlineData("12:00 am", 0)]
    [InlineData("12:30 pm", 750)]
    [InlineData("1:00 am", 60)]
    public void Times_parse(string text, int minute)
    {
        Assert.True(CalTime.TryParseTime(text, out int result));
        Assert.Equal(minute, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("9:60")]
    [InlineData("9:5")]
    [InlineData("9")]
    [InlineData("13:00 pm")]
    [InlineData("0:30 am")]
    [InlineData("ab:cd")]
    [InlineData("9:05:00")]
    public void Invalid_times_are_rejected(string text) => Assert.False(CalTime.TryParseTime(text, out _));

    [Theory]
    [InlineData(0, true, "00:00")]
    [InlineData(545, true, "09:05")]
    [InlineData(1050, true, "17:30")]
    [InlineData(0, false, "12:00 AM")]
    [InlineData(750, false, "12:30 PM")]
    [InlineData(785, false, "1:05 PM")]
    [InlineData(1439, false, "11:59 PM")]
    public void Clocks_format(int minute, bool h24, string expected) => Assert.Equal(expected, CalTime.Clock(minute, h24));

    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(60, "1 h")]
    [InlineData(90, "1 h 30 min")]
    [InlineData(120, "2 h")]
    public void Lengths_format(int minutes, string expected) => Assert.Equal(expected, CalTime.Length(minutes));

    [Theory]
    [InlineData(10, "now")]
    [InlineData(59, "now")]
    [InlineData(60, "1 min")]
    [InlineData(42 * 60, "42 min")]
    [InlineData(99 * 60, "99 min")]
    [InlineData(100 * 60, "1.6 h")]
    [InlineData(5 * 3600, "5 h")]
    [InlineData(47 * 3600, "47 h")]
    [InlineData(48 * 3600, "2 d")]
    [InlineData(100 * 3600, "4 d")]
    public void Countdowns_are_short(int seconds, string expected)
    {
        string text = CalTime.Countdown(TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, text);
        Assert.True(text.Length <= 6);
    }

    [Fact]
    public void Names_and_titles_are_english()
    {
        int day = Kit.D(2026, 3, 9);

        Assert.Equal("Mon 9 Mar", CalTime.ShortDate(day));
        Assert.Equal("Monday 9 March", CalTime.LongDate(day));
        Assert.Equal("March 2026", CalTime.MonthTitle(day));
        Assert.Equal("2026-03-09", CalTime.IsoDate(day));
    }

    [Fact]
    public void Weeks_start_on_monday_or_sunday()
    {
        int wednesday = Kit.D(2026, 3, 11);

        Assert.Equal(Kit.D(2026, 3, 9), CalTime.WeekStart(wednesday, sundayFirst: false));
        Assert.Equal(Kit.D(2026, 3, 8), CalTime.WeekStart(wednesday, sundayFirst: true));
        Assert.Equal(Kit.D(2026, 3, 8), CalTime.WeekStart(Kit.D(2026, 3, 8), sundayFirst: true));
        Assert.Equal(Kit.D(2026, 3, 2), CalTime.WeekStart(Kit.D(2026, 3, 8), sundayFirst: false));
    }

    [Fact]
    public void The_month_grid_has_six_weeks_that_cover_the_month()
    {
        IReadOnlyList<int> monday = CalTime.MonthGrid(Kit.D(2026, 3, 15), sundayFirst: false);
        IReadOnlyList<int> sunday = CalTime.MonthGrid(Kit.D(2026, 3, 15), sundayFirst: true);

        Assert.Equal(42, monday.Count);
        Assert.Equal(Kit.D(2026, 2, 23), monday[0]);
        Assert.Equal(Kit.D(2026, 3, 1), sunday[0]);
        Assert.Contains(Kit.D(2026, 3, 31), monday);
        Assert.Contains(Kit.D(2026, 3, 31), sunday);
        Assert.Equal(Enumerable.Range(monday[0], 42), monday);
    }

    [Fact]
    public void Month_arithmetic_works_from_the_first_of_the_month()
    {
        Assert.Equal(Kit.D(2026, 2, 1), CalTime.AddMonths(Kit.D(2026, 1, 31), 1));
        Assert.Equal(Kit.D(2025, 12, 1), CalTime.AddMonths(Kit.D(2026, 1, 15), -1));
        Assert.Equal(Kit.D(2027, 1, 1), CalTime.AddMonths(Kit.D(2026, 12, 31), 1));
        Assert.Equal(Kit.D(2100, 12, 1), CalTime.AddMonths(Kit.D(2100, 12, 15), 1));
        Assert.Equal(Kit.D(2000, 1, 1), CalTime.AddMonths(Kit.D(2000, 1, 15), -1));
        Assert.Equal(29, CalTime.DaysInMonth(Kit.D(2028, 2, 10)));
        Assert.Equal(28, CalTime.DaysInMonth(Kit.D(2026, 2, 10)));
        Assert.True(CalTime.SameMonth(Kit.D(2026, 3, 1), Kit.D(2026, 3, 31)));
        Assert.False(CalTime.SameMonth(Kit.D(2026, 3, 31), Kit.D(2026, 4, 1)));
    }

    [Fact]
    public void A_local_time_in_winter_and_summer_maps_to_different_instants_in_warsaw()
    {
        DateTimeOffset winter = CalTime.ToUtc(Kit.D(2026, 1, 12), 9 * 60, Kit.Warsaw);
        DateTimeOffset summer = CalTime.ToUtc(Kit.D(2026, 7, 13), 9 * 60, Kit.Warsaw);

        Assert.Equal(new DateTimeOffset(2026, 1, 12, 8, 0, 0, TimeSpan.Zero), winter);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 7, 0, 0, TimeSpan.Zero), summer);
    }

    [Fact]
    public void A_time_inside_the_spring_forward_gap_moves_to_the_first_minute_after_it()
    {
        DateTimeOffset start = CalTime.ToUtc(Kit.D(2026, 3, 29), (2 * 60) + 30, Kit.Warsaw);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(3 * 60, CalTime.MinuteOf(start, Kit.Warsaw));
    }

    [Fact]
    public void A_time_that_happens_twice_in_autumn_means_the_first_time()
    {
        DateTimeOffset start = CalTime.ToUtc(Kit.D(2026, 10, 25), (2 * 60) + 30, Kit.Warsaw);

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void New_york_changes_on_a_different_date_than_warsaw()
    {
        DateTimeOffset before = CalTime.ToUtc(Kit.D(2026, 3, 2), 9 * 60, Kit.NewYork);
        DateTimeOffset after = CalTime.ToUtc(Kit.D(2026, 3, 9), 9 * 60, Kit.NewYork);

        Assert.Equal(new DateTimeOffset(2026, 3, 2, 14, 0, 0, TimeSpan.Zero), before);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 13, 0, 0, TimeSpan.Zero), after);
    }

    [Fact]
    public void Day_and_minute_of_an_instant_follow_the_zone()
    {
        var late = new DateTimeOffset(2026, 1, 12, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal(Kit.D(2026, 1, 12), CalTime.DayOf(late, TimeZoneInfo.Utc));
        Assert.Equal(Kit.D(2026, 1, 13), CalTime.DayOf(late, Kit.Warsaw));
        Assert.Equal(30, CalTime.MinuteOf(late, Kit.Warsaw));
        Assert.Equal((23 * 60) + 30, CalTime.MinuteOf(late, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Unknown_zone_ids_give_null_and_the_first_known_one_wins()
    {
        Assert.Null(CalTime.FindZone("Nowhere/Land"));
        Assert.NotNull(CalTime.FindZone("Nowhere/Land", "UTC"));
    }
}

public class CalendarRecurrenceTests
{
    private static readonly int Mon = Kit.D(2026, 1, 5);

    private static int[] Expand(CalendarEntry entry, int from, int to) => Kit.Days(CalendarRecurrence.Expand([entry], from, to));

    [Fact]
    public void A_one_time_entry_appears_only_on_its_day()
    {
        CalendarEntry entry = Kit.Task("a", Mon);

        Assert.Equal([Mon], Expand(entry, Mon - 3, Mon + 3));
        Assert.Empty(Expand(entry, Mon + 1, Mon + 10));
        Assert.Empty(Expand(entry, Mon - 10, Mon - 1));
    }

    [Fact]
    public void Daily_every_three_days()
    {
        CalendarEntry entry = Kit.Daily(Kit.Task("a", Kit.D(2026, 1, 1)), 3);

        Assert.Equal([Kit.D(2026, 1, 1), Kit.D(2026, 1, 4), Kit.D(2026, 1, 7), Kit.D(2026, 1, 10)], Expand(entry, Kit.D(2026, 1, 1), Kit.D(2026, 1, 11)));
    }

    [Fact]
    public void A_range_far_after_the_start_keeps_the_phase()
    {
        CalendarEntry entry = Kit.Daily(Kit.Task("a", Kit.D(2026, 1, 1)), 3);

        int[] days = Expand(entry, Kit.D(2026, 2, 1), Kit.D(2026, 2, 7));

        Assert.Equal([Kit.D(2026, 2, 3), Kit.D(2026, 2, 6)], days);
    }

    [Fact]
    public void Daily_ends_after_a_count()
    {
        CalendarEntry entry = Kit.Daily(Kit.Task("a", Mon));
        entry.End = EndKind.AfterCount;
        entry.Count = 4;

        Assert.Equal([Mon, Mon + 1, Mon + 2, Mon + 3], Expand(entry, Mon - 5, Mon + 100));
        Assert.Empty(Expand(entry, Mon + 4, Mon + 100));
    }

    [Fact]
    public void Daily_ends_on_a_date_inclusive()
    {
        CalendarEntry entry = Kit.Daily(Kit.Task("a", Mon), 2);
        entry.End = EndKind.OnDate;
        entry.UntilDay = Mon + 6;

        Assert.Equal([Mon, Mon + 2, Mon + 4, Mon + 6], Expand(entry, Mon, Mon + 50));
    }

    [Fact]
    public void Weekly_on_monday_and_wednesday()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000101);

        Assert.Equal([Mon, Mon + 2, Mon + 7, Mon + 9], Expand(entry, Mon, Mon + 13));
    }

    [Fact]
    public void Weekly_with_no_weekday_uses_the_weekday_of_the_start()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon + 3), 0);

        Assert.Equal([Mon + 3, Mon + 10, Mon + 17], Expand(entry, Mon, Mon + 20));
    }

    [Fact]
    public void Weekly_never_includes_days_before_the_start_in_its_first_week()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon + 2), 0b0010101);

        Assert.Equal([Mon + 2, Mon + 4, Mon + 7], Expand(entry, Mon, Mon + 8));
    }

    [Fact]
    public void Every_two_weeks()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000001, 2);

        Assert.Equal([Mon, Mon + 14, Mon + 28], Expand(entry, Mon, Mon + 30));
    }

    [Fact]
    public void Every_two_weeks_keeps_its_phase_when_the_range_starts_later()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000101, 2);

        Assert.Equal([Mon + 28, Mon + 30, Mon + 42, Mon + 44], Expand(entry, Mon + 21, Mon + 45));
    }

    [Fact]
    public void Weekly_ends_after_a_count_of_occurrences_not_weeks()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000101);
        entry.End = EndKind.AfterCount;
        entry.Count = 3;

        Assert.Equal([Mon, Mon + 2, Mon + 7], Expand(entry, Mon, Mon + 100));
        Assert.Equal([Mon + 7], Expand(entry, Mon + 5, Mon + 100));
    }

    [Fact]
    public void Weekly_ends_on_a_date()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b1111111);
        entry.End = EndKind.OnDate;
        entry.UntilDay = Mon + 8;

        Assert.Equal(9, Expand(entry, Mon, Mon + 30).Length);
    }

    [Fact]
    public void An_every_52_weeks_series_still_finds_its_next_day()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000001, 52);

        Assert.Equal([Mon + (52 * 7)], Expand(entry, Mon + 300, Mon + 400));
    }

    [Fact]
    public void A_skipped_occurrence_disappears()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000001);
        entry.Skipped.Add(Mon + 7);

        Assert.Equal([Mon, Mon + 14], Expand(entry, Mon, Mon + 14));
    }

    [Fact]
    public void An_edited_occurrence_moves_to_its_new_day_and_replaces_the_original()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Session("Focus", Mon, 540), 0b0000001);
        entry.Overrides.Add(new OccurrenceOverride { OriginalDay = Mon + 7, Day = Mon + 9, Minute = 600, Duration = 45, Title = "Moved" });

        IReadOnlyList<Occurrence> items = CalendarRecurrence.Expand([entry], Mon, Mon + 14);

        Assert.Equal([Mon, Mon + 9, Mon + 14], Kit.Days(items));
        Occurrence moved = items.Single(o => o.IsOverride);
        Assert.Equal((Mon + 7, 600, 45, "Moved"), (moved.OriginalDay, moved.StartMinute, moved.Duration, moved.Title));
        Assert.Equal($"{entry.Id}:{Mon + 7}", moved.Key);
    }

    [Fact]
    public void An_edited_occurrence_moved_into_the_range_from_outside_is_found()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000001);
        entry.Overrides.Add(new OccurrenceOverride { OriginalDay = Mon + 28, Day = Mon + 3, Minute = -1, Duration = 0, Title = "Early" });

        Assert.Equal([Mon + 3], Expand(entry, Mon + 1, Mon + 5));
        Assert.DoesNotContain(Mon + 28, Expand(entry, Mon + 20, Mon + 40));
    }

    [Fact]
    public void An_edit_of_a_day_that_is_not_in_the_series_is_ignored()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000001);
        entry.Overrides.Add(new OccurrenceOverride { OriginalDay = Mon + 3, Day = Mon + 4, Minute = -1, Title = "Ghost" });

        Assert.DoesNotContain(Mon + 4, Expand(entry, Mon, Mon + 14));
    }

    [Fact]
    public void A_skipped_day_hides_its_edit()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000001);
        entry.Skipped.Add(Mon + 7);
        entry.Overrides.Add(new OccurrenceOverride { OriginalDay = Mon + 7, Day = Mon + 8, Minute = -1, Title = "x" });

        Assert.Equal([Mon, Mon + 14], Expand(entry, Mon, Mon + 14));
    }

    [Fact]
    public void Done_days_mark_the_matching_occurrence()
    {
        CalendarEntry entry = Kit.Daily(Kit.Task("a", Mon));
        entry.DoneDays.Add(Mon + 1);

        IReadOnlyList<Occurrence> items = CalendarRecurrence.Expand([entry], Mon, Mon + 2);

        Assert.Equal([false, true, false], items.Select(o => o.Done));
    }

    [Fact]
    public void Occurrences_are_ordered_by_day_then_time_with_untimed_first()
    {
        CalendarEntry late = Kit.Task("late", Mon, 900);
        late.Id = 1;
        CalendarEntry early = Kit.Task("early", Mon, 480);
        early.Id = 2;
        CalendarEntry untimed = Kit.Task("untimed", Mon);
        untimed.Id = 3;
        CalendarEntry next = Kit.Task("next", Mon + 1, 60);
        next.Id = 4;

        string[] titles = [.. CalendarRecurrence.Expand([late, next, early, untimed], Mon, Mon + 1).Select(o => o.Title)];

        Assert.Equal(["untimed", "early", "late", "next"], titles);
    }

    [Fact]
    public void A_range_is_capped_at_the_span_limit()
    {
        CalendarEntry entry = Kit.Daily(Kit.Task("a", Mon));

        Assert.Equal(CalendarLimits.MaxSpanDays, Expand(entry, Mon, Mon + 5000).Length);
        Assert.Empty(Expand(entry, Mon + 5, Mon));
    }

    [Fact]
    public void Series_membership_ignores_skips()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Task("a", Mon), 0b0000001);
        entry.Skipped.Add(Mon + 7);

        Assert.True(CalendarRecurrence.IsSeriesDay(entry, Mon + 7));
        Assert.False(CalendarRecurrence.IsSeriesDay(entry, Mon + 8));
        Assert.False(CalendarRecurrence.IsSeriesDay(entry, Mon - 7));
    }

    [Fact]
    public void A_weekly_session_keeps_its_wall_clock_time_across_the_warsaw_dst_change()
    {
        CalendarEntry entry = Kit.Weekly(Kit.Session("Focus", Kit.D(2026, 3, 23), 540), 0b0000001);

        Occurrence[] items = [.. CalendarRecurrence.Expand([entry], Kit.D(2026, 3, 23), Kit.D(2026, 4, 6))];
        DateTimeOffset[] starts = [.. items.Select(o => CalendarRecurrence.StartUtc(o, Kit.Warsaw))];

        Assert.Equal(
            [new DateTimeOffset(2026, 3, 23, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 30, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 4, 6, 7, 0, 0, TimeSpan.Zero)],
            starts);
    }

    [Fact]
    public void A_session_that_ends_over_midnight_ends_the_next_day()
    {
        CalendarEntry entry = Kit.Session("Late", Mon, (23 * 60) + 30, 120);
        Occurrence occurrence = CalendarRecurrence.Expand([entry], Mon, Mon).Single();

        Assert.Equal(new DateTimeOffset(2026, 1, 6, 1, 30, 0, TimeSpan.Zero), CalendarRecurrence.EndUtc(occurrence, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Overlapping_sessions_are_found_and_touching_ones_are_not()
    {
        CalendarEntry existing = Kit.Session("Morning", Mon, 540, 120);
        existing.Id = 1;
        CalendarEntry overlapping = Kit.Session("Clash", Mon, 600, 60);
        CalendarEntry touching = Kit.Session("Next", Mon, 660, 60);

        string? clash = CalendarRecurrence.FindSessionOverlap(overlapping, [existing], Mon, 30, TimeZoneInfo.Utc, true);
        string? fine = CalendarRecurrence.FindSessionOverlap(touching, [existing], Mon, 30, TimeZoneInfo.Utc, true);

        Assert.Equal("Overlaps \"Morning\" on Mon 5 Jan at 09:00.", clash);
        Assert.Null(fine);
    }

    [Fact]
    public void A_series_is_checked_against_every_occurrence_and_never_against_itself()
    {
        CalendarEntry existing = Kit.Weekly(Kit.Session("Weekly", Mon, 540, 60), 0b0000001);
        existing.Id = 1;
        CalendarEntry sameSeries = existing.Clone();
        CalendarEntry later = Kit.Session("Third monday", Mon + 14, 570, 30);

        Assert.Null(CalendarRecurrence.FindSessionOverlap(sameSeries, [existing], Mon, 60, TimeZoneInfo.Utc, true));
        Assert.NotNull(CalendarRecurrence.FindSessionOverlap(later, [existing], Mon, 60, TimeZoneInfo.Utc, true));
    }

    [Fact]
    public void Tasks_never_clash_with_sessions()
    {
        CalendarEntry session = Kit.Session("Focus", Mon, 540, 120);
        session.Id = 1;
        CalendarEntry task = Kit.Task("Call", Mon, 560);

        Assert.Null(CalendarRecurrence.FindSessionOverlap(task, [session], Mon, 30, TimeZoneInfo.Utc, true));
    }

    [Fact]
    public void Overlap_is_decided_by_instants_so_zones_matter()
    {
        CalendarEntry existing = Kit.Session("A", Kit.D(2026, 3, 29), 60, 120);
        existing.Id = 1;
        CalendarEntry candidate = Kit.Session("B", Kit.D(2026, 3, 29), 150, 30);

        // 01:00 local is 00:00 UTC, so A runs until 02:00 UTC. 02:30 local does not exist and becomes 03:00 local = 01:00 UTC: inside A.
        Assert.NotNull(CalendarRecurrence.FindSessionOverlap(candidate, [existing], Kit.D(2026, 3, 29), 3, Kit.Warsaw, true));
    }
}
