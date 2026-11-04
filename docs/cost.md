# What a team's work is estimated to cost

A team's **Estimated Execution Cost** is read in money, by the outcomes it works on, against the
budgets set for them, and only then in the tokens behind it. It is shown in four places: the
**summary** above the Teams list, the **Estimated Execution Cost** tile on the team's page, the
dialog that tile opens, and the **Estimated Execution Cost · 8 weeks** column of the Teams list.

**It is an estimate, not money paid.** Each run is priced from the snapshot taken at its start:
its hours times the rate it started with. It is not what was paid or owed to any vendor, and none
of these screens call it spend.

Every figure comes from the usage ledger, over the last eight weeks (Monday-start weeks in UTC,
oldest first, the same weeks the Outcomes dashboard uses). The hours are measured; only the money
is an estimate.

## How a run is priced

- **Each run is priced from the snapshot taken at its start**, at the rate that snapshot records,
  rounded to the cent. The snapshot is described below.
- **An agent with no rate of its own** inherited the tenant rate at that start. With no tenant rate
  then, its run is unavailable: its time is in Agent Hours, and it adds no money.
- **A plugin with no rate of its own is not priced** and is not in Agent Hours.
- **A plugin with its own rate** adds its estimate to the headline only. Its time is still not in
  Agent Hours.
- **A rate or a link changed later does not move the run.** Changing the tenant rate or a member's
  rate prices only the runs that start afterwards, so **past weeks do not move when the rate
  changes**. A run stays on the outcome its workflow's link named when it started.
- **A run that crosses a week is split by overlap.** Each week gets the part of the run inside it,
  priced and rounded to the cent on its own. Tokens are reported when a run ends, so they stay in
  the week it ended.
- **A run from before snapshots** has no snapshot, and is priced from the rate's history instead:
  - **An agent run is priced at the tenant rate in effect at its start**, the value of the latest
    change to the agent hourly rate at or before it, labelled **reconstructed**. It is in the
    instance's currency now.
  - **A run from before every recorded rate change** uses the one rate captured when the rate
    history began (when this version first started), labelled **fallback**. That captured rate is
    the stored tenant rate then or, with none stored, a rate given in the Host's configuration. It
    is written once and does not move: a later start or a later rate change leaves it as it was.
  - **A zero or a missing rate is unavailable.** A run that started while the rate was 0, empty or
    reset is unavailable even when the captured rate is set; so is a run before every change when
    nothing was captured. Its time is in Agent Hours, and it adds no money, never `$0`.
  - **A plugin run from before snapshots is not priced**, never at the agent rate, and is not in
    Agent Hours.
  - **A run that already has a snapshot is not reconstructed**, including one whose snapshot
    records no rate: that run stays unavailable.
  - **A later rate change does not move a past run**: the change is in effect only for runs that
    start after it.
  - It keeps the outcome its workflow's newest link names.
- **A figure that used a reconstructed or fallback rate says so**, with a footnote under it:
  `* Includes reconstructed rates`, `* Includes a fallback rate`, or
  `* Includes reconstructed and fallback rates`. The title and the figure are unchanged.
- **The line under the figure** reads `<N> Agent Hours at <R>/h`. N is the agent time; R is the
  rate of the priced agent time alone, its estimates divided by its hours, reconstructed and
  fallback agent time included. **The headline need not equal N times R**: a priced plugin is in the
  headline and not in N, and an unpriced agent hour is in N and not in the money. A reconstructed or
  fallback agent hour is in both. With no priced agent time the line gives the hours alone.
- **One currency.** Money in another currency than the instance's is left out of the figure, never
  converted or added.
- **On the Outcomes dashboard** an outcome's agent time, tokens and money are the runs that serve
  it, each run once, as above. Its workflow counts and elapsed times still follow each workflow's
  newest link, so a workflow moved after a run started is counted under its new outcome while that
  run's time and money stay with the old one.

## Where the rate and budgets are set

- **The rate.** The instance's **agent hourly rate** is the tenant default an agent inherits when
  its run starts, in the instance's currency. A person sets both in **Admin → Outcomes**, with
  **change** beside the rate line (the **Pricing agent time** dialog). The Estimated Execution Cost
  dialog's **Change in Outcomes** link opens the same place. No figure is that rate times hours: a
  change prices the runs that start afterwards.
- **No price, no dollars.** When no run in a figure has an estimate, the figure is Agent Hours
  instead. Nothing shows `$0` for time that has no price. This is not the same as the tenant rate
  being unset: a member's own rate still prices its runs.
- **Budgets.** An outcome's **Budget** (in Admin → Outcomes, on the outcome, or when creating one)
  is what achieving it is worth: what it may spend. An outcome with no budget, and the team's work
  linked to no outcome at all (**Unattributed**), have none.

## What a run records when it starts

Each run records, when it starts, a **snapshot** of how it was configured: whether the member is
an agent or a plugin, its agent preset, the estimated hourly rate it is priced at and where that
rate came from, the member's billing account, and the outcome link the workflow had at that moment.
The run's usage ledger row names that snapshot. A snapshot is never changed afterwards.

### The member's estimated hourly rate

A member, agent or plugin, can carry its own estimated hourly rate, set by a person on the member
(`PATCH /api/teams/{team}/containers/{name}`, `hourlyRate`). The snapshot at start records the
member's effective rate:

- A member with its own rate above 0 is priced at that rate, in the instance's currency.
- An agent with no rate of its own inherits the tenant default, the instance's agent hourly rate.
  With no tenant rate, or a tenant rate of 0, the snapshot records no rate.
- A plugin with no rate of its own is not priced, never at the agent rate.
- A rate of 0 is not priced. A member's own 0 does not fall back to the tenant default. A snapshot
  never records a rate of 0.
- Only the stored setting counts. A rate given only in the Host's configuration is not recorded on
  a snapshot. It is read once, when the rate history begins, as the fallback for runs from before
  snapshots, and nowhere else.

**The configuration at start covers the whole run.** Changing the member's rate or billing
account, or the tenant rate, during a run leaves that run's snapshot unchanged; the next run's
snapshot carries the new effective rate. An agent with its own rate does not pick up a change to the
tenant rate. Linking the workflow to another outcome during a run does not move that run's link
either.

Runs that started before snapshots existed have none, and none is made for them afterwards. They
are priced from the rate's history when a figure is read, as **How a run is priced** says; nothing
is written back.

The Teams list, its summary, the team page and the Outcomes dashboard price every run from this
snapshot, and a run with none from the rate's history, as **How a run is priced** says.

A snapshot also records who the run's workflow began with: the origin the log derived for the
workflow's first row (`person`, `agent`, `trigger` or `system`). A person's origin carries their id
and email, a member's carries its id and no email, and a trigger's carries its source. When the
run ends, its usage ledger row records the same origin's kind and actor id. Both are copies, so
they stay after the log's 90-day purge takes the rows they came from. Nothing prices, filters or
shows them yet. Runs and ledger rows from before origins were recorded have none, and none is
written back.

A run that ends interrupted also records why, as `usage_ledger.reason_code`: one code from the
closed set `InterruptionReasons` (see **Interruption reasons** in
[architecture.md](architecture.md)). A failed run's code comes from its failure class:
`needs-person` is `auth`, and every other class (`quota`, `rate`, `transport`, `agent-fault`,
`launch-missing`, `out-of-memory`, `crashed`, `timeout`, `interrupted`, `worker-lost`, `unknown`,
or no class) is `failure`. A blocked run's code is the code on the newest `blocked` or
`needsDecision` row the run published for the whole run (not for one batch item). A completed or
quiet run's code is NULL. The two databases write the same values. The code is a copy, so it stays
after the log's purge. Nothing prices, filters or shows it yet. Ledger rows from before it was
recorded have none, and none is written back.

`GET /api/ledger/export` gives each run its `rateSource` (`member`, `tenant` or `none` for a run
with a snapshot; `reconstructed`, `fallback-at-upgrade` or `none` for one without) and its
`estimate` (`amount` and `currency`, both null when it is not priced), from the same pricing.

## Billing accounts

A **billing account** is a named vendor account the instance is billed through: a
`subscription`, a `metered` account or a `service`, from a vendor, billed `monthly` or
`annual`, in **its own currency**. That currency is the account's, stored as three upper-case
letters; it need not be the instance's currency and is never converted. An account may carry a
recurring amount (zero or more; a zero is a zero) and the dates it is effective from and to.

- **It is not a connection.** It holds no credential, no key and no login, and nothing in its tenant
  log rows is secret.
- **Actual Spend is not computed from it yet.** A recurring amount is stored as it was given and is
  not added to any estimate. It is projected over periods only when read (see
  [Projected recurring charges](#projected-recurring-charges)), and a projection is never Actual
  Spend. Hours are never Actual Spend: every figure on these screens is still an **Estimated
  Execution Cost**.
- **A person's only.** Listing, reading, creating and changing accounts are people's acts; no agent
  or key may read or write one, whatever it holds.
- **Never deleted.** An account is deactivated, and may be reactivated. A deactivated account is
  still listed. Deactivating one does not clear the members who name it and does not rewrite any
  run's snapshot.
- **Every change has its tenant row.** A create, an edit, a deactivate and a reactivate each append
  `billing.account-created`, `-changed`, `-deactivated` or `-reactivated`, naming the account and
  each field that moved as old and new, in the same transaction: when that row cannot be written,
  the change does not happen. A save that changes nothing writes nothing.

**A member's billing account** (`billingAccountId` on the member PATCH) is blank, or the id of a
billing account; a deactivated one counts. An id that names no account is refused and nothing is
written. A save that does not send the field keeps what is stored, even an id saved before any
account existed. A new run's snapshot copies the member's stored account, as it always has, and a
run is never refused for it.

There is no screen for billing accounts yet; they are reached through the routes below.

## Expenses

An **expense** is a charge recorded on a billing account: an `invoice`, a `subscription-charge` or
an `adjustment`, for a service period, in **that account's currency**. The currency must be the
account's; an expense in another currency is refused, and nothing is converted. The amount may be
zero; only an adjustment may be negative. A deactivated account may still take an expense (a late
invoice), but an account id that names no account is refused.

- **Append-only.** An expense row is never changed and never deleted; the table refuses both. A
  mistake is corrected by a new row that supersedes the old one:
  - **Void**: a new `void` row carrying the old row's account, amount, currency, period and kind,
    with an optional note. The old row stays, no longer current.
  - **Replace**: a new `confirmed` row on the **same account**, with the fields given over the old
    row's. To move a charge to another account, void it and record a new one.
- **A row is corrected once.** A row is current while nothing supersedes it. A void row cannot be
  voided or replaced, and a row already voided or replaced cannot be corrected again: correct the
  current row instead. Two corrections of one row at the same moment land one and refuse the other.
- **Every write has its tenant row.** Recording, voiding and replacing each append
  `billing.expense-recorded`, `-voided` or `-replaced`, naming the expense, its account and, for a
  void or a replace, the row it supersedes, in the same transaction: when that row cannot be
  written, the expense is not recorded.
- **A person's only**, as billing accounts are: no agent or key may read or write one.

### Projected recurring charges

An active account with a recurring amount is expected to bring one charge per billing period. Those
**projected** charges are computed when they are read and never stored. The periods step from the
account's effective-from date by one month (`monthly`) or one year (`annual`), each counted from
effective-from itself, so an account from 31 January projects 28 February, then 31 March. They stop
at effective-to: a period that starts there or later is not one, and the last period ends there. A
zero amount projects a zero charge; an inactive account, or one with no recurring amount, projects
nothing.

A projected charge is **dropped when a confirmed expense covers it**: any current, confirmed
expense on that account whose service period overlaps the projected period. A voided expense no
longer covers its period, and a replaced one covers by its replacement's period. A projection is
labelled projected, kept apart from recorded expenses, and is **never counted as Actual Spend** and
never added to an estimate. Several members naming one account never multiply its charge.

No screen shows expenses or projections yet; expenses are reached through the routes below. Hours
are never Actual Spend.

## People's acts: the intervention ledger

`intervention_ledger` (`outcome-010`, on both providers) records one row per meaningful act by a
person. Nothing is priced from it and no screen, route or report reads it yet. Each row holds:

| Column | What it is |
|---|---|
| `id`, `occurred_at` | The row, and when the act happened (its log row's time) |
| `actor_id`, `actor_email` | The person (below) |
| `kind` | One of the closed set `InterventionKinds` |
| `team_id` | The team the act was on, or NULL (a tenant-level credential, a Concierge opened on no team) |
| `correlation` | The workflow the act was in, or NULL (a team resume, a credential, a Concierge session) |
| `outcome_id_at_event` | The workflow's outcome at that moment (its newest link, after the act), or NULL |
| `related_seq` | The message log row the act was written with, or NULL when its log row is a tenant row (or none) |
| `duration_seconds`, `duration_measured` | Measured time, and 1 when it was measured; NULL and 0 for every kind but a Concierge session |

The table is append-only: an UPDATE or DELETE is refused by trigger, as on the other ledgers. No
content is stored: no instruction text, prompt, answer, secret, connection value or connection
field.

**Who acted.** The route builds the act from its own signed-in principal, never from the request
body. A person signed in is their user id and email. An API key is its owner: the owner's id, and
their email read from `users` in the same transaction, so the row still reads after the key is
revoked and the owner deleted. A member, a member's key, the Concierge, a trigger, a schedule, a
wake and the Host are no person and write no row; so does any act through a route a member calls.

**Where each kind is written.** Each row lands in the same transaction as its log row, so the act is
recorded with it or not at all.

| Kind | Written when | Route | With the log row |
|---|---|---|---|
| `dispatch` | A person dispatches a backlog item to a team (existing or new) | `POST /api/teams/{team}/backlog/{id}/dispatch`, `POST /api/backlog/{id}/dispatch-to-new` | `backlog.item.dispatched`; the Manager's instruction after it is no second act |
| `instruction` | A person tells a member, which starts a workflow when it names no causation | `POST /api/teams/{team}/containers/{name}/tell` | the instruction row |
| `decision_answered` | A person's tell that answers a decision: it names the `needsDecision` row, or joins a workflow in which a member of the team is waiting on one | the same tell route | the instruction row |
| `resume` | A person releases a paused workflow, or a paused team | `POST /api/teams/{team}/workflows/{correlation}/resume`; `POST /api/teams/{team}/resume` | `workflow.resumed`; the `team.resumed` tenant row (only when the team was paused) |
| `nudge` | A person nudges a stalled workflow's Manager | `POST /api/teams/{team}/workflows/{correlation}/nudge` | the Manager's instruction |
| `close` | A person closes a workflow | `POST /api/teams/{team}/workflows/{correlation}/close` | `workflow.closed` |
| `outcome_changed` | A person, or a person's key, sets, changes or clears a workflow's outcome | `PUT` and `DELETE /api/teams/{team}/workflows/{correlation}/outcome` | the `workflow.outcome-changed` tenant row |
| `credential_supplied` | A person saves a connection's value, or a secret a member runs with: one row per value set or replaced | the connection routes that save a value (credential, mailbox, IMAP, password, an OAuth sign-in's completion), `PUT /api/secrets/{key}`, `PUT .../members/{member}/secrets/{field}`, `PUT .../named-secrets/{name}`, and a member's settings save carrying values | that save's tenant row (`connections.connected`, `.reconnected`, `.password-updated`, `.credentials-changed`, `secrets.set`, `.replaced`, `member.secret-set`, `.secret-replaced`, `member.named-secret-set`, `.named-secret-replaced`) |
| `access_resolved` | Nothing today: no run waits on an access grant a person resolves. The kind is in the set for later | none | none |
| `concierge_session` | A person's Concierge session ends | the Host, at the session's end | none: written alone |

A tell that also names an outcome is one act (`instruction`), not two. A clear of a secret, a rename,
a test, a team's connection assignment, a stop, a wake cancel, a card move or comment, a kanban
plan, a repository action, and deleting or merging an outcome (which may unlink workflows) are not
recorded. A Run now pressed by a person starts its workflow as its trigger, and writes no row.
`InterventionSites` lists every writer site, and a test fails when a Host route hands the log a
person's principal without recording an act and is not named as recording none.

**The Concierge session.** A session opens when the Host spawns a person's Concierge, and is one row
when it ends, however many chat messages it held, with `duration_seconds` measured open to close and
`duration_measured` 1. It ends when the person ends it, when its CLI exits, when the idle reaper ends
it, or when the Host stops. The person's end and the CLI's exit close it then. An abandoned session
(the reaper's, or one still open when the Host stops) closes at its last activity, so the unattended
time before it is not counted as the person's.

**Waiting is not attention.** Only a measured duration is attention. `blockedSeconds` stays the time
work waited on a person and is never added to an intervention's duration.

**Where it begins.** The start that applies `outcome-010` records `interventions.startedAt` once, a
`tenant_settings` row with its tenant row, as `ledger.startedAt` is; it never moves. Acts before it
were never recorded, and nothing reconstructs them from the log, so a human figure begins there.

## A shared budget is measured across every team

An outcome's budget belongs to the outcome, not to one team. When two teams both serve an outcome,
its budget is measured against **the outcome's total cost across all teams**, and both teams' tiles
show the same estimate against the same budget. A team's own part of that cost is in the dialog's
table, under **Estimated Execution Cost**; the **Budget** column beside it is the outcome's all-team total against its
budget.

## An outcome total counts each run once

Every outcome figure is a sum of the per-run estimates `ExecutionCost` makes
(`src/Harness.Host/ExecutionCost.cs`), across every team: `GET /api/outcomes` and
`GET /api/outcomes/{id}`, the merge preview, the Teams list's **Where it goes**, each team's
by-outcome entries, and `outcomeTotalCost`. Nothing else prices an outcome.

- **The key is the run** (`run_seq`), never the team. A run is counted once, in exactly one bucket:
  one outcome or **Unattributed**. A workflow whose runs are on two teams adds each run once, so
  an outcome two teams share is never counted twice.
- **Which bucket.** A run with a snapshot goes to the outcome its `link_id` named when it started,
  followed through `merged_into`. A link written after it started, an unlink included, does not
  move it, and nothing is ever re-attributed. A run from before snapshots keeps the workflow's
  newest link; nothing is back-filled.
- **Unattributed** is the work that serves no outcome: a run with no link, a run that started after
  an unlink, a run from before snapshots whose newest link is an unlink, and a run whose outcome
  was deleted. It is computed on every read, never stored: there is no Unattributed outcome row.
  Nothing is allocated or split between outcomes. (`noOutcome` on `GET /api/outcomes` carries it,
  named `Unattributed`.)
- **The identity.** For the same window and the same teams, Unattributed plus every outcome's
  total equals the period's Estimated Execution Cost, to the cent. Each run's part in each week
  is priced and rounded once, and the buckets and the total add the same parts.
- **Weighted rate.** An outcome's rate (`cost.agentRate`) is its priced agent time's money over its
  hours. Runs with no price and plugin time are left out. It is never the tenant rate, and never
  hours times an average.

### How a person reads an outcome: Project or Operational

An outcome has one model and two presentations. `outcomes.presentation` (`outcome-011`) is
`project`, `operational`, or NULL. NULL means nobody has picked one, and it reads as project. The
column is checked on both providers, and nothing back-fills it.

- **Who sets it.** A person, through the outcome edit route (`PATCH /api/outcomes/{id}` with
  `presentation`), the same people who may rename an outcome. The route is `HumansOnly`, so a
  member, a member key, a Manager, a Concierge credential, a trigger or the Host is refused
  (403), and nothing is stored. Any other value, an empty one or a different case included, is a
  400 and stores nothing, not even a rename sent with it. A change writes the edit's existing
  tenant row (`outcome.changed`, or `outcome.renamed` with a rename), and that row's detail names
  `presentation: { old, new }`. `old` is null when it was never picked.
- **Project** shows the outcome as a whole: all-time Agent Hours and Estimated Execution Cost,
  backlog progress (`<achieved> of <total> items achieved` and its bar), and a cumulative chart.
  The chart is the detail route's `figures.cumulative`: for each of the eight weeks, every run's
  part from the start of the ledger to the end of that week. Its last bar is the outcome's
  all-time figure.
- **Operational** shows the outcome week by week, over the same eight weekly buckets (there is no
  new period control):
  - the Estimated Execution Cost by week;
  - throughput (`figures.throughput`: the workflows serving it that closed in each week, every one
    and those completed);
  - cost per completed workflow, each week and over the eight weeks.

  Cost per completed workflow is the cost over the workflows completed. With none completed, or
  nothing priced, it reads **not measurable**. It is never `$0` and never a division by zero.
- **Both** keep the budget words, the budget bar and `value_amount` as they were. The opened
  outcome's cost is labelled `Estimated Execution Cost · <h> Agent Hours`, with `at <rate>/h` when
  its own weighted rate is known. It no longer names the tenant rate. Human Interventions and
  intervention frequency are not shown yet.

## The summary above the Teams list

Before any team is opened, the Teams list answers four questions in four tiles: what is this
costing, are we inside budget, where is the money going, and does anything need me. They sit in
one row above the list, two to a row on a narrower screen and one on a phone. At the right of the
**Teams** title, `Last 8 weeks · <first week> – <last day>` names the period: the first Monday of
the eight weeks and the last day of the eighth. Every figure is every team's work together,
from one call; when that call cannot be read, the tiles and the period line are not shown and the
list is unchanged.

- **Estimated Execution Cost** (`8 wk`): the eight weeks' estimate in the instance's currency; `▲ <n>%` or `▼ <n>%`
  `vs prior 8 wk`, the change on the eight weeks before (no line when those weeks had none);
  `<h> Agent Hours at <rate>/h`, the rate being the priced agent time's own; and the eight weeks as
  columns, one series, with each week's date and figure on hover. **With no priced run** it reads
  Agent Hours, the change is in hours, and the line says `no rate set`. Clicking it opens Outcomes.
- **Budgets** (`<n> outcomes`, how many carry a budget): the share used across every budgeted
  outcome, `<estimate> of <budget>` and a bar. Each outcome's estimate is its total across every
  team, as on the team page's tile. Then `All inside budget`, or, in the critical colour,
  `⚠ <outcome names> over`; and `· <amount> unbudgeted` when outcomes with no budget, or work
  linked to no outcome, cost anything. **No budgets:** `None set` and
  `Give an outcome a value to track it here.` **No rate:** `—` and
  `Set an agent hourly rate in Outcomes to measure Estimated Execution Cost against budgets.` Clicking it opens
  Outcomes.
- **Where it goes** (`by outcome`): the three outcomes that cost most, then **Other outcomes**
  (every other outcome), then **Unattributed** (the work that serves no outcome), each a bar with
  its amount: an outcome in its colour, the other two in neutral gray. Every run is in exactly one
  bar, so the bars add up to the Estimated Execution Cost tile's figure, to the cent. The bars share
  one scale. An outcome with a budget has a tick where its budget falls, and `⚠` after its name when
  it is over. Without a rate the bars are Agent Hours and carry no ticks. Clicking a bar opens that
  outcome in Outcomes; Other outcomes and Unattributed open the Outcomes list.
- **Right now**: open workflows, members running, and `⚠ waiting on you` - the same count the
  app bar's **Needs you** shows - in the warning colour above 0, and plain at 0. Clicking waiting on
  you opens the Needs-you list.

Status colours mark only an outcome over budget and what waits on you; the names, amounts and `⚠`
say the same without colour.

## The team page's tile

The team page's **Estimated Execution Cost · last 8 weeks** tile, beside Activity and Workflow
Status, shows:

- **The team's estimate in dollars** over the eight weeks, with `<h> Agent Hours at <rate>/h`
  under it, the rate being the priced agent time's own.
- **A small stacked weekly chart**, one colour per outcome, Unattributed in neutral gray at the
  bottom of each week.
- **With budgets** - when any outcome the team serves has one - a bar and
  `<estimate> of <budget> budget (<n>%)`. Estimate and budget are summed over the budgeted outcomes
  only, each outcome's estimate being its total across all teams. When the team also worked on unbudgeted
  outcomes or on no outcome, a second line says `+ <amount> on work with no budget`.
- **Over budget.** When any outcome's total estimate passes its budget, the bar turns the critical
  colour and the line ends `⚠ <n> over`, `<n>` being how many outcomes are over.
- **No budgets.** No bar, and `No outcome budget set`.
- **No priced run.** Agent Hours instead of dollars, the chart in hours, and
  `Set an agent hourly rate in Outcomes to see dollars`.

Clicking the tile opens the team's Estimated Execution Cost dialog.

## The Estimated Execution Cost dialog

Titled **Estimated Execution Cost · <team>**, with the instance's default rate as set now on the
right (`Default agent rate $60/h (USD) · Change in Outcomes`, or `No default agent rate set (USD)`).
That is the rate a new agent run with no rate of its own inherits, not the rate the figures were
priced at. It has two tabs.

**Estimated Execution Cost** (the tab it opens on):

- Headline figures: **Estimated Execution Cost**, **Agent time** (in Agent Hours), **Budget used**
  (only when an outcome has a budget), and **Per 1M billable tokens** - the estimate divided by the
  billable tokens in millions.
- A legend, and a large stacked weekly chart by outcome on one money axis (hours without a rate).
  Hovering a week lists each outcome's cost and hours and the week's total.
- A table: one row per outcome, then **Unattributed**, then **Team total**, with **Outcome**,
  **Agent Hours**, **Estimated Execution Cost**, **Budget** (a small bar and `<outcome total> / <budget>`, `⚠ over` when the
  outcome is over, `None set` when it has none), **Billable tokens** and **$ / 1M tokens**.
- A note under the table: the estimate prices each run at the rate it started with, not money
  paid, and the per-million figure explains the dollars against the tokens behind them. Tokens are
  not billed here.

**Tokens** is what the Tokens dialog showed before: the chart of the team's runs from the usage
ledger with its metric switch, then the team total from the message log with its in, cache read,
cache write and out breakout, by agent brand and by member.

## The Teams list's Estimated Execution Cost · 8 weeks column

Each row shows the team's stacked weekly chart at row height, the period total beside it (Agent
Hours without a rate), and `⚠` when one of the team's outcomes is over its budget. Clicking it opens
that team's Estimated Execution Cost dialog; clicking anywhere else on the row opens the team, as before. The whole
list is read in one call.

**Same scale**, a switch in the column's heading, sets how the rows' charts compare. Off (the
default), each row's chart uses its own scale, so a small team's weeks fill its chart as a big
team's do. On, every row is drawn on the largest team's scale - the biggest week of any team - so
bars compare across teams. The choice is remembered in this browser.

This column replaces the Activity column, and its **Same time span** switch and legend went with it.
The team page's Activity tile is unchanged.

## Colours

Outcomes keep a fixed order and colour. The order is the same on every team: Unattributed first,
then outcomes by when they were created, oldest first. An outcome's colour comes from its place in
that order among every outcome any team worked on in the eight weeks, not from its place on one
team. So an outcome has the same colour on the summary's Where it goes tile, on every row of the teams
list, on the team page's tile and in its dialog, even when a team works on only some outcomes. The first eight outcomes in that
order take the categorical palette's first eight slots, and any outcome after them shares one
neutral "Other" colour. Unattributed is always neutral gray and stacked first. Because the order
covers only the last eight weeks, an outcome can move up a slot when an older outcome has had no
work for eight weeks. Dark mode uses the same palette's dark steps.
Colour is never the only cue: the dialog's table carries every figure the chart draws.

## Routes

| Route | Answers |
|---|---|
| `GET /api/teams/{team}/cost` | One team: `money` (`currency`, and `agentHourlyRate`, the tenant default as set now), the eight `weeks`, and per outcome (`id` null for Unattributed) its `weekly` figures, `totals`, `value` (its budget), `outcomeTotalCost` (its estimate across all teams and all time) and `rank` (its place in the order shared by every team, which picks its colour; null for Unattributed); then the team's `totals`. `totals` carry `agentSeconds` (agent time only), `cost` (every priced run) and `agentRate` (the priced agent time's rate). `cost` is null, never 0, when no run was priced. Every `cost` is the estimate, never money paid. |
| `GET /api/billing-accounts`, `GET /api/billing-accounts/{id}` | Every billing account, active and deactivated, oldest first (an empty list on a fresh instance), or one; 404 for an id that names none. Each has `id`, `name`, `vendor`, `billingType`, `currency`, `billingPeriod`, `recurringAmount`, `effectiveFrom`, `effectiveTo`, `active`, `description`, `scope` (`tenant`), `instanceId`, `createdBy`, `createdAt` and `updatedAt`. People only. |
| `POST /api/billing-accounts`, `POST /api/billing-accounts/{id}`, `.../deactivate`, `.../reactivate` | Create an account, edit the fields sent (null clears only `description`, `effectiveTo` and `recurringAmount`), and turn `active` off or on. People only; there is no delete. |
| `GET /api/expenses`, `GET /api/expenses/{id}` | Every expense row, current and superseded, oldest recorded first (`?accountId=` for one account's; an empty list on a fresh instance), or one; 404 for an id that names none. Each has `id`, `accountId`, `amount`, `currency`, `servicePeriodStart`, `servicePeriodEnd` (exclusive), `kind`, `status` (`confirmed` or `void`), `supersedes`, `reference`, `note`, `recordedBy`, `recordedAt`, `supersededBy` and `current`. People only. |
| `POST /api/expenses`, `POST /api/expenses/{id}/void`, `POST /api/expenses/{id}/replace` | Record a confirmed expense; void one (an optional `note`); replace one with the fields sent over the old row's, on the same account. Each answers the new row. People only; there is no edit and no delete. |
| `GET /api/teams/cost` | The same for every team at once (`money`, `weeks`, `teams[]`), for the Teams list. |
| `GET /api/teams/cost/summary` | The summary above the Teams list: `rate` (the tenant default as set now, null without one); the eight `weeks` (`weekStart`, `agentSeconds`, `cost`), `periodStart` and `periodEnd`; the eight weeks' `total` and the eight before as `prior`, each with `agentRate`; per outcome (`id` null for Unattributed) its `name`, `rank`, `agentSeconds`, `cost`, `budget` and `outcomeTotalCost` (its cost across every team; this field was `spentTotal` before); and `rightNow` (`openWorkflows`, `membersRunning`, `waitingOnYou`). `cost` is null, never 0, when no run was priced. |

In the web app, one module decides every outcome's colour: `web/src/lib/outcomeColours.ts`. Its
`outcomeColour({ id, rank })` returns the colour for one outcome entry, `outcomeColours(entries)` builds
an id-to-colour map (keyed `null` for Unattributed) from one team's entries or every team's, and
`colourForRank(rank)` returns the colour for a bare rank. The summary's Where it goes tile, the teams
list's Estimated Execution Cost column, the team page's tile and its dialog all use it, and any new cost view should use it too, so that an outcome is never
two colours.
