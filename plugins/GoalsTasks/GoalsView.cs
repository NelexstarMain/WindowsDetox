// Aggregation (stored day records -> numbers for the UI) and text formatting.

using System.Globalization;

namespace GoalsTasks;

/// <summary>Everything the UI shows, derived from the stored day records.</summary>
public sealed record GoalsView
{
    /// <summary>The local day number the view was built for.</summary>
    public int Today { get; init; }

    /// <summary>Goal of today.</summary>
    public int Goal { get; init; }

    /// <summary>Tasks of today in the order they were added (copies; safe to read without a lock).</summary>
    public IReadOnlyList<TaskItem> Tasks { get; init; } = [];

    /// <summary>Done tasks of today.</summary>
    public int Done { get; init; }

    /// <summary>Done tasks per day for the last 7 days, oldest first (the last value is today).</summary>
    public IReadOnlyList<int> History { get; init; } = [];

    /// <summary>Open tasks of today.</summary>
    public int Open => Tasks.Count - Done;

    /// <summary>Whether today's goal is reached.</summary>
    public bool GoalReached => Done >= Goal;
}

/// <summary>Pure function that turns day records into a <see cref="GoalsView"/>.</summary>
public static class GoalsAggregator
{
    /// <summary>Combines day records (any order) for the local day <paramref name="today"/>.</summary>
    public static GoalsView Build(IReadOnlyList<DayRecord> days, int today, int defaultGoal)
    {
        int[] history = new int[GoalsLimits.RetentionDays];
        DayRecord? current = null;
        foreach (DayRecord day in days)
        {
            int age = today - day.Day;
            if (age is >= 0 and < GoalsLimits.RetentionDays)
            {
                history[GoalsLimits.RetentionDays - 1 - age] += day.Tasks.Count(t => t.Done);
            }

            if (age == 0)
            {
                current = day;
            }
        }

        TaskItem[] tasks = current is null ? [] : [.. current.Tasks.Select(t => new TaskItem { Id = t.Id, Text = t.Text, Done = t.Done })];
        return new GoalsView
        {
            Today = today,
            Goal = current is { Goal: > 0 } ? current.Goal : defaultGoal,
            Tasks = tasks,
            Done = tasks.Count(t => t.Done),
            History = history,
        };
    }
}

/// <summary>Text formatting shared by the page, the widget and the settings section.</summary>
public static class GoalsFormat
{
    /// <summary>"3 / 5 tasks".</summary>
    public static string Progress(int done, int goal) => string.Create(CultureInfo.InvariantCulture, $"{done} / {goal} tasks");

    /// <summary>"1 task", "2 tasks".</summary>
    public static string Count(int count, string singular) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {singular}{(count == 1 ? string.Empty : "s")}");

    /// <summary>"Goal reached" or "2 to go".</summary>
    public static string Remaining(int done, int goal) =>
        done >= goal ? "Goal reached" : string.Create(CultureInfo.InvariantCulture, $"{goal - done} to go");

    /// <summary>"Mon".</summary>
    public static string WeekdayLabel(int dayNumber) => DateOnly.FromDayNumber(dayNumber).ToString("ddd", CultureInfo.InvariantCulture);
}
