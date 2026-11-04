# What a team's work is estimated to cost

A team's **Estimated Execution Cost** is read in money, by the outcomes it works on, and only then
in the tokens behind it. No screen measures it against an outcome budget (see
[Outcome values are not budgets](#outcome-values-are-not-budgets)). It is shown in four places: the
**summary** above the Teams list, the **Estimated Execution Cost** tile on the team's page, the
dialog that tile opens, and the **Estimated Execution Cost** and **Agent Hours** columns of the Teams list.

**It is an estimate, not money paid.** Each run is priced from the snapshot taken at its start:
its hours times the rate it started with. It is not what was paid or owed to any vendor, and none
of these screens call it spend.

Every figure comes from the usage ledger. The Teams list's columns and its Estimated Execution Cost
and Where it goes tiles, and the team page's tile, its dialog, the Activity tile's period line and
each workflow's line, read the period chosen above the list or the team page - one shared choice
([below](#the-teams-lists-period)). The Outcomes dashboard reads the last eight weeks (Monday-start
weeks in UTC, oldest first). The hours are measured;
only the money is an estimate.

## How a run is priced

- **Each run is priced from the snapshot taken at its start**, at the rate that snapshot records,
  rounded to the cent. The snapshot is described below.
- **An agent with no rate of its own** inherited the tenant rate at that start. With no tenant rate
  then, its run is unavailable: its time is in Agent Hours, and it adds no money.
- **A plugin with no rate of its own is not priced** and is not in Agent Hours. It never takes the
  agent default, on any read or screen.
- **A plugin with its own rate** is priced by duration, its time at that rate as recorded at its start,
  and adds its estimate to the headline only. Its time is still not in Agent Hours. Duration is the
  only plugin cost type: a plugin's manifest may say so (`"cost": {"type": "duration"}`) and any other
  type is refused (see [plugins.md](plugins.md#cost-type-cost)).
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

## Where the rate is set

- **The rate.** The instance's **agent hourly rate** is the tenant default an agent inherits when
  its run starts, in the instance's currency. A person sets both in **Admin → Outcomes**, with
  **change** beside the rate line (the **Pricing agent time** dialog). The Estimated Execution Cost
  dialog's **Change in Outcomes** link opens the same place. No figure is that rate times hours: a
  change prices the runs that start afterwards. A member's own rate, and its billing account, are
  set on the member: see [In Member settings](#in-member-settings).
- **No price, no dollars.** When no run in a figure has an estimate, the figure is Agent Hours
  instead. Nothing shows `$0` for time that has no price. This is not the same as the tenant rate
  being unset: a member's own rate still prices its runs.
- **No outcome budget.** No screen asks for, shows or measures an outcome budget. See
  [Outcome values are not budgets](#outcome-values-are-not-budgets).

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

### In Member settings

**Team settings → Members → a member's settings** (and the settings button on its card) has a
section, **Estimated hourly rate and billing account**, for agent and plugin members alike:

- **Estimated hourly rate:** **Inherit default** or **Override**.
  - The Inherit choice names the default: for an agent, the stored tenant default and currency
    ("Inherit default (60 USD/h)"), or "not set: not priced". A plugin member has no default, so its
    Inherit choice reads "not priced: a plugin member has no default"; it never falls back to the
    agent default.
  - An override is typed as a plain number. It is sent as `hourlyRate` and checked by the member
    PATCH's own rules: canonical text, `0` explicitly not priced, and a refusal shown as the Host
    words it. Choosing Inherit, or leaving the override blank, clears it.
- **Billing account:** No account, or one of the tenant's **active** billing accounts (each named
  with its vendor and currency). An inactive account appears only as the current value of a member
  that already holds one, marked inactive. A stored id that names no account (saved before billing
  accounts existed) shows as an unknown account and is kept until a person chooses another.
- **What a save sends.** The rate and the account are sent only when the person changed them. A save
  that changes only the name, only the rate or only the account leaves the other fields as stored,
  so it never touches a held account, inactive or unknown. A save that changes nothing sends nothing.
- **Effective configuration** (read-only): the CLI preset (or the plugin), the model ("not
  identifiable": a preset's model is a launch argument and is not recorded), the effective estimated
  hourly rate ("62.5 USD/h", or "Not priced") and its source in words ("source: member override",
  "tenant default" or "none"), and the billing account's name (with its vendor and currency, marked
  inactive, or an unknown account). These are the member read's own `effectiveRate` and `billingAccountId`,
  exactly as the Host answered them - the screen does not work them out - so they show what is
  stored and what a run starting now records, not what is being typed.
- **The sentence**, under the section: *"Changes apply to the next run; past runs keep the rate
  they started with."*
- **People only.** The member PATCH is `HumansOnly`: an agent, a member key, a Manager or an API key
  is refused the rate and the account, whatever permits it holds. The member read is unchanged and
  answers the same fields to whoever may read the member.

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
- **It never makes Actual Spend by itself.** A recurring amount is stored as it was given and is
  not added to any estimate. It is projected over periods only when read (see
  [Projected recurring charges](#projected-recurring-charges)), and a projection is never Actual
  Spend. Only confirmed expenses are Actual Spend, on [the Spend page](spend.md). Hours are never
  Actual Spend: every figure on the Teams list and team page is an **Estimated Execution Cost**.
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

Billing accounts are listed, added, edited and deactivated on [the Spend page](spend.md)'s Billing accounts tab, with the members linked to each, and through the routes below.

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

[The Spend page](spend.md) shows expenses and projections: Actual Spend is the current confirmed
expenses whose service period starts in the period, each counted whole there (never split by days),
per currency and never converted; Projected is apart and is never in it. Hours are never Actual
Spend.

## People's acts: the intervention ledger

`intervention_ledger` (`outcome-010`, on both providers) records one row per meaningful act by a
person. Nothing is priced from it. The human figures below are read from it, through the existing
outcome and cost reads; there is no route of its own. Each row holds:

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

## Outcome values are not budgets

An outcome can carry a stored amount, its **value** (`outcomes.value_amount`, `outcome-007`). It is
kept as it is, and it is no budget: no screen shows it, asks for it or measures an estimate against
it. There is no Budgets tile, budget bar, `<x> of <y> budget`, `⚠ over`, `None set` or
`on work with no budget` anywhere on the Teams list, the team page, the Estimated Execution Cost dialog
or Outcomes.

- **It stays readable and settable through the API.** `GET /api/outcomes` and `GET /api/outcomes/{id}`
  answer it as `value`; `POST /api/outcomes` and `PATCH /api/outcomes/{id}` accept `value` as before,
  checked by `OutcomeValue.Canonical`, and a change writes its `outcome.changed` tenant row
  (`OutcomeDashboardTests.An_outcomes_value_is_a_persons_edit_kept_as_a_plain_amount_and_anything_else_is_refused`). The cost
  reads keep carrying it unchanged - `value` and `outcomeTotalCost` on `GET /api/teams/{team}/cost`, and
  `budget` and `outcomeTotalCost` on `GET /api/teams/cost/summary` and the period reads - and the web
  no longer reads them.
- **Outcomes edits name and description only.** The settings dialog and **New outcome** have no field
  for it, and a save sends only the fields a person changed, so a stored value is never cleared or
  changed from the screen. The history reads `Details changed` for an `outcome.changed` row.
- **The token safeguards are not outcome budgets.** The per-workflow token budget, its line and bar on
  the team page's Workflow tile and in the Workflows dialog, the daily token caps and their limit
  sources are unchanged.
- **No export carries it.** None of the Spend page's CSV files ever had a budget column, so none
  changed.

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
- **The identity.** On the weekly reads (each team's cost, every team's, and the Teams list
  summary), Unattributed plus every outcome's total equals the period's Estimated Execution Cost
  for the same teams, to the cent. Each run's part in each week is priced and rounded once, and
  the buckets and the total add the same parts. With no window, `GET /api/outcomes` adds up the
  same way: Unattributed plus every outcome is every run's estimate, once. A windowed
  `GET /api/outcomes` prices each run's part inside the window as one span, rounded once, not week
  by week. Compared with the weekly reads over the same dates, its figures can differ by up to a
  cent for each run that crosses a Monday.
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
- **Both** show no budget: the opened outcome has no Budget figure and no share of it, and a tile reads
  `<amount> Estimated Execution Cost · <h> Agent Hours` (the Unattributed tile likewise). The opened
  outcome's cost is labelled `Estimated Execution Cost · <h> Agent Hours`, with `at <rate>/h` when
  its own weighted rate is known. It no longer names the tenant rate. **Waiting on a person** is
  its own figure beside **Agents working**.
- **The human figures** (below) are shown in both. Project shows all-time **Human
  Interventions** and **Human Attention Time**. Operational shows Human Attention Time, all time,
  and each week's Human Interventions (intervention frequency) and Human Attention Time as two
  more columns of the weekly table, beside cost and throughput. The Unattributed tile has a line
  with its Human Interventions and Human Attention Time, beside its money.

## Human figures: Human Interventions, Human Attention Time, Waiting on a person

Three figures describe what people did. They are read from `intervention_ledger` and the ledger's
blocked time. Each is computed on the service, on every read, by `HumanFigures`
(`src/Harness.Host/HumanFigures.cs`). Nothing is priced: there is no rate, no labour cost and no
value word near them.

- **Human Interventions** is the number of `intervention_ledger` rows whose `occurred_at` is in
  the read's window. Every kind counts, and each row counts once. A Concierge session is one row,
  so it is one intervention however many messages it held.
- **Human Attention Time** is the sum of `duration_seconds` over the rows with
  `duration_measured = 1`, and no others. Today only a Concierge session is measured. A row with
  no measured duration adds nothing, and nothing is estimated for it. When no row in the window
  was measured, the figure is null (`attentionSeconds: null`) and the screen says **no measured
  time**. It never shows 0 for that.
- **Waiting on a person** is the existing `blockedSeconds`: time work waited on a person. It is not
  attention. It is never added to Human Attention Time, and no figure, sum or label joins the two.

**Where an act counts.** For an outcome, a row counts under its `outcome_id_at_event`, followed
through `merged_into`, the same way a run follows its link. A row with no outcome, or whose outcome
was deleted, counts under **Unattributed**. Each row lands in exactly one bucket: it is never split,
and never counted once per team. For a team, rows are counted by `team_id`. **A Concierge session
carries no team** (its `team_id` is NULL), so a team's counts, and the all-teams summary, which sums
teams, leave Concierge sessions out. They still count under their outcome (Unattributed, as a
session names no workflow) on the outcome reads. Only a person's act writes a row: a member, a
member's key, an agent, a trigger, a wake and the Host write none, so they add nothing here. A
team's count is not cut at the team's floor the way its runs are: a team recreated under the same
id counts its predecessor's acts.

**Where they appear.** The same window and teams as the figures beside them:

- `GET /api/outcomes` and `GET /api/outcomes/{id}`: each outcome's `figures.human`
  (`interventions`, `measuredInterventions`, `attentionSeconds`) over the read's window, and
  `figures.humanWeekly`, the same over the eight weekly buckets whatever the window. `noOutcome`
  carries Unattributed's beside its money. `blockedSeconds` is Waiting on a person.
- `GET /api/teams/{team}/cost` and `GET /api/teams/cost`: each outcome entry's `human` (the team's
  acts under that outcome over the eight weeks), and the team's `human`: `interventions`,
  `measuredInterventions`, `attentionSeconds`, `unattributed` (the same three for its acts serving
  no outcome) and `waitingSeconds`, Waiting on a person over the eight weeks, from its runs above
  the team's floor.
- `GET /api/teams/cost/summary`: `human`, the reached teams' figures, each row once, with
  `unattributed` and `waitingSeconds`; and each listed outcome's `human`. An outcome is listed
  there by the existing rule, so acts under an outcome that is not listed are in the total only.

The Teams list's tiles and the team page's tiles do not show them.

**Where they begin.** At `interventions.startedAt`. Acts before it were never recorded, so a figure
over an earlier window counts only what followed it.

## The Teams list's period

One control sits at the right of the **Teams** title, above the summary tiles and the list:
**Daily** or **Monthly**, over **the last two months** (the previous UTC month and the current one,
`[first of last month, first of next month)`) or a **Custom range** (a first and a last day, both
included, read as UTC days). **Last two months** goes back to the default, which moves with the
clock. The period's name sits beside the control: `Last two months · Oct 1, 2026 – Nov 30, 2026`, a
whole month by its name, or a range by its first and last day, with `UTC`. The choice - the bucket,
and a custom range or none - is remembered in this browser under `harness.teamsCostPeriod`, beside
`harness.teamSort` and `harness.teamsShown`. A team page shows the same control and shares that one
choice (`web/src/lib/costPeriodChoice.ts`, `CostPeriodControl.vue`): a team opened from the list shows
the row that was clicked, in detail, and a period picked on a team page is the list's when the person
goes back.

The **Estimated Execution Cost** and **Agent Hours** columns, and the summary's **Estimated Execution
Cost** and **Where it goes** tiles, follow it; **Right now** does not. Every figure is
`GET /api/teams/cost/period`'s, read in one call for the whole list, and the web adds no money up
into a figure a person reads. The period is UTC, `from` inclusive and `to` exclusive, and a run
crossing an edge counts only its part inside, as `ExecutionCost` allocates every figure. A figure
over the period is one tally over the period, never the sum of its buckets: a run crossing a
bucket's edge is priced per bucket for the chart and once for the total, so a row's bars can add up
to a cent away from its total. A team's figure over a period is exactly what the Spend page's
**Estimated Execution Cost** says for that team's runs over the same period; the instance's total
is the Spend page's figure when every run belongs to a team the caller reaches.

## The summary above the Teams list

Before any team is opened, the Teams list answers three questions in three tiles: what is this
estimated to cost, where is it going, and does anything need me. They sit in one row above the
list, two to a row on a narrower screen and one on a phone. There is no Budgets tile. When the summary
cannot be read, the tiles are not shown and the list is unchanged; until the period's figures land,
the first and third tiles show the summary's eight weeks.

- **Estimated Execution Cost** (`by day` or `by month`): the period's estimate in the instance's
  currency; `▲ <n>%` or `▼ <n>%` `vs prior period`, the change on the same length of time just
  before (no line when that had none); `<h> Agent Hours at <rate>/h`, the rate being the priced agent
  time's own; and the period's days or months as columns, one series, with each one's date and
  figure on hover. **With no priced run** it reads `Estimate unavailable` with `<h> Agent Hours`, and
  `—` with no run at all. Clicking it opens Outcomes.
- **Where it goes** (`by outcome`, over the period): the three outcomes that cost most, then **Other outcomes**
  (every other outcome), then **Unattributed** (the work that serves no outcome), each a bar with
  its amount: an outcome in its colour, the other two in neutral gray. Every run is in exactly one
  bar, so the bars add up to the Estimated Execution Cost tile's figure, to the cent. The bars share
  one scale, the largest bar's; nothing marks an outcome against a budget. Without a rate the bars are
  Agent Hours. Clicking a bar opens that
  outcome in Outcomes; Other outcomes and Unattributed open the Outcomes list.
- **Right now**: open workflows, members running, and `⚠ waiting on you` - the same count the
  app bar's **Needs you** shows - in the warning colour above 0, and plain at 0. Clicking waiting on
  you opens the Needs-you list.

The status colour marks only what waits on you; the number and `⚠` say the same without colour.

## The team page's tile

The period control sits above the team page's three tiles, at the right. The **Estimated Execution
Cost · <period>** tile (`last two months`, a whole month by its name, or a first and last day),
beside Activity and Workflow, shows:

- **The team's Estimated Execution Cost over the period**, `GET /api/teams/{team}/cost/period`'s
  `totals.cost` - the same figure as the team's Teams list row - with the `*` footnote marker and its
  words under it when a reconstructed or fallback rate priced part of it. `Estimate unavailable` when
  the team ran in the period and nothing was priced; `—` when it did not run. Never $0 for unpriced
  time.
- `<h> Agent Hours at <rate>/h` under it: the period's Agent Hours and the route's weighted rate (the
  priced agent time's money over its hours), never the tenant setting; `<h> Agent Hours` alone when no
  agent time was priced.
- **A small stacked trend chart** over the period's days or months, one colour per outcome,
  Unattributed in neutral gray at the bottom, on **the team's own scale** (its tallest day or month),
  not the Teams list's shared one.
- **Nothing else.** No budget bar or budget line, and nothing from the eight weeks: the tile does not
  read `GET /api/teams/{team}/cost` at all (the dialog reads it when it opens, for its default-rate
  line). It shows no `Set an agent hourly rate in Outcomes to see dollars` note either: a rate set
  now prices only runs that start afterwards, so it could not change the period's figure, and
  `Estimate unavailable` already says nothing was priced. The rate is changed from the dialog's
  **Change in Outcomes** link.

Clicking the tile opens the team's Estimated Execution Cost dialog.

## The Estimated Execution Cost dialog

Titled **Estimated Execution Cost · <team>**, with the instance's default rate as set now on the
right (`Default agent rate $60/h (USD) · Change in Outcomes`, or `No default agent rate set (USD)`).
That is the rate a new agent run with no rate of its own inherits, not the rate the figures were
priced at. Like the tile, the dialog has no `Set an agent hourly rate` note when nothing in the period
was priced: a rate set now cannot price the period's runs, and `Estimate unavailable` says so. It has two tabs. Opened from the team page it uses the page's period read; opened from the
Teams list it reads `GET /api/teams/{team}/cost/period` itself, for the list's period.

**Estimated Execution Cost** (the tab it opens on), over the period, with the period control at its top:

- Headline figures: **Estimated Execution Cost · <period>** (the tile's figure), **Agent Hours** with
  `<h> Agent Hours at <rate>/h`, and **Per 1M billable tokens** -
  the estimate divided by the billable tokens of the runs that ended in the period, in millions.
- A legend, and a large stacked chart by outcome over the period's days or months, on the team's own
  scale (hours without a priced run). Hovering a column lists each outcome's figure and the column's total.
- **Breakdowns** (By outcome, By member, By workflow), each row the route's own figure:
  - **By outcome:** one row per outcome, then **Unattributed** (runs with no outcome), then **Team
    total**, with **Executions**, **Agent Hours**, **Estimated Execution Cost**, **Billable tokens** and
    **$ / 1M tokens**. There is no Budget column.
  - **By member:** agent members (**Executions**, **Agent Hours**, **Estimated Agent Cost**) and plugin
    members (**Executions**, **Estimated Plugin Cost**) in two tables. A plugin member's time is never
    Agent Hours; a plugin member with no rate of its own reads **Not priced**, never the agent default, and one
    whose runs in the period are all from before snapshots reads **Estimate unavailable**.
  - **By workflow:** each workflow that ran in the period, newest first, with **Executions**, **Agent
    Hours** and **Estimated Execution Cost**.
- **They add up.** Each run's part inside the period is priced once - in the team's total and in its
  row of each breakdown - and each breakdown splits the team's runs with none left over, so each
  breakdown's rows add up to the Team total exactly. Only the chart's columns are priced apart (a run
  crossing a day or month edge is priced per column), so they can add up to a cent away from it, the
  documented one-cent rule. A run crossing the period's edge counts only its part inside.
- A note under the breakdown: the estimate prices each run at the rate it started with, not money
  paid; the per-million figure explains the dollars against the tokens behind them. Tokens are not
  billed here.

**Tokens** is what the Tokens dialog showed before: the chart of the team's runs from the usage
ledger with its metric switch, then the team total from the message log with its in, cache read,
cache write and out breakout, by agent brand and by member.

## The team page's Activity and Workflow tiles

- **Activity.** The spans are unchanged and keep the team's own window. Under them, one line for the
  period: `<n> executions · <h> Agent Hours · <m> active members`, from the same period read's
  `totals` as the Cost tile - so its Agent Hours are the tile's, by construction. An **execution** is one
  run that started and whose span meets the period (a run crossing an edge counts in each period it
  touches); a member is **active** when it has one. The activity read (`GET /api/teams/{team}/activity`)
  counts no executions and scopes by the team's creation and activity-from rather than the cost floor,
  so these come from the cost read.
- **Workflow.** Name and layout are unchanged; active, completed and blocked counts as before. Beside
  them, `Needs you · <n>`: the app bar's one Needs you list (`GET /api/needs-you`, held by the web's
  Needs you store) filtered to the team - the rows whose team is this one, and a connection row on
  every team it holds up. Zero is shown; nothing is shown before the list has been read.

## A workflow's line

Each row of the team page's Workflows dialog, and an opened Kanban card's detail, carries one line:
`Agent Hours <h> · Estimated Execution Cost <amount>`, from `GET /api/teams/{team}/cost/workflows`
(the dialog reads every workflow once when it opens; a card reads its own with `?workflow=`). It is
the workflow's whole life - every run of it on the team, each priced whole from its own snapshot -
not the period. `Estimate unavailable` when it ran unpriced; `Agent Hours 0.0 · Estimated Execution
Cost —` when it has no runs. The cards on the board are unchanged.

## The Teams list's Estimated Execution Cost and Agent Hours columns

Over the period above the list, each row shows the team's chart by outcome at row height - one
column per day or month, Unattributed first in neutral gray, each outcome in its colour - and the
period's **Estimated Execution Cost** beside it, then the team's **Agent Hours** in a column of their
own, so a team with little work stays readable. No mark follows the total for an outcome budget.
Clicking the chart opens that team's Estimated Execution Cost dialog over the same period; clicking anywhere else on the row opens the team, as before. The whole list is read in one
call.

- **One relative scale.** Every row's chart is drawn against the same top: the tallest day or month of
  any team listed, in money when any team had a priced run in the period and in Agent Hours
  otherwise. There is no switch; the **Same scale** toggle and its `harness.teamsCostSameScale` key
  were removed, and a value left in a browser under that key is no longer read.
- **The total.** The route's own figure, with the `*` footnote marker (its words as the title) when a
  reconstructed or fallback rate priced part of it. `Estimate unavailable` when the team ran in the
  period and none of it was priced; `—` when it did not run. Agent Hours are agent-kind time, priced or
  not: `0.0` for a team that did not run.
- **Sorting.** **Estimated Execution Cost** and **Agent Hours** sort like Team, Members and Status:
  the first click ascending, the next descending, remembered under `harness.teamSort`. A team with no
  Estimated Execution Cost for the period (it did not run, nothing was priced, or the figures have not
  landed) sorts after every team with one, in both directions; a team that did not run has 0 Agent
  Hours and sorts as 0. Ties, and the teams with no figure, are ordered by name, A to Z, in both
  directions.

This column replaced the Activity column, and its **Same time span** switch and legend went with it.

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
Colour is never the only cue: the dialog's table carries every figure the chart draws. Over the Teams
list's period, the list's rows and its Where it goes tile take each outcome's rank among the outcomes
served in that period instead of the eight weeks, by the same order; so do the team page's tile and
its dialog, ranked among the outcomes served by the teams the caller reaches, as on the list.

## Reporting: the `report` tool

Agents read cost figures for a report through `report`, a read-only MCP tool on the harness server
(`src/Harness.Host/PlatformMcpReportTools.cs`).

- **One route per action, unchanged.** Each action sends one GET that a screen makes, with the
  caller's own credential. It answers `HTTP <status>`, then one heading line in the product's
  words, then the route's own JSON byte for byte. So every figure is the route's, priced by
  `ExecutionCost` and nothing else.
- **The same reach as the screens.** The team gate bounds the tool as it bounds the route: a
  member reaches only its own team, and another team's figures are refused with the route's
  sentence.
- **Period.** `from` and `to` are `YYYY-MM-DD`, UTC, with `to` exclusive. With neither, every action
  reads the current UTC month. `bucket` is `day` or `month` for `teams` and `team`.

| Action | Route | What it gives |
|---|---|---|
| `teams` | `GET /api/teams/cost/period` | Estimated Execution Cost and Agent Hours for each team the caller reaches: the Teams list's rows |
| `team` | `GET /api/teams/{team}/cost/period` | One team's period, by outcome (Unattributed), by member (agent and plugin apart; `unpriced` `not-priced` or `unavailable`) and by workflow: the team page's tile and dialog |
| `workflows` | `GET /api/teams/{team}/cost/workflows` | Each workflow's Agent Hours and Estimated Execution Cost, all time |
| `outcomes` | `GET /api/outcomes?from=&to=` | Estimate, Agent Hours, Human Interventions and Human Attention Time by outcome and Unattributed, across every team |
| `human` | `GET /api/teams/{team}/cost` | A team's Human Interventions and Human Attention Time over its last eight weeks (the period is not read) |
| `spend` | `GET /api/spend/overview` | Actual Spend and Projected for the period, per currency, with the sentence that the two measurements need not reconcile. Needs `Spend` |
| `accounts` | `GET /api/billing-accounts` | The billing accounts. Needs `Spend` |

- **Words.** The estimate actions' heading names Estimated Execution Cost and Agent Hours, and says
  the figure is an estimate and never Actual Spend. It says that `cost` null is Estimate
  unavailable, never 0, that `legacyRates` names a reconstructed or fallback-at-upgrade rate, and
  that money is never converted. No estimate field the tool answers is named `spend`.
- **Actual Spend needs the permit.** Without the `Spend` permit, `spend` and `accounts` are
  refused with the one sentence in [spend.md](spend.md#who-may-read-it-and-who-may-write). Nothing
  in the tool writes.
- **Not in the tool.** There is no scheduled delivery, no figure from another instance, and no PDF
  or mail. Writing a report from the tool is the `outcome-reporting` skill's
  ([below](#writing-a-report-the-outcome-reporting-skill)).
- **Tests.** Pinned by `ReportToolTests` and `SpendReadingRuleTests` in
  `tests/Harness.Tests/Host/ReportToolTests.cs`, and
  `RouteMarkerTests.Exactly_the_Actual_Spend_reads_require_the_Spend_permit_and_every_spend_write_stays_humans_only`.

## Writing a report: the `outcome-reporting` skill

The built-in skill `outcome-reporting` (`src/Harness.Host/BuiltInSkills.cs`) teaches an agent to
write a report on a team, an outcome or a period from the `report` tool and nothing else. It is
offered to the Manager and to members. A member writes the report; the Manager dispatches it and
checks the hand-back against the skill. The Concierge is not offered it: it has no shared folder,
and its own skill already sends a report to a team.

- **Copy, never compute.** Every figure is one the tool answered: a total is the answer's own
  `total` or `totals`, a rate its `agentRate`. The one conversion is Agent Hours from
  `agentSeconds` (one decimal under 100 hours, as the screens write it). Actual Spend is never
  inferred from hours or rates. No currencies are added or converted. Estimated Execution Cost and
  Actual Spend never share one figure, row, column or total.
- **The words.** The terms of this page and [spend.md](spend.md), with these rules:
  - `cost` null is Estimate unavailable;
  - `legacyRates` is carried as a footnote, and a plugin member's `unpriced` as Not priced or
    Estimate unavailable;
  - an outcome's stored value is never treated as a budget;
  - the Spend page's sentence goes wherever both measurements appear.
- **Actual Spend only when allowed.** When `spend` is refused, the report carries the estimates
  only, with a line saying a person must turn on **Let agents read Actual Spend for reports**.
- **Files.** Markdown, HTML (self-contained, no script) and CSV, written to the team's shared
  folder. The CSV files are one per measurement: `<name>-estimated-execution-cost.csv` and, only
  when `spend` answered, `<name>-actual-spend.csv`.
- **What it cannot make or send.**
  - The platform writes no PDF and no XLSX. CSV is the spreadsheet, and the HTML prints to PDF
    from a browser. A PDF is made only by a plugin member whose own skill says it makes one.
  - Mail goes only through a mail plugin member a person has set up on the team; without one,
    the report says it was not sent.
  - A recurring report runs from a trigger a person sets. There is no report scheduler.
- **Tests.** Pinned by `OutcomeReportingSkillTests` in
  `tests/Harness.Tests/Host/OutcomeReportingSkillTests.cs`:
  - the roles;
  - the actions it names against `PlatformMcpTools.ReportActions`;
  - the words, and that no estimate is called spend;
  - copying, never computing;
  - estimates only when Actual Spend is refused;
  - the files and limits.

  The general built-in skill rules hold too: `McpContractTests.Built_in_skills_send_agents_to_mcp_tools_not_the_http_api`.

## Routes

| Route | Answers |
|---|---|
| `GET /api/teams/{team}/cost` | One team: `money` (`currency`, and `agentHourlyRate`, the tenant default as set now), the eight `weeks`, and per outcome (`id` null for Unattributed) its `weekly` figures, `totals`, `value` (its stored value, no screen reads it), `outcomeTotalCost` (its estimate across all teams and all time) and `rank` (its place in the order shared by every team, which picks its colour; null for Unattributed), and `human` (its Human Interventions and Human Attention Time); then the team's `totals`, and its `human` with `unattributed` and `waitingSeconds` (Waiting on a person). `totals` carry `agentSeconds` (agent time only), `cost` (every priced run) and `agentRate` (the priced agent time's rate). `cost` is null, never 0, when no run was priced. Every `cost` is the estimate, never money paid. |
| `GET /api/billing-accounts`, `GET /api/billing-accounts/{id}` | Every billing account, active and deactivated, oldest first (an empty list on a fresh instance), or one; 404 for an id that names none. Each has `id`, `name`, `vendor`, `billingType`, `currency`, `billingPeriod`, `recurringAmount`, `effectiveFrom`, `effectiveTo`, `active`, `description`, `scope` (`tenant`), `instanceId`, `createdBy`, `createdAt` and `updatedAt`. People, and agents holding `Spend`. |
| `POST /api/billing-accounts`, `POST /api/billing-accounts/{id}`, `.../deactivate`, `.../reactivate` | Create an account, edit the fields sent (null clears only `description`, `effectiveTo` and `recurringAmount`), and turn `active` off or on. People only; there is no delete. |
| `GET /api/expenses`, `GET /api/expenses/{id}` | Every expense row, current and superseded, oldest recorded first (`?accountId=` for one account's; an empty list on a fresh instance), or one; 404 for an id that names none. Each has `id`, `accountId`, `amount`, `currency`, `servicePeriodStart`, `servicePeriodEnd` (exclusive), `kind`, `status` (`confirmed` or `void`), `supersedes`, `reference`, `note`, `recordedBy`, `recordedAt`, `supersededBy` and `current`. People, and agents holding `Spend`. |
| `POST /api/expenses`, `POST /api/expenses/{id}/void`, `POST /api/expenses/{id}/replace` | Record a confirmed expense; void one (an optional `note`); replace one with the fields sent over the old row's, on the same account. Each answers the new row. People only; there is no edit and no delete. |
| `GET /api/spend/overview`, `GET /api/spend/linked-members` | The Spend page's reads: Actual Spend, Projected, fixed vs variable, Estimated Execution Cost · Agent Hours and the monthly trend for a period, and the members linked to each billing account. See [spend.md](spend.md#routes). People, and agents holding `Spend`. |
| `GET /api/spend/export/billing-accounts`, `.../expenses`, `.../period-summary`, `.../estimated-execution-cost` | The Spend page's CSV files for a period (`?from=&to=`, the overview's period rule): every billing account; every expense row of the period with its correction chain and whether it counts in Actual Spend; the overview's figures one row each; and Estimated Execution Cost with Agent Hours by team and by outcome (Unattributed included), one row per `rate_source`, agent and plugin apart. Stable snake_case columns, `instance_id` on every row and `scope` last (expenses also `billing_account_instance_id`), per currency and never converted, formula cells neutralised. See [spend.md](spend.md#csv-export). People, and agents holding `Spend`. |
| `GET /api/teams/cost` | The same for every team at once (`money`, `weeks`, `teams[]`), for the Teams list. Each entry and each team also carry `human` (above). |
| `GET /api/teams/cost/period` | The Teams list's period (`?from=&to=` as `YYYY-MM-DD`, midnight UTC, `to` exclusive, the previous and current UTC month when neither is sent; `?bucket=day` (the default) or `month`; at most 366 days by day or 60 months by month; anything else is refused, 400, with one sentence): `money`, `period` (`from`, `to`, `bucket`), `buckets` (each bucket's first day), and per reachable team `totals` (`agentSeconds`, `cost`, `agentRate`, `legacyRates`) over the whole period, `buckets` (`start`, `agentSeconds`, `cost`) and `outcomes` (Unattributed first, each with `rank`, `totals` and `buckets`); the reached teams' `total`, `totalBuckets` and `prior` (the same length just before); and `outcomes` as the summary gives them. Every figure is `ExecutionCost`'s, one tally per span, `cost` null - never 0 - when nothing was priced. The same permit as the other cost reads. |
| `GET /api/teams/{team}/cost/period` | One team over the same period (`?from=&to=&bucket=`, read and refused exactly as `GET /api/teams/cost/period`; 404 for an unknown team, the team gate for a machine principal): `team`, `money`, `period`, `buckets`; `totals` - the team's entry of `GET /api/teams/cost/period` for that period, byte for byte - with `billable`, `measuredRuns`, `unmeasuredRuns` (tokens of runs that ended in the period), `executions` and `activeMembers`; `totalBuckets` (the entry's `buckets`); `outcomes` (the entry's, each with `value`, `outcomeTotalCost` and `figures`); `members` (agent members first, then plugin members, by name: `member`, `kind`, `figures`; a plugin member with no money in the period also has `unpriced` - `not-priced` when a run started with no rate of its own, `unavailable` when every run is from before snapshots - and no other entry has it) and `workflows` (newest first: `workflow`, `figures`). Each `figures` is `agentSeconds`, `cost`, `agentRate`, `legacyRates`, `executions`, `billable`, `measuredRuns`, `unmeasuredRuns`. Each breakdown adds up to `totals` exactly. The same permit as the other cost reads. |
| `GET /api/teams/{team}/cost/workflows` | Each workflow of the team, all time (`?workflow=N` for one): `team`, `money`, `workflows` (`workflow`, `figures` as above, every run whole). A workflow with no runs is absent. The same permit as the other cost reads. |
| `GET /api/teams/cost/summary` | The summary above the Teams list: `rate` (the tenant default as set now, null without one); the eight `weeks` (`weekStart`, `agentSeconds`, `cost`), `periodStart` and `periodEnd`; the eight weeks' `total` and the eight before as `prior`, each with `agentRate`; per outcome (`id` null for Unattributed) its `name`, `rank`, `agentSeconds`, `cost`, `budget` (its stored value; no screen reads it) and `outcomeTotalCost` (its cost across every team; this field was `spentTotal` before); and `rightNow` (`openWorkflows`, `membersRunning`, `waitingOnYou`); `human` (the reached teams' human figures, with `unattributed` and `waitingSeconds`) and each outcome's `human`. `cost` is null, never 0, when no run was priced. |

In the web app, one module decides every outcome's colour: `web/src/lib/outcomeColours.ts`. Its
`outcomeColour({ id, rank })` returns the colour for one outcome entry, `outcomeColours(entries)` builds
an id-to-colour map (keyed `null` for Unattributed) from one team's entries or every team's, and
`colourForRank(rank)` returns the colour for a bare rank. The summary's Where it goes tile, the teams
list's Estimated Execution Cost column, the team page's tile and its dialog (`web/src/lib/teamPeriod.ts`) all use it, and any new cost view should use it too, so that an outcome is never
two colours.
