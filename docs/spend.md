# The Spend page: Actual Spend, Projected and Estimated Execution Cost

**Admin → Spend** opens the tenant's Spend page at `#/spend`. It shows what was paid to vendors next to
what agent and plugin time is estimated to have cost, and it keeps the two apart. Billing accounts,
expenses and projected recurring charges are described in [cost.md](cost.md#billing-accounts), and
the estimate in [cost.md](cost.md#how-a-run-is-priced).

## Three measurements, never mixed

- **Actual Spend** is what you paid vendors: the **current, confirmed** expenses in the period, and
  nothing else. A row that has been voided or replaced is counted once, by its current row. A void
  row is not confirmed, so a voided expense adds nothing, and a replaced expense counts only its
  replacement. Actual Spend is **never inferred from hours or estimates**. A period with no
  confirmed expense says *No confirmed expenses in this period*; it does not show an estimate, a
  projection or a zero in that place.
- **Projected** is the recurring charges expected but not yet invoiced, as
  [Projected recurring charges](cost.md#projected-recurring-charges) computes them. They are labelled
  projected and are **never added to Actual Spend**. A projection for an account and period
  **disappears once a confirmed expense covers it**, meaning a current, confirmed expense on that
  account whose service period overlaps it.
- **Estimated Execution Cost · Agent Hours** for the same period comes from the one pricing path
  every cost figure uses (`ExecutionCost`). Each run that overlaps the period counts the part of its
  span inside the period, priced from its snapshot. It is the same sum a windowed
  `GET /api/outcomes?from=&to=` gives across every outcome plus Unattributed, over every team.
  Nothing new is priced.

Wherever Actual Spend and Estimated Execution Cost appear together, the page shows this sentence:

> Actual Spend is what you paid vendors. Estimated Execution Cost is agent and plugin time at your
> estimated hourly rates. They are different measurements and need not reconcile.

**Fixed subscriptions vs variable** groups the accounts by `billing_type`. `subscription` accounts
are fixed; `metered` and `service` accounts are variable. Each group is shown by type and currency,
with its Actual Spend and its Projected amount apart.

## Currencies

Totals are **per currency**, by currency code, and are never converted or added together. A period
with expenses in two currencies shows two Actual Spend figures. The monthly trend draws one Actual
Spend chart per currency.

## The period, and the period edge rule

The page has a period control of its own: **the current month in UTC** by default, the previous and
next months, and a custom date range (a first and a last day, both included). It is separate from
the Teams list and team page controls.

**An expense counts whole in the period where its service period starts.** It is never split by
days. An invoice for 15 March to 15 April is all in March and adds nothing to April. A custom range
covering both months counts it once. The monthly trend follows the same rule.

## What the page shows

- **Overview:**
  - the four tiles and the sentence;
  - a table by account and a table by vendor, each with Actual Spend and Projected apart;
  - the projected charges, one per account and period;
  - the trend: Actual Spend per month beside Estimated Execution Cost per month. These are separate
    small charts, each on its own scale, never stacked and never one summed series. The trend covers
    at least the six months up to the period's last month, and the whole period when it is longer
    (at most 24 months).
- **Billing accounts:** list, add, edit, deactivate and reactivate accounts over the billing
  account routes, with the members linked to each. The member list is read-only; a member's account
  is set in its own settings (see [In Member settings](cost.md#in-member-settings)). There is no
  delete.
- **Expenses:** record an invoice, an adjustment or a confirmed subscription charge, and correct one
  by voiding it or replacing it. Each expense shows its history (the rows it supersedes). There is
  no edit and no delete.
- **Export CSV** (in the header): four CSV files for the period the page shows. See
  [CSV export](#csv-export).

## Who may read it, and who may write

The Spend page is a person's. Every write it makes is `HumansOnly`: adding, editing, deactivating
and reactivating a billing account, and recording, voiding and replacing an expense. A machine
principal is refused each one with the humans-only sentence, whatever it holds. Rates, a member's
cost settings and an outcome's stored value are a person's writes too.

**Agents may read it for a report, only when a person says so.** The reads are the page's
overview, the members linked to each account, the four CSV files, and the billing account and
expense lists. They need the `Spend` permit of a machine principal. A person always passes, so
their answers are unchanged.

- **How an agent gets the permit.** No key is minted or stored with it. On every request an agent
  is given it only while the tenant setting **Let agents read Actual Spend for reports**
  (`reporting.agentsMayReadSpend`, Admin → Settings → Spend) is on. An agent is a team's Manager or
  member, or a Concierge.
- **Off by default.** Only a person can turn the setting on, and turning it off refuses the
  agent's next request.
- **Who never gets it.** A person's API key and the worker key never get it.
- **What a machine principal without it sees.** "Reading Actual Spend is a person's, and an
  agent's only while a person has turned on reporting.agentsMayReadSpend in Admin > Settings >
  Spend; no credential changes that, so report what you need and ask a person."

Agents read these figures through the read-only `report` MCP tool
([cost.md](cost.md#reporting-the-report-tool)), and write a report from it with the built-in skill
`outcome-reporting` ([cost.md](cost.md#writing-a-report-the-outcome-reporting-skill)). While the
setting is off, that report carries Estimated Execution Cost only and says a person must turn the
setting on; Actual Spend is never filled in from hours or rates. The overview's `estimate` is the tenant's
Estimated Execution Cost across every team, as a person sees it. Agents can already read the same
figures across every team in `GET /api/outcomes`.

## CSV export

**Export CSV** downloads one of four files for the page's period. The period rule is the overview's:
`?from=YYYY-MM-DD&to=YYYY-MM-DD` (midnight UTC, `to` exclusive), or the current UTC month when neither
date is sent. A bad period is a 400 with one sentence. Each file is built on the server from the same
rows and figures the page reads. Nothing is priced again, converted or inferred.

### Format

- **CSV (RFC 4180).** UTF-8, comma-separated, CRLF line ends. A field holding a comma, a double quote,
  a CR or an LF is put in double quotes, with each quote doubled.
- **Header row.** Always there, even when the file has no data rows.
- **Column names.** Lower-case snake_case. They do not change: renaming or reordering one is a
  breaking change, and a golden header test holds them. A new column is only ever appended at the
  end.
- **Timestamps.** ISO-8601 UTC, `yyyy-MM-ddTHH:mm:ss.fffZ`.
- **Currencies.** ISO 4217 codes, upper case.
- **Ids.** As stored: GUIDs for accounts, expenses and outcomes, and team ids as the team has them.
- **Amounts.** Plain decimal text with no currency symbol and no thousands separator. A stored
  amount (an expense, a recurring amount) is written as stored. Actual Spend and Projected sums
  keep the stored digits. Estimates are to the cent. An estimate with nothing priced is an empty
  cell, never 0.
- **On every row.** `instance_id` (the instance's `instance.id`; on a billing account, the one it
  copied at create), `period_start` and `period_end` (the period exported), and `scope`, the last
  column (see [Across instances](#across-instances)).
- **Per currency, never converted.** No row adds two currencies together.
- **Formula injection.** A text cell whose first character is `=`, `+`, `-`, `@`, a tab or a
  carriage return is written with a single quote (`'`) before it, so a spreadsheet shows it as text
  and never runs it. A plain number (an optional minus, digits and an optional fraction, such as
  `-12.50`) is a number and is written as it is.
- **Words.** No column calls an estimate spend or cost alone (`estimated_execution_cost`, never
  `cost`). `actual_spend` is only ever confirmed current expenses, and no column names a value,
  ROI, return or savings.

### `billing-accounts`

Every billing account, active and inactive, by name.

`instance_id`, `period_start`, `period_end`, `billing_account_id`, `name`, `vendor`, `billing_type`,
`fixed_or_variable` (`fixed` for a subscription, `variable` for metered and service), `currency`,
`billing_period`, `recurring_amount`, `effective_from`, `effective_to`, `is_active`, `description`,
`created_by`, `created_at`, `updated_at`, `scope` (the account's stored scope; only `tenant` can
be written today).

### `expenses`

Every row of each correction chain that has a row whose service period starts in the period. That
includes current, voided and superseded rows, in the order they were recorded. A replacement that
moved the service period into or out of the period still brings the rest of its chain.

`instance_id`, `period_start`, `period_end`, `expense_id`, `billing_account_id`,
`billing_account_name`, `vendor`, `kind`, `status` (`confirmed` or `void`), `amount`, `currency`,
`service_period_start`, `service_period_end` (exclusive), `supersedes_expense_id`,
`superseded_by_expense_id`, `is_current`, `counts_in_actual_spend`, `reference`, `note`,
`recorded_by`, `recorded_at`, `scope` (the account's stored scope), `billing_account_instance_id`
(the account's stored `instance_id`; `instance_id` is the instance that exported the row).

`supersedes_expense_id` and `superseded_by_expense_id` rebuild the chain. `counts_in_actual_spend`
is `true` only for a current confirmed row whose service period starts in the period (the
[period edge rule](#the-period-and-the-period-edge-rule)). A voided or replaced expense is
therefore counted once, by its current row, and the rows marked `true` add up to Actual Spend.

### `period-summary`

`GET /api/spend/overview`'s figures for the same period, one row each. The route builds the same
overview object with the same code, so the figures cannot differ.

`instance_id`, `period_start`, `period_end`, `measure`, `billing_type`, `fixed_or_variable`,
`currency`, `amount`, `item_count`, `agent_seconds`, `agent_hours`, `agent_rate`, `rate_source`,
`scope` (on a spend row, the stored scopes of the accounts the row adds together - that currency's,
and that billing type's on a by-type row - `;`-separated; empty on the estimate row).

| `measure` | One row per | Filled |
|---|---|---|
| `actual_spend` | currency | `amount`, `item_count` (expenses counted) |
| `projected` | currency | `amount`, `item_count` (projected charges); never in Actual Spend |
| `actual_spend_by_billing_type` | billing type and currency | `billing_type`, `fixed_or_variable`, `amount` |
| `projected_by_billing_type` | billing type and currency | `billing_type`, `fixed_or_variable`, `amount` |
| `estimated_execution_cost` | the instance's currency (one row) | `amount` (Estimated Execution Cost, empty when nothing was priced), `agent_seconds`, `agent_hours`, `agent_rate`, `rate_source` (the rate sources priced into it, `;`-separated) |

### `estimated-execution-cost`

Estimated Execution Cost and Agent Hours by team and by outcome. Every run that overlaps the period
counts the part of its span inside it, priced on its own by the one pricing path (`ExecutionCost`).

`instance_id`, `period_start`, `period_end`, `rollup`, `team_id`, `team_name`, `outcome_id`,
`outcome_name`, `rate_source`, `currency`, `estimated_agent_cost`, `estimated_plugin_cost`,
`estimated_execution_cost`, `agent_seconds`, `agent_hours`, `agent_rate`, `scope` (always empty).

- **`rollup` `team`.** Each team the caller reaches that has runs in the period. Its runs are read as
  every team figure reads them: the ledger rows carrying its id, above the team's floor.
- **`rollup` `outcome`.** Every run in the period, under the outcome it serves, followed through
  `merged_into`, as the outcome reads attribute it. `Unattributed` (empty `outcome_id`) holds the
  runs serving none, including those whose outcome was deleted.
- **One row per team or outcome and `rate_source`** (`member`, `tenant`, `reconstructed`,
  `fallback-at-upgrade`, `none`). A team's or an outcome's rows add up to its figure.
  - The outcome rows add up, to the cent, to each outcome's and Unattributed's cost in
    `GET /api/outcomes?from=&to=` for the same window.
  - All the outcome rows together add up to the summary's estimate.
  - The team rows leave out runs with no team, and runs of teams the caller does not reach.
- **Agent and plugin apart.**
  - `estimated_agent_cost` and `estimated_plugin_cost` are the agent and plugin runs' parts.
  - `estimated_execution_cost` is both together.
  - Each is empty, never 0, when nothing in it was priced.
  - `agent_seconds` and `agent_hours` are agent time only.
  - `agent_rate` is the priced agent time's weighted rate.
- **Hours inside the period.** `agent_seconds` here, like the overview's, is the part of each run
  inside the period. A windowed `GET /api/outcomes` instead counts the whole of each run that ended
  in the window, so its `agentSeconds` can differ for a run that crosses the period's edge.
- **The weekly team reads.** These split a run that crosses a Monday into weekly parts, each
  rounded on its own. For the same span they can therefore differ from this file by a cent per run
  that crosses a Monday, as described in [cost.md](cost.md).

### Across instances

The files are ready to be put beside another instance's files. Nothing reads two instances' files
together yet; that is later work.

- **Ids that do not change.** `instance_id` is a GUID written once at the first start and never
  changed. Accounts and expenses have GUIDs the store assigns, and expenses are never edited (a
  correction is a new row). Team and outcome ids are stored ids; Unattributed is always the empty
  `outcome_id`. Two exports of the same period are the same bytes, before and after a restart.
- **No row id on a summary row.** A `period-summary` row is identified by `instance_id`,
  `period_start`, `period_end`, `measure`, `billing_type` and `currency`. An
  `estimated-execution-cost` row is identified by `instance_id`, the period, `rollup`, `team_id` or
  `outcome_id`, and `rate_source`. No file lists runs.
- **`scope` is a billing account's.** It is `tenant` on every account today; any other value is
  refused when an account is created or edited. The column leaves room for an account shared
  between instances.
- **Counting a shared account once.** An account is identified across instances by
  `billing_account_instance_id` and `billing_account_id` (on the `billing-accounts` file, by
  `instance_id` and `billing_account_id`, since there `instance_id` is the account's own). An
  expense whose `scope` is `tenant` belongs to the instance that exported it. Any other `scope` is
  counted once by `billing_account_instance_id`, `billing_account_id` and `expense_id`, however many
  instances exported it. Actual Spend across instances is added up from the `expenses` files' rows
  with `counts_in_actual_spend` `true`, never from the `period-summary` files, whose spend rows add
  every account of a currency together.
- **Estimates have no scope.** An Estimated Execution Cost is one instance's own runs priced on that
  instance. It is never shared, so `instance_id` identifies it and `scope` is empty.

`SpendExportIdentityRuleTests` holds the column order (each file's columns before `scope` are
unchanged; only the new ones are appended) and shows a shared account's expense exported by two
instances is one row by those three ids. `SpendExportIdentityTests` checks, on a real Host started
twice over one data root, that `instance_id` is the stored `instance.id`, that each account's
`scope` and instance are its stored row's, and that every file is the same after a second export
and after a restart.

## Routes

| Route | Answers |
|---|---|
| `GET /api/spend/overview?from=YYYY-MM-DD&to=YYYY-MM-DD` | The period's `actual` and `projected` (`{ currency, amount, count }` per currency), `projections`, `billingTypes` (`group` `fixed` or `variable`), `estimate` (`{ currency, cost, agentSeconds, agentRate, legacyRates }`; `cost` is null, never 0, when nothing was priced), `accounts` and `vendors` (Actual Spend and Projected apart, null when there is none), and `months` (`{ monthStart, actual, estimate }`). With neither date it uses the current UTC month. `to` is exclusive. A bad period is a 400 with one sentence. People, and agents holding `Spend`. |
| `GET /api/spend/export/billing-accounts?from=&to=` | The [`billing-accounts`](#billing-accounts) CSV file. People, and agents holding `Spend`. |
| `GET /api/spend/export/expenses?from=&to=` | The [`expenses`](#expenses) CSV file. People, and agents holding `Spend`. |
| `GET /api/spend/export/period-summary?from=&to=` | The [`period-summary`](#period-summary) CSV file. People, and agents holding `Spend`. |
| `GET /api/spend/export/estimated-execution-cost?from=&to=` | The [`estimated-execution-cost`](#estimated-execution-cost) CSV file. People, and agents holding `Spend`. |
| `GET /api/spend/linked-members` | `{ accountId, team, teamName, member, label }` for every member on a team the caller reaches whose billing account is set. People, and agents holding `Spend`. |

The billing account and expense routes are listed in [cost.md](cost.md#routes).
