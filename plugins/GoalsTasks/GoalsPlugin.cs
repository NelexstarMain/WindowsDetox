// Goals & Tasks: a small daily to-do list with a goal of focus tasks for the day.
//
// Everything stays on this device:
//   * storage     the tasks of the last 7 days and the default goal (see GoalsStore)
//   * UI          a page (ui.page), a dashboard widget (ui.dashboard) and a settings section (ui.settings)
//   * 50 ms rule  every render reads the in-memory copy of at most 7 small day records; storage is never read while rendering
// The plugin subscribes to no events, so it declares no event capabilities.

using System.Globalization;
using Blocker.Sdk;
using Blocker.UI.Sdk;

namespace GoalsTasks;

/// <summary>Entry point named in plugin.json.</summary>
public sealed class GoalsPlugin : IBlockerPlugin
{
    private GoalsStore? _store;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var store = new GoalsStore(context.Storage, context.Time, context.Logger);
        await store.LoadAsync(cancellationToken);
        _store = store;

        context.Extensions.Add(new GoalsPage(store));
        context.Extensions.Add(new GoalsWidget(store));
        context.Extensions.Add(new GoalsSettings(store));
    }

    /// <inheritdoc/>
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_store is { } store)
        {
            await store.FlushAsync(cancellationToken);
        }
    }
}

/// <summary>Remembers the surface of a contribution, re-renders it whenever the data changes and handles the task toggles.</summary>
public abstract class GoalsContribution : IUiContribution
{
    /// <summary>Prefix of the id of a task's "done" toggle; the task id follows.</summary>
    public const string DoneIdPrefix = "goals-done-";

    private IUiSurface? _surface;

    /// <summary>Subscribes to store changes.</summary>
    protected GoalsContribution(GoalsStore store)
    {
        Store = store;
        store.Changed += () => Volatile.Read(ref _surface)?.RequestRender();
    }

    /// <summary>The shared, cached state.</summary>
    protected GoalsStore Store { get; }

    /// <inheritdoc/>
    public virtual async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        if (action.Kind == UiActionKind.Toggle && TryTaskId(action.ElementId, DoneIdPrefix, out int id) && bool.TryParse(action.Value, out bool done))
        {
            await Store.SetDoneAsync(id, done, cancellationToken);
        }
    }

    /// <summary>Call at the start of every render and action so a later render can be requested.</summary>
    protected void Remember(UiRenderContext context) => Volatile.Write(ref _surface, context.Surface);

    /// <summary>Reads the task id after <paramref name="prefix"/> in an element id.</summary>
    protected static bool TryTaskId(string elementId, string prefix, out int id)
    {
        id = 0;
        return elementId.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(elementId.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>The tick box of a task. It has no caption in either state besides the task text, so ticking never changes the height of its row.</summary>
    protected static UiToggle DoneToggle(TaskItem task) =>
        Ui.Checkbox(DoneIdPrefix + task.Id.ToString(CultureInfo.InvariantCulture), task.Text, task.Done);
}

/// <summary>The Goals page. Requires "ui.page".</summary>
public sealed class GoalsPage(GoalsStore store) : GoalsContribution(store), IUiPagePlugin
{
    /// <summary>Id of the form that adds a task.</summary>
    public const string AddFormId = "goals-add";

    /// <summary>Id of the text field of the add form.</summary>
    public const string AddFieldId = "goals-add-text";

    /// <summary>Prefix of the id of a task's delete button; the task id follows.</summary>
    public const string DeleteIdPrefix = "goals-del-";

    /// <summary>Id of the "Clear completed" button.</summary>
    public const string ClearButtonId = "goals-clear";

    /// <summary>Id of the button that lowers today's goal.</summary>
    public const string GoalDownId = "goals-goal-down";

    /// <summary>Id of the button that raises today's goal.</summary>
    public const string GoalUpId = "goals-goal-up";

    /// <inheritdoc/>
    public UiNavigationItem Navigation { get; } = new("Goals", UiIcon.Target);

    /// <inheritdoc/>
    public ValueTask<UiPage> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        GoalsView view = Store.GetView();
        return ValueTask.FromResult(new UiPage(
            "Goals & Tasks",
            "Pick a few focus tasks for today and tick them off.",
            [Tiles(view), TasksCard(view), HistoryCard(view)]));
    }

    /// <inheritdoc/>
    public override async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        await base.OnActionAsync(action, context, cancellationToken);
        if (action.Kind == UiActionKind.Submit && action.ElementId == AddFormId && action.FormValues.TryGetValue(AddFieldId, out string? text))
        {
            await Store.AddTaskAsync(text, cancellationToken);
        }
        else if (action.Kind == UiActionKind.Click)
        {
            if (TryTaskId(action.ElementId, DeleteIdPrefix, out int id))
            {
                await Store.DeleteAsync(id, cancellationToken);
                return;
            }

            switch (action.ElementId)
            {
                case ClearButtonId:
                    await Store.ClearCompletedAsync(cancellationToken);
                    break;
                case GoalDownId:
                    await Store.AdjustTodayGoalAsync(-1, cancellationToken);
                    break;
                case GoalUpId:
                    await Store.AdjustTodayGoalAsync(1, cancellationToken);
                    break;
            }
        }
    }

    private static UiNode Tiles(GoalsView v) => Ui.Grid(
        4,
        Ui.Stat(
            "Today",
            string.Create(CultureInfo.InvariantCulture, $"{v.Done} / {v.Goal}"),
            GoalsFormat.Remaining(v.Done, v.Goal),
            v.GoalReached ? UiTone.Success : UiTone.Accent,
            UiIcon.Target),
        Ui.Stat("Done this week", v.DoneWeek.ToString(CultureInfo.InvariantCulture), "Last 7 days", UiTone.Success, UiIcon.Checkmark),
        Ui.Stat(
            "Streak",
            v.Streak.ToString(CultureInfo.InvariantCulture) + (v.StreakCapped ? "+" : string.Empty),
            "Days in a row",
            v.Streak > 0 ? UiTone.Success : UiTone.Neutral,
            UiIcon.Flame),
        Ui.Stat("Open tasks", v.Open.ToString(CultureInfo.InvariantCulture), "Today", UiTone.Neutral, UiIcon.Clock));

    private static UiCard TasksCard(GoalsView v)
    {
        List<UiNode> body = [];
        if (v.Tasks.Count < GoalsLimits.MaxTasksPerDay)
        {
            body.Add(Ui.Form(AddFormId, "Add", new UiTextField(AddFieldId, "New task", string.Empty, "Add a task for today", GoalsLimits.MaxTaskLength)));
        }
        else
        {
            body.Add(Ui.Text($"Today's list is full ({GoalsLimits.MaxTasksPerDay} tasks). Remove or clear some to add more.", UiTextStyle.Caption));
        }

        if (v.Tasks.Count == 0)
        {
            body.Add(Ui.Column(
                Ui.Text("Nothing planned for today"),
                Ui.Text("Add the one to three things that matter, then tick them off here.", UiTextStyle.Caption)));
        }

        foreach (TaskItem task in v.Tasks)
        {
            body.Add(Ui.Row(DoneToggle(task), Ui.Button(DeleteIdPrefix + task.Id.ToString(CultureInfo.InvariantCulture), "Remove", UiButtonVariant.Subtle, UiIcon.Dismiss)));
        }

        if (v.Done > 0)
        {
            body.Add(Ui.Button(ClearButtonId, "Clear completed", UiButtonVariant.Secondary, UiIcon.Checkmark));
        }

        // The inner row is the only child of the outer one, so it keeps its natural width and the buttons sit next to the label.
        body.Add(Ui.Row(Ui.Row(
            Ui.Text($"Goal for today: {GoalsFormat.Count(v.Goal, "task")}", UiTextStyle.Caption),
            new UiButton(GoalDownId, "Goal -1", UiButtonVariant.Secondary, IsEnabled: v.Goal > GoalsLimits.MinGoal, ShortLabel: "\u2212"),
            new UiButton(GoalUpId, "Goal +1", UiButtonVariant.Secondary, IsEnabled: v.Goal < GoalsLimits.MaxGoal, ShortLabel: "+"))));
        return new UiCard(body, "Today's tasks", v.Tasks.Count == 0 ? null : $"{v.Open} open, {v.Done} done");
    }

    private static UiCard HistoryCard(GoalsView v)
    {
        UiNode body = v.History.Any(count => count > 0)
            ? new UiBarChart(
                [.. v.History.Select((count, i) => Ui.Point(i == v.History.Count - 1 ? "Today" : GoalsFormat.WeekdayLabel(v.Today - v.History.Count + 1 + i), count))],
                null,
                "done")
            : Ui.Text("Completed tasks show up here.", UiTextStyle.Caption);
        return new UiCard([body], "Completed per day", "Last 7 days");
    }
}

/// <summary>Dashboard widget: today's progress and the next open tasks. The host draws the surface, icon, name and tap-to-open. Requires "ui.dashboard".</summary>
public sealed class GoalsWidget(GoalsStore store) : GoalsContribution(store), IDashboardWidgetPlugin
{
    /// <summary>Open tasks shown in the widget.</summary>
    public const int VisibleTasks = 3;

    /// <inheritdoc/>
    public UiWidgetSize Size => UiWidgetSize.Medium;

    /// <inheritdoc/>
    public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        GoalsView v = Store.GetView();
        UiTone tone = v.GoalReached ? UiTone.Success : UiTone.Accent;
        List<UiNode> body =
        [
            Ui.Stat("Today", GoalsFormat.Progress(v.Done, v.Goal), GoalsFormat.Remaining(v.Done, v.Goal), tone),
            new UiProgress(Math.Min(v.Done, v.Goal), v.Goal, null, tone),
        ];
        TaskItem[] open = [.. v.Tasks.Where(t => !t.Done)];
        body.AddRange(open.Take(VisibleTasks).Select(task => (UiNode)DoneToggle(task)));
        if (open.Length > VisibleTasks)
        {
            body.Add(Ui.Text($"+{open.Length - VisibleTasks} more on the Goals page", UiTextStyle.Caption));
        }
        else if (v.Tasks.Count == 0)
        {
            body.Add(Ui.Text("Add tasks on the Goals page", UiTextStyle.Caption));
        }
        else if (open.Length == 0)
        {
            body.Add(Ui.Text("All tasks done. Nice work!", UiTextStyle.Caption));
        }

        return ValueTask.FromResult(Ui.Card([.. body]));
    }
}

/// <summary>Settings: default daily goal and resetting today. Requires "ui.settings".</summary>
public sealed class GoalsSettings(GoalsStore store) : GoalsContribution(store), ISettingsSectionPlugin
{
    /// <summary>Id of the button that lowers the default goal.</summary>
    public const string DefaultDownId = "goals-default-down";

    /// <summary>Id of the button that raises the default goal.</summary>
    public const string DefaultUpId = "goals-default-up";

    /// <summary>Id of the reset button.</summary>
    public const string ResetButtonId = "goals-reset";

    /// <summary>How long the reset button waits for its confirming second click.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(10);

    private long _armedAt;
    private int _armed;

    /// <inheritdoc/>
    public string Title => "Goals & Tasks";

    /// <inheritdoc/>
    public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        int goal = Store.DefaultGoal;
        UiButton reset = IsArmed()
            ? Ui.Button(ResetButtonId, "Reset today? Click again", UiButtonVariant.Warning, UiIcon.Warning)
            : Ui.Button(ResetButtonId, "Reset today", UiButtonVariant.Secondary, UiIcon.Dismiss);
        return ValueTask.FromResult<UiNode>(Ui.Column(
            Ui.Row(
                Ui.Text($"Default daily goal: {GoalsFormat.Count(goal, "task")}"),
                new UiButton(DefaultDownId, "Default -1", UiButtonVariant.Secondary, IsEnabled: goal > GoalsLimits.MinGoal, ShortLabel: "\u2212"),
                new UiButton(DefaultUpId, "Default +1", UiButtonVariant.Secondary, IsEnabled: goal < GoalsLimits.MaxGoal, ShortLabel: "+")),
            Ui.Text("Used for every day without its own goal; change today's goal on the Goals page.", UiTextStyle.Caption),
            Ui.Text("Reset today removes all of today's tasks and today's own goal.", UiTextStyle.Caption),
            reset));
    }

    /// <inheritdoc/>
    public override async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        await base.OnActionAsync(action, context, cancellationToken);
        if (action.Kind != UiActionKind.Click)
        {
            return;
        }

        switch (action.ElementId)
        {
            case DefaultDownId:
                await Store.AdjustDefaultGoalAsync(-1, cancellationToken);
                break;
            case DefaultUpId:
                await Store.AdjustDefaultGoalAsync(1, cancellationToken);
                break;
            case ResetButtonId when IsArmed():
                Volatile.Write(ref _armed, 0);
                if (!await Store.ResetTodayAsync(cancellationToken))
                {
                    context.Surface.RequestRender();   // nothing to reset, but the button must leave its armed state
                }

                break;
            case ResetButtonId:
                Volatile.Write(ref _armedAt, Store.Time.GetTimestamp());
                Volatile.Write(ref _armed, 1);
                context.Surface.RequestRender();
                break;
        }
    }

    private bool IsArmed() =>
        Volatile.Read(ref _armed) == 1 && Store.Time.GetElapsedTime(Volatile.Read(ref _armedAt)) < ConfirmWindow;
}
