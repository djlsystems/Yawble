# Outcomes as a dashboard (design, 2026-10-05)

**Status:** approved in conversation, implemented directly at the person's request.

## Who it is for

A product owner, and above them a CTO. The Outcomes dialog answers, at a glance: what is each
outcome's work, how far along is it, what has it cost against what it is worth, and how much of
its time was spent waiting on a person.

## Decisions

1. **A backlog item inherits the outcome its dispatch workflow is linked to**, only when the item
   has none. Whoever links the workflow (a Manager's `set` or `propose`, a person's Change outcome…,
   a `tell`) - the item takes the link's outcome in the same transaction as the link row, with a
   `backlog.item-outcome-inherited` tenant row naming the item, the outcome and the link's author.
   It never overwrites an item's outcome and never clears one: a person's "None" leaves the item
   alone. Items from before this are back-filled once at start, each from its newest dispatch
   workflow's current outcome (followed through merges), recorded by the `backlog.outcomesInheritedAt`
   tenant setting and one tenant row; a second start does nothing.
2. **Efficiency is agent time against time blocked on a person.** Blocked is the team activity
   read's span: from a run that ended `blocked` (the ledger's `run_outcome`, which covers
   needs-decision) to the same member's next queue or start, or to now while it has none. It is
   credited to the blocked run's workflow. `efficiency = agentSeconds / (agentSeconds + blockedSeconds)`,
   null when both are 0, never 0%.
3. **Money: cost and budget.** (First called value; renamed Budget on 2026-10-05 at the person's request. The API field and column stay `value` / `value_amount`.)
   - `outcomes.agentHourlyRate` (whole currency units an agent hour, 0 = not set) and
     `outcomes.currency` (an ISO 4217 code from a fixed list, default `usd`, stored lower-case as every choice is and answered upper-case) are tenant settings.
   - Each outcome gets a `value` (`outcome-007` adds `outcomes.value_amount`, a non-negative decimal
     as text), edited by a person through the existing edit route with its tenant row.
   - Cost = agent hours x the current rate, computed by the route at read time, null when no rate is
     set (never 0). Changing the rate re-prices history, by design.
   - The target metric, unit and target are no longer shown or written. Their columns stay:
     a shipped step is never edited.
4. **Backlog buckets per outcome**, counted by the route from items whose outcome resolves to it:
   - Not started: not archived, `pending` or `ready`, never dispatched.
   - In progress: dispatched and neither `declared` nor `implemented`.
   - Achieved: `implemented`, archived or not.
   - `declared` counts as Achieved by default, or In progress under
     `outcomes.declaredCountsAs = in-progress`.
   - Archived items never finished are not counted.
5. **Weekly spend**: the last 8 weeks, Monday-start in UTC, ending with the current week - per week
   agent seconds, billable tokens, measured and unmeasured runs, and cost. Always 8 weeks,
   whatever the list's period filter says.
6. **"No outcome" is renamed "No outcome assigned"** and shown last, as a dashed tile.

## Shape

- `GET /api/outcomes` and `GET /api/outcomes/{id}`: each figures object gains `backlog`,
  `blockedSeconds`, `efficiency`, `weekly`, `cost`; the answer gains `money` (`currency`,
  `agentHourlyRate`, `declaredCountsAs`). The detail gains `backlogItems` (each bucket's items: id,
  citation, title, state, team, workflow, landed). Existing fields are kept.
- The dialog: a grid of tiles, money first (spend against budget, the 8-week chart, the backlog
  bar, a summary line of completed/active workflows, pending items and efficiency). The detail:
  header and actions, a money row (budget editable, cost, % of budget spent, efficiency), the weekly
  chart, three backlog columns, and today's workflows and history folded below. A rate line above
  the tiles opens an editor for the three settings.

## Not changed

The completion gate, the link rules (a person's link is never moved by an agent, an unlink is a
row, a rename or merge never rewrites a link), the kanban tag and filter, and every field the
routes already answer.
