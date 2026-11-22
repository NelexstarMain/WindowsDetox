# Calendar

First-party Blocker plugin (`com.windowsdetox.calendar`, version 1.1.0). Your own calendar on this PC, in the style of Google Calendar: drag on a day to plan a focus session or a task, repeat it, and let planned sessions start by themselves. Nothing leaves the device: no account, no network.

## What it stores

All data stays in the plugin's private storage, as three small documents:

* `data`: the entries (at most 300). A **focus session** has a title, date, start time, length (15 minutes to 12 hours) and an optional repeat; a **task or event** has a title, date, optional time and length, an optional repeat, a colour tag and a done tick per occurrence. Repeats: once, every N days, or on chosen weekdays of every N-th week; a series ends never, on a date or after a count. Single occurrences can be skipped or edited on their own.
* `history`: what happened to each planned session occurrence (started, missed, could not start), 120 days and at most 600 records. It is what keeps a session from starting twice and what the streak is counted from.
* `settings`: week start, clock, default session length, reminder lead time, auto-start.

Times are local wall-clock times: "09:00 every Monday" stays 09:00 across daylight saving changes. A time inside a spring-forward gap runs at the first minute after the gap; a time that happens twice in autumn means the first one. Impossible dates are clamped to the end of the month (31 February becomes 28 February). Every entry is validated the same way whether it is loaded, added or replaced (lengths, intervals, colour tags, dates).

Safety of the stored data:

* **Newer or unknown data is read-only.** A document whose `v` is newer than this build writes, or one with a kind, repeat, end or outcome this build does not know, is shown as far as it is understood and never written back (the page says so, dragging and saving do nothing, nothing starts by itself). A downgrade therefore cannot delete newer data.
* **Unreadable data starts empty** (the page says so) and a copy is kept under `<key>-unreadable` as `<unix seconds>` newline `<text>`, cut to 64 KiB. The copy is deleted 30 days after it was made.
* **A session never starts twice.** The history record is written before the start is requested; if that write fails the session is not started and the next tick tries again.
* **Ids stay unique.** After an unreadable entries document the id counter is placed above every id the history still mentions.

## What it shows

* Page "Calendar": tiles (Next session, Today, This week, Streak) and one calendar card. The card has a single header row: **Today**, previous and next arrows, the period title and a **Day / Week / Month** switch (the current view is filled). Nothing else is always visible: there is no Plan form and no button per entry.
  * **Week** (the default) and **Day** are one host-drawn time grid (`UiTimeGrid`): 7 or 1 columns, 15-minute snapping, a line for the current time, scrolled to an hour before the first block (08:00 when empty). Blocks show the time range (`09:00–10:30`), a session is the accent colour (green once started, amber when missed, red when it could not start), a task takes its colour tag and turns green when done. A block that crosses midnight is drawn as two blocks, one per day. Click a day header in Week to open that day.
  * **Create**: drag across empty space (a click makes a one-hour block). A ghost block appears with the quick editor next to it: title (default "Focus session"), kind (focus session or task), start and end (an end before the start means the next day; empty means the default session length), repeat (does not repeat, every day, every week) and **Save**. "More options" adds the date, repeat interval and weekdays, how the repeat ends and the colour tag. Clicking outside discards the block.
  * **Select**: click a block for its details next to it (title, when, state, repeat) and **Edit**, **Duplicate** (a copy to adjust and save), **Delete** (a repeating entry offers "Delete this one" and "Delete series"; a task also has a Done tick). Delete asks for a second click. Ctrl or Shift click adds or removes blocks: with two or more selected the popover says "N selected" and offers **Delete selected**, **Duplicate to next week** and **Clear selection**. Clicking outside clears the selection.
  * **Move and resize**: drag a block or its bottom edge; the change is saved at once. On a repeating entry only that occurrence changes (the page says so). Sessions are checked like in the editor (not in the past, no overlap, 15 minutes to 12 hours); a session that started or was missed, and a block across midnight, cannot be dragged (use Edit).
  * **Month** is a full-size host-drawn grid (`UiMonthGrid`): today is a filled circle, the selected day is outlined, other months are dimmed and each day lists the titles of its entries (three, the host adds "+N"). Click a day to open it in Day view.
  * Tasks without a time (from earlier versions) have no block; while there are any they are listed under the grid with a tick box and an Open button.
* Dashboard widgets: "Next session" (one flat tile: countdown, title and time; fits one cell) and "Calendar" (next session, a compact month with its own previous/next buttons, the selected day's agenda with tick boxes, what is coming up). The widget API does not tell a widget its size and the host opens every widget one column wide with up to three rows, so the rich widget lists its content by importance and the host clips the rest. Tapping a widget opens this page.
* Settings: auto-start on or off, default session length (used when the editor's end time is left empty), reminder lead time, week starts on Sunday, 24-hour clock, clear past entries.

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

`plugin.json` (manifest and icon), `CalendarPlugin.cs` (entry point), `CalendarModel.cs` (records, limits), `CalendarTime.cs` (dates, time zones, parsing, formatting), `CalendarRecurrence.cs` (expansion, overlaps), `CalendarStore.cs` (persistence), `CalendarForm.cs` (editor, move and copy validation), `CalendarScheduler.cs` (auto-start rules), `CalendarAutoStarter.cs` (tick listener), `CalendarView.cs`, `CalendarState.cs`, `CalendarController.cs`, `CalendarPage.cs` (page, month), `CalendarGrid.cs` (time grid), `CalendarPopover.cs` (editor, details, multi-selection), `CalendarWidgets.cs`, `CalendarSettingsSection.cs`, and the tests `Calendar*Tests.cs` (run by Blocker before installing).
