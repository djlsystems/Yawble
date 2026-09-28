# Triggers and what they cost

A trigger starts a member's run on its own: on a schedule (**Every N**, **Cron**, **Once**), when an event is published (**Event**), or when files change in a folder (**Folder change**, see [ops/folder-change-triggers.md](ops/folder-change-triggers.md)). A person adds them in a member's **Triggers** dialog, opened from its card.

Every run of an agent member is a paid model run. A trigger that fires often can quietly run up a large bill, so the dialog shows what the member's runs actually cost, and each trigger has two settings that bound it: whether its runs wake the Manager, and a daily token cap.

## Poll with plugins, spend models only when something happened

A plugin watches for free and publishes an event when it finds something; an agent reacts to that event. A model member on a short schedule is almost always better as a plugin plus an event trigger.

- **The plugin polls.** A schedule trigger wakes a [plugin member](plugins.md) every few minutes. It checks what is new, publishes one event per new item, and finishes. A plugin runs no model, so a poll costs no tokens.
- **The agent reacts.** An **Event** trigger on the agent member, on that event type, wakes it only when the plugin found something, with the item in front of it.

An agent member on a 1-minute schedule is 1,440 model runs a day, and under **Always** (below) as many Manager runs again, nearly all of them to learn that nothing happened. The plugin-plus-event shape spends a model run only on the items that exist.

## The measured cost line

When you add or edit a trigger, the dialog shows what this member's recent runs actually cost:

> Median 41,250 billable tokens per run, over its last 10 runs (7 measured, 3 not measured).

- It is the **median billable tokens** of the member's last 10 runs (fewer when it has run fewer times), over the runs that reported their usage.
- Runs that reported no usage are counted as **not measured**. They are never shown as zero cost and never guessed at. A plugin run reports no usage, so a plugin's runs are all not measured.
- A member with no measured run says **No runs measured yet**.
- There is no projected daily figure. How often a trigger will fire, and what each fire will cost, is not known in advance, so the dialog does not estimate it.

## A short schedule on an agent member asks first

Saving a schedule that fires more often than every 5 minutes on an agent member opens a confirmation. It says how often the schedule fires, what one run of this member has cost (or that none was measured, so its cost per run is not known), and suggests a plugin plus an event trigger instead. **Save anyway** saves it; **Go back** returns to the form.

This applies to **Every N** and **Cron** schedules. A cron schedule is judged by the shortest gap between its next fires, so `0 * 9 * * *` (every minute of the nine o'clock hour) asks too. Plugin members have no minimum and are never asked.

## Wake the Manager when a run ends

Each trigger chooses what a run it started does to the Manager when the run ends:

| Choice | The Manager is woken |
|---|---|
| **Only if it hands back or fails** | When the run hands back or fails. A run that finishes without handing back wakes nobody. This is the default for a new trigger of every kind. |
| **Always** | On every completion, hand-back and failure. This is how every trigger behaved before the setting existed, and a trigger created before it keeps **Always** until a person changes it. |
| **Never** | Not even for a failure. The run is still recorded, and a failure still shows on the card and in the feed, but nobody is woken. |

- **An agent needs no new tool.** An agent member that found something hands back: that is how it wakes the Manager with its finding. One that found nothing simply finishes.
- **A plugin's `quiet` keeps its meaning.** Under **Always** it is still how a plugin says "nothing happened"; see [Quiet runs](plugins.md#quiet-runs).
- **It concerns only runs a trigger started.** Work the Manager sent still wakes it when it completes (the Manager is waiting for it), and a person's own tell behaves as before.

## Daily token cap

**Daily token cap** is optional. It is the most billable tokens this trigger's runs may spend in a day, counting the Manager runs those runs woke. Blank is no cap.

- **The day** is the trigger's timezone for a cron trigger, and UTC for every other kind.
- **When the cap is reached, a schedule says so once and sleeps until the next day.** Its next fire is skipped: the trigger's chip says **skipped (daily cap)**, and the log records ONE `schedule.skipped` row, with the reason "daily token cap reached; resumes at <time>", and the instance's audit log ONE matching row. Its next fire moves to its first occurrence on or after the start of the next day in its timezone (the same day boundary the cap counts by), and it does not wake before then: an every-minute schedule capped at 10:00 writes one pair of rows, not one a minute until midnight. Its row says **Capped until <time>**, in its timezone. An every-N schedule keeps its own rhythm, so it resumes at the first of its own times after midnight; a one-off (**Once**) has no later time and does not fire again.
- **It survives a restart.** Nothing is held in memory: the time it resumes is its stored next fire, and what it spent today is read again from the log. A Host restarted while it sleeps waits for that time. A Host that was down when the sleep ended treats it like any fire missed while down: one **missed** record, then the next occurrence still ahead.
- **A person's change wakes it.** Raising the cap above what it spent today, or clearing the cap, makes it fire at its next occurrence from now instead of the next day. Editing the schedule (kind, interval, cron, timezone or start) re-arms it from now as any edit does; if it is still over its cap then, that fire is skipped and counted, without another row, and it sleeps until the next day again. Lowering the cap leaves it asleep. Every change is written with its audit-log row in the same transaction.
- **Event and folder triggers are capped too**, since a storm of events is the same bill as a fast schedule. They have no schedule to sleep on, so they still skip every event while capped, but only the FIRST skip of the day writes the `schedule.skipped` row and the audit row; the rest are counted, and the row says **skipped today: N** (every skip today, the logged one included).
- **Only measured tokens count.** A run that reported no usage adds nothing to the figure and is counted as not measured. A cap therefore bounds what was measured; a member whose runs report no usage is never stopped by it.

Each trigger's row shows what it spent today against its cap:

> spent today 3,000 tokens + 1 run not measured / cap 50,000 tokens

Runs that reported no usage are named as such, never folded in as zero. When no run today was measured, the row says **nothing measured** rather than **0 tokens**. Once the cap is reached the line adds **cap reached, fires again tomorrow**.

The trigger routes return the same state read-only: `capReachedToday`, `cappedUntil` (the time a schedule asleep on its cap resumes, else null) and `skippedToday` (how many fires the cap skipped today).

## What else holds

**Only when the member is idle** (on by default for a new trigger) skips a fire while the member is busy, so fires do not pile up behind a long run. That is unchanged.
