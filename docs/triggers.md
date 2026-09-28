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
- Runs that reported no usage are counted as **not measured**. They are never shown as zero cost and never guessed at.
- **A plugin member's line says "Runs no model: no token cost"** instead of a median. A plugin runs no model, so its token cost is known, and it is zero: its run's terminal row carries `tokensSource: "none"`, the reason no token figures follow. The trigger's spent-today, the member's recent cost and the workflow spend all count such a run as a **measured run of 0 billable tokens**, not as not measured. Only a plugin's run is marked this way; an agent run that reports no usage stays not measured. Plugin runs recorded before the marker existed carry no marker and still count as not measured.
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
- **When the cap is reached**, the next fire is skipped: the trigger's chip says **skipped (daily cap)**, the log records `schedule.skipped` with the reason "daily token cap reached", and so does the instance's audit log. The next day it fires again.
- **Event and folder triggers are capped the same way.** A storm of events is the same bill as a fast schedule.
- **Only measured tokens count.** A run that reported no usage adds nothing to the figure and is counted as not measured. A cap therefore bounds what was measured; a member whose runs report no usage is never stopped by it.

Each trigger's row shows what it spent today against its cap:

> spent today 3,000 tokens + 1 run not measured / cap 50,000 tokens

Runs that reported no usage are named as such, never folded in as zero. When no run today was measured, the row says **nothing measured** rather than **0 tokens**. Once the cap is reached the line adds **cap reached, fires again tomorrow**.

## What else holds

**Only when the member is idle** (on by default for a new trigger) skips a fire while the member is busy, so fires do not pile up behind a long run. That is unchanged.
