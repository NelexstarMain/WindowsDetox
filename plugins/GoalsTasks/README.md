# Goals & Tasks

First-party Blocker plugin (`com.windowsdetox.goals`, version 1.0.1). A small daily to-do list with a goal of focus tasks for the day, so a detox session starts with a plan.

## What it stores

All data stays on this device, in the plugin's private storage:

* the tasks of the last 7 days (today included): text (at most 120 characters), done or open; at most 50 tasks per day
* an optional goal per day and the default daily goal (1 to 20 tasks, 3 until changed)

Every change is written immediately. Days follow the local calendar date of the plugin clock. A new day starts with an empty list: tasks you did not finish stay in the previous day's record (which feeds the history chart) and are dropped after 7 days. Nothing is recorded about sessions or blocked apps; the plugin subscribes to no events.

## What it shows

* Page "Goals" (title "Goals & Tasks"), built like the Insights page: four stat tiles (Today `done / goal` with "N to go" or "Goal reached", Done this week, Streak = days in a row with the goal reached, Open tasks); the "Today's tasks" card with an add form, one toggle per task, a remove button per task, "Clear completed" and small `Goal -1` / `Goal +1` buttons; the "Completed per day" bar chart for the last 7 days.
* Dashboard widget (two rows): today's progress and the next 3 open tasks, which can be ticked off right there; tapping anywhere else opens this page. With no tasks it says "Add tasks on the Goals page".
* Settings: the default daily goal and "Reset today" (asks for a second click) which removes today's tasks and today's own goal.

## Capabilities

`ui.page`, `ui.dashboard`, `ui.settings`. No events, no network.

## Files

`plugin.json` (manifest and icon), `GoalsPlugin.cs` (entry point, page, widget, settings), `GoalsStore.cs` (limits, persistence, day handling), `GoalsView.cs` (aggregation, formatting), `GoalsTests.cs` (run by Blocker before installing). The folder is zipped flat; the install pipeline test `BundledPluginTests` runs it through package, policy, compile and plugin tests.
