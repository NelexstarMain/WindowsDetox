# Calendar

First-party Blocker plugin (`com.windowsdetox.calendar`, version 1.0.0). Your own calendar on this PC: plan focus sessions and tasks, repeat them, and let planned sessions start by themselves. Nothing leaves the device: no account, no network.

## What it stores

All data stays in the plugin's private storage, as three small documents:

* `data`: the entries (at most 300). A **focus session** has a title, date, start time, length (15 minutes to 12 hours) and an optional repeat; a **task or event** has a title, date, optional time and length, an optional repeat, a colour tag and a done tick per occurrence. Repeats: once, every N days, or on chosen weekdays of every N-th week; a series ends never, on a date or after a count. Single occurrences can be skipped or edited on their own.
* `history`: what happened to each planned session occurrence (started, missed, could not start), 120 days and at most 600 records. It is what keeps a session from starting twice and what the streak is counted from.
* `settings`: week start, clock, default session length, reminder lead time, auto-start.

Times are local wall-clock times: "09:00 every Monday" stays 09:00 across daylight saving changes. A time inside a spring-forward gap runs at the first minute after the gap; a time that happens twice in autumn means the first one. Impossible dates are clamped to the end of the month (31 February becomes 28 February). Stored data is sanitized on load; an unreadable document starts empty, the page says so, and the unreadable text is kept under `<key>-unreadable`.

## What it shows

* Page "Calendar", built like the Insights page: tiles (Next session, Today, This week, Streak), the calendar card with a **month grid** (dots per day, click a day for its agenda), a **week** view and an **agenda** of the next 14 days, and the Plan card (add or edit an entry; editing a repeating entry asks whether the change applies to this occurrence or to all of them). Skip and delete ask for a second click.
* Dashboard widgets: a compact one (next session countdown and title, today's progress) and a rich one (next session, today's agenda with tick boxes, a mini month with dots and a highlighted today, the selected day's agenda, what is coming up, and a quick-add field such as `15:30 Call Anna`). The widget API does not tell a widget its size, so the rich widget lists its content by importance and the host clips the rest. Tapping a widget opens this page.
* Settings: auto-start on or off, default session length, reminder lead time, week starts on Sunday, 24-hour clock, clear past entries.

## Planned sessions start by themselves

A tick every 15 seconds decides (in memory) what is due; the service call runs in the background:

* a reminder toast comes the configured number of minutes before the start;
* at the planned time the plugin starts the session with `ISessionLauncher` (the same service call as the Setup page: your saved allowlist, default emergency passes), unless a session is already running;
* the occurrence's history record is written **before** the start is requested, so it can never start twice, also not after a restart;
* after sleep or boot a session that began less than 10 minutes ago still starts, for the remaining time (it ends when it was planned to end); a later one is recorded as missed and never started late. Less than 15 minutes left counts as missed;
* a refusal (for example an empty allowlist) is retried twice, 30 seconds apart, then recorded as failed with a toast.

A started session cannot be shortened or cancelled, like every session, and it has **no break-glass code**: the code is never handed to a plugin, only emergency passes can interrupt it. Overlapping sessions are rejected when you plan them.

## Capabilities

`ui.page`, `ui.dashboard`, `ui.settings`, `tick`, `session.start`, `notify`. No network, no events, no secrets.

## Files

`plugin.json` (manifest and icon), `CalendarPlugin.cs` (entry point), `CalendarModel.cs` (records, limits), `CalendarTime.cs` (dates, time zones, parsing, formatting), `CalendarRecurrence.cs` (expansion, overlaps), `CalendarStore.cs` (persistence), `CalendarForm.cs` (Plan form validation), `CalendarScheduler.cs` (auto-start rules), `CalendarAutoStarter.cs` (tick listener), `CalendarView.cs`, `CalendarState.cs`, `CalendarController.cs`, `CalendarPage.cs`, `CalendarWidgets.cs`, `CalendarSettingsSection.cs`, and the tests `Calendar*Tests.cs` (run by Blocker before installing).
