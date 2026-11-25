using Blocker.Plugin.Testing;
using Blocker.Sdk.Events;
using Blocker.Sdk.Model;
using CalendarPlanner;
using GoalsTasks;

namespace Blocker.UI.Render;

/// <summary>
/// Fills the bundled plugins with a realistic week through their own stores and events, so renders look like a used app.
/// Plugin clocks only move forward, so history is written oldest first and the clock ends at the fixed "now".
/// </summary>
internal static class PluginSeeds
{
    private static readonly string[] BlockedApps = ["discord.exe", "steam.exe", "spotify.exe", "slack.exe", "chrome.exe", "telegram.exe"];

    public static async Task SeedAsync(string folder, FakePluginContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (folder)
        {
            case "Calendar":
                await SeedCalendarAsync(context, now, cancellationToken);
                break;
            case "GoalsTasks":
                await SeedGoalsAsync(context, now, cancellationToken);
                break;
        }
    }

    // A work week: recurring focus blocks, a few multi-hour sessions and tasks. Monday 23 November is "today"; 10:30.
    private static async Task SeedCalendarAsync(FakePluginContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        context.FakeTime.SetUtcNow(now);
        var store = new CalendarStore(context.Storage, context.Time, context.Logger);
        await store.LoadAsync(cancellationToken);
        int today = DateOnly.FromDateTime(now.DateTime).DayNumber;
        const int Mon = 1, Tue = 2, Wed = 4, Thu = 8, Fri = 16;

        _ = await store.AddAsync(Session("Deep work", today - 7, 9 * 60, 120, color: 0, weekly: Mon | Wed | Fri), cancellationToken);
        _ = await store.AddAsync(Task("Team standup", today - 7, 11 * 60, 30, color: 2, weekly: Mon | Tue | Wed | Thu | Fri), cancellationToken);
        _ = await store.AddAsync(Session("Study: Rust ownership", today - 7, 19 * 60, 90, color: 1, weekly: Tue | Thu), cancellationToken);
        _ = await store.AddAsync(Session("Write release notes", today, 14 * 60, 180, color: 3), cancellationToken);
        _ = await store.AddAsync(Session("Architecture review prep", today + 1, 13 * 60, 240, color: 4), cancellationToken);
        _ = await store.AddAsync(Task("Dentist", today + 2, (16 * 60) + 30, 60, color: 5), cancellationToken);
        _ = await store.AddAsync(Task("Release v2.1", today + 3, 12 * 60, 30, color: 2), cancellationToken);
        _ = await store.AddAsync(Task("Weekly review", today + 4, 17 * 60, 45, color: 1), cancellationToken);
        _ = await store.AddAsync(Session("Read: Designing Data-Intensive Applications", today + 5, 10 * 60, 90, color: 0), cancellationToken);
        _ = await store.AddAsync(Task("Extra one", today, 16 * 60, 30, color: 1), cancellationToken);
        _ = await store.AddAsync(Task("Extra two", today, 17 * 60, 30, color: 2), cancellationToken);
        _ = await store.AddAsync(Task("Plan next sprint", today - 2, 15 * 60, 60, color: 3), cancellationToken);
        await store.FlushAsync(cancellationToken);
    }

    // Seven days of tasks; the clock walks through them because a store only edits the current day.
    private static async Task SeedGoalsAsync(FakePluginContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var store = new GoalsStore(context.Storage, context.Time, context.Logger);
        context.FakeTime.SetUtcNow(now.Date.AddDays(-6).AddHours(9));
        await store.LoadAsync(cancellationToken);
        await store.SetDefaultGoalAsync(4, cancellationToken);
        string[][] days =
        [
            ["Outline the migration plan", "Review two pull requests", "Reply to customer emails", "Refactor the settings page", "Read the RFC"],
            ["Finish the migration plan", "Fix the flaky integration test", "Update the changelog", "Plan the demo"],
            ["Prepare the demo", "Triage new issues", "Write the unit tests", "Walk for 30 minutes", "Pay the invoices"],
            ["Demo to the team", "Collect feedback", "Archive old branches", "Book the dentist"],
            ["Draft the release notes", "Pair on the importer", "Clean the inbox", "Answer the recruiter"],
            ["Groceries", "Laundry", "Read 30 pages", "Call the parents"],
            ["Review pull requests", "Write release notes", "Plan the sprint", "Reply to customer emails", "Book the dentist"],
        ];
        int[] doneCounts = [4, 3, 4, 4, 4, 4, 2];
        for (int offset = 0; offset < days.Length; offset++)
        {
            DateTimeOffset at = now.Date.AddDays(offset - 6).AddHours(9);
            context.FakeTime.SetUtcNow(at);
            if (offset == days.Length - 1)
            {
                _ = await store.SetTodayGoalAsync(4, cancellationToken);
            }

            foreach (string text in days[offset])
            {
                _ = await store.AddTaskAsync(text, cancellationToken);
            }

            foreach (TaskItem task in store.GetView().Tasks.Take(doneCounts[offset]))
            {
                _ = await store.SetDoneAsync(task.Id, true, cancellationToken);
            }

        }

        await store.FlushAsync(cancellationToken);
        context.FakeTime.SetUtcNow(now);
    }

    /// <summary>Publishes three weeks of focus sessions and blocked attempts to an already running Focus Insights or Focus Forest plugin.</summary>
    public static async Task ReplaySessionsAsync(FakePluginContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var random = new Random(2026);
        for (int back = 20; back >= 1; back--)
        {
            DateTimeOffset day = now.Date.AddDays(-back);
            bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            int sessions = weekend ? random.Next(0, 2) : random.Next(2, 5);
            int hour = 8 + random.Next(0, 2);
            for (int s = 0; s < sessions; s++)
            {
                int planned = new[] { 45, 60, 90, 120 }[random.Next(4)];
                DateTimeOffset start = day.AddHours(hour).AddMinutes(random.Next(0, 30));
                bool endedEarly = random.Next(10) == 0;
                int actual = endedEarly ? planned / 3 : planned;
                await ReplaySessionAsync(context, start, planned, actual, endedEarly, random, cancellationToken);
                hour += (planned / 60) + 1 + random.Next(0, 2);
                if (hour > 20)
                {
                    break;
                }
            }
        }

        DateTimeOffset todayStart = now.Date.AddHours(8).AddMinutes(40);
        await ReplaySessionAsync(context, todayStart, 90, 90, false, random, cancellationToken);
        context.FakeTime.SetUtcNow(now);
    }

    private static async Task ReplaySessionAsync(FakePluginContext context, DateTimeOffset start, int plannedMinutes, int actualMinutes, bool endedEarly, Random random, CancellationToken cancellationToken)
    {
        var id = new Guid(random.Next(), 0, 0, [1, 2, 3, 4, 5, 6, 7, 8]);
        context.FakeTime.SetUtcNow(start);
        await context.PublishAsync(new SessionStarted(id, TimeSpan.FromMinutes(plannedMinutes), start), cancellationToken);
        int attempts = random.Next(2, 9);
        for (int i = 0; i < attempts; i++)
        {
            DateTimeOffset at = start.AddMinutes(random.Next(1, Math.Max(2, actualMinutes)));
            if (at > context.FakeTime.GetUtcNow())
            {
                context.FakeTime.SetUtcNow(at);
            }

            string image = BlockedApps[random.Next(BlockedApps.Length)];
            var process = new ProcessDescriptor(1000 + i, 1, image, $@"C:\Program Files\{image}", null, null, at);
            await context.PublishAsync(new ProcessBlocked(process, BlockReason.NotAllowlisted, null, at), cancellationToken);
        }

        DateTimeOffset end = start.AddMinutes(actualMinutes);
        if (end > context.FakeTime.GetUtcNow())
        {
            context.FakeTime.SetUtcNow(end);
        }

        await context.PublishAsync(new SessionEnded(id, endedEarly ? SessionEndReason.BreakGlass : SessionEndReason.Expired, end), cancellationToken);
    }

    private static CalendarEntry Session(string title, int day, int minute, int minutes, int color, int weekly = 0) =>
        Entry(EntryKind.Session, title, day, minute, minutes, color, weekly);

    private static CalendarEntry Task(string title, int day, int minute, int minutes, int color, int weekly = 0) =>
        Entry(EntryKind.Task, title, day, minute, minutes, color, weekly);

    private static CalendarEntry Entry(EntryKind kind, string title, int day, int minute, int minutes, int color, int weekly) => new()
    {
        Kind = kind,
        Title = title,
        StartDay = day,
        StartMinute = minute,
        Duration = minutes,
        Color = color,
        Repeat = weekly == 0 ? RepeatKind.Once : RepeatKind.Weekly,
        WeekdayMask = weekly,
    };
}
