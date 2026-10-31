# What a team costs

A team's cost is read in money, by the outcomes it works on, against the budgets set for them, and
only then in the tokens behind it. It is shown in four places: the **summary** above the Teams
list, the **Cost** tile on the team's page, the **Cost** dialog that tile opens, and the
**Cost · 8 weeks** column of the Teams list.

Every figure comes from the usage ledger, over the last eight weeks (Monday-start weeks in UTC,
oldest first, the same weeks the Outcomes dashboard uses). Nothing is estimated.

## Where the rate and budgets are set

- **The rate.** Cost is agent hours times the instance's **agent hourly rate**, in its currency. A
  person sets both in **Admin → Outcomes**, with **change** beside the rate line (the **Pricing
  agent time** dialog). The Cost dialog's **Change in Outcomes** link opens the same place.
- **No rate, no dollars.** Until a rate is set, every cost figure is agent hours instead. Nothing
  shows `$0` for time that has no price.
- **Budgets.** An outcome's **Budget** (in Admin → Outcomes, on the outcome, or when creating one)
  is what achieving it is worth: what it may spend. An outcome with no budget, and the team's work
  linked to no outcome at all ("No outcome"), have none.

## A shared budget is measured across every team

An outcome's budget belongs to the outcome, not to one team. When two teams both serve an outcome,
its budget is measured against **the outcome's total cost across all teams**, and both teams' tiles
show the same spent against the same budget. A team's own part of that cost is in the dialog's
table, under **Cost**; the **Budget** column beside it is the outcome's all-team total against its
budget.

## The summary above the Teams list

Before any team is opened, the Teams list answers four questions in four tiles: what is this
costing, are we inside budget, where is the money going, and does anything need me. They sit in
one row above the list, two to a row on a narrower screen and one on a phone. At the right of the
**Teams** title, `Last 8 weeks · <first week> – <last day>` names the period: the first Monday of
the eight weeks and the last day of the eighth. Every figure is every team's work together,
from one call; when that call cannot be read, the tiles and the period line are not shown and the
list is unchanged.

- **Spend** (`8 wk`): the eight weeks' cost in the instance's currency; `▲ <n>%` or `▼ <n>%`
  `vs prior 8 wk`, the change on the eight weeks before (no line when those weeks had none);
  `<h> agent h at <rate>/h`; and the eight weeks as columns, one series, with each week's date and
  figure on hover. **Without a rate** it reads agent hours, the change is in hours, and the line
  says `no rate set`. Clicking it opens Outcomes.
- **Budgets** (`<n> outcomes`, how many carry a budget): the share used across every budgeted
  outcome, `<spent> of <budget>` and a bar. Each outcome's spent is its total across every team,
  as on the Cost tile. Then `All inside budget`, or, in the critical colour,
  `⚠ <outcome names> over`; and `· <amount> unbudgeted` when outcomes with no budget, or work
  linked to no outcome, spent anything. **No budgets:** `None set` and
  `Give an outcome a value to track it here.` **No rate:** `—` and
  `Set an agent hourly rate in Outcomes to measure spend against budgets.` Clicking it opens
  Outcomes.
- **Where it goes** (`by outcome`): the three outcomes that spent most, then
  **Other / no outcome** (every other outcome and the work linked to none), each a bar in its
  outcome's colour with its amount. The four bars share one scale. An outcome with a budget has a
  tick where its budget falls, and `⚠` after its name when it is over. Without a rate the bars are
  agent hours and carry no ticks. Clicking a bar opens that outcome in Outcomes; Other opens the
  Outcomes list.
- **Right now**: open workflows, members running, and `⚠ waiting on you` - the same count the
  app bar's **Needs you** shows - in the warning colour above 0, and plain at 0. Clicking waiting on
  you opens the Needs-you list.

Status colours mark only an outcome over budget and what waits on you; the names, amounts and `⚠`
say the same without colour.

## The Cost tile

The team page's **Cost - last 8 weeks** tile shows:

- **The team's dollars** over the eight weeks, with `<h> agent h at <rate>/h` under it.
- **A small stacked weekly chart**, one colour per outcome, "No outcome" in neutral gray at the
  bottom of each week.
- **With budgets** - when any outcome the team serves has one - a bar and
  `<spent> of <budget> budget (<n>%)`. Spent and budget are summed over the budgeted outcomes only,
  each outcome's spent being its total across all teams. When the team also spent on unbudgeted
  outcomes or on no outcome, a second line says `+ <amount> on work with no budget`.
- **Over budget.** When any outcome's total spend passes its budget, the bar turns the critical
  colour and the line ends `⚠ <n> over`, `<n>` being how many outcomes are over.
- **No budgets.** No bar, and `No outcome budget set`.
- **No rate.** Agent hours instead of dollars, the chart in hours, and
  `Set an agent hourly rate in Outcomes to see dollars`.

Clicking the tile opens the Cost dialog.

## The Cost dialog

Titled **Cost · <team>**, with the rate line on the right (`Agent time at $60/h (USD) · Change in
Outcomes`). It has two tabs.

**Cost** (the tab it opens on):

- Headline figures: **Spent**, **Agent time**, **Budget used** (only when an outcome has a budget),
  and **Per 1M billable tokens** - the cost divided by the billable tokens in millions.
- A legend, and a large stacked weekly chart by outcome on one money axis (hours without a rate).
  Hovering a week lists each outcome's cost and hours and the week's total.
- A table: one row per outcome, then "No outcome", then **Team total**, with **Outcome**,
  **Agent h**, **Cost**, **Budget** (a small bar and `<outcome total> / <budget>`, `⚠ over` when the
  outcome is over, `None set` when it has none), **Billable tokens** and **$ / 1M tokens**.
- A note under the table: the per-million figure explains the dollars against the tokens behind
  them. Tokens are not billed here: cost is agent time at the rate.

**Tokens** is what the Tokens dialog showed before: the chart of the team's runs from the usage
ledger with its metric switch, then the team total from the message log with its in, cache read,
cache write and out breakout, by agent brand and by member.

## The Teams list's Cost · 8 weeks column

Each row shows the team's stacked weekly chart at row height, the period total beside it (agent
hours without a rate), and `⚠` when one of the team's outcomes is over its budget. Clicking it opens
that team's Cost dialog; clicking anywhere else on the row opens the team, as before. The whole
list is read in one call.

**Same scale**, a switch in the column's heading, sets how the rows' charts compare. Off (the
default), each row's chart uses its own scale, so a small team's weeks fill its chart as a big
team's do. On, every row is drawn on the largest team's scale - the biggest week of any team - so
bars compare across teams. The choice is remembered in this browser.

This column replaces the Activity column, and its **Same time span** switch and legend went with it.
The team page's Activity tile is unchanged.

## Colours

Outcomes keep a fixed order and colour. The order is the same on every team: "No outcome" first,
then outcomes by when they were created, oldest first. An outcome's colour comes from its place in
that order among every outcome any team worked on in the eight weeks, not from its place on one
team. So an outcome has the same colour on the summary's Where it goes tile, on every row of the teams
list, on the team page's tile and in the Cost dialog, even when a team works on only some outcomes. The first eight outcomes in that
order take the categorical palette's first eight slots, and any outcome after them shares one
neutral "Other" colour. "No outcome" is always neutral gray and stacked first. Because the order
covers only the last eight weeks, an outcome can move up a slot when an older outcome has had no
work for eight weeks. Dark mode uses the same palette's dark steps.
Colour is never the only cue: the dialog's table carries every figure the chart draws.

## Routes

| Route | Answers |
|---|---|
| `GET /api/teams/{team}/cost` | One team: `money` (`currency`, `agentHourlyRate`), the eight `weeks`, and per outcome (`id` null for No outcome) its `weekly` figures, `totals`, `value` (its budget), `outcomeTotalCost` (its cost across all teams) and `rank` (its place in the order shared by every team, which picks its colour; null for No outcome); then the team's `totals`. `cost` is null, never 0, when no rate is set. |
| `GET /api/teams/cost` | The same for every team at once (`money`, `weeks`, `teams[]`), for the Teams list. |
| `GET /api/teams/cost/summary` | The summary above the Teams list: `rate` (null without one); the eight `weeks` (`weekStart`, `agentSeconds`, `cost`), `periodStart` and `periodEnd`; the eight weeks' `total` and the eight before as `prior`; per outcome (`id` null for No outcome) its `name`, `rank`, `agentSeconds`, `cost`, `budget` and `spentTotal` (its spend across every team); and `rightNow` (`openWorkflows`, `membersRunning`, `waitingOnYou`). `cost` is null, never 0, when no rate is set. |

In the web app, one module decides every outcome's colour: `web/src/lib/outcomeColours.ts`. Its
`outcomeColour({ id, rank })` returns the colour for one outcome entry, `outcomeColours(entries)` builds
an id-to-colour map (keyed `null` for No outcome) from one team's entries or every team's, and
`colourForRank(rank)` returns the colour for a bare rank. The summary's Where it goes tile, the teams
list's Cost column, the Cost tile and the Cost dialog all use it, and any new cost view should use it too, so that an outcome is never
two colours.
