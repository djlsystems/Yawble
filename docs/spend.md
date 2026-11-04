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
  is set in its own settings. There is no delete.
- **Expenses:** record an invoice, an adjustment or a confirmed subscription charge, and correct one
  by voiding it or replacing it. Each expense shows its history (the rows it supersedes). There is
  no edit and no delete.

## People only

The Spend page is a person's. Every route it reads or writes is `HumansOnly`, including its own two
reads. A machine principal is refused with the humans-only sentence, even one holding every permit:
an agent, a member key, a Manager, a Concierge acting as an agent, or a person's API key. There is no
Spend permit and no MCP tool for it yet; an agent may get a read permit for reporting later, but not
before then.

## Routes

| Route | Answers |
|---|---|
| `GET /api/spend/overview?from=YYYY-MM-DD&to=YYYY-MM-DD` | The period's `actual` and `projected` (`{ currency, amount, count }` per currency), `projections`, `billingTypes` (`group` `fixed` or `variable`), `estimate` (`{ currency, cost, agentSeconds, agentRate, legacyRates }`; `cost` is null, never 0, when nothing was priced), `accounts` and `vendors` (Actual Spend and Projected apart, null when there is none), and `months` (`{ monthStart, actual, estimate }`). With neither date it uses the current UTC month. `to` is exclusive. A bad period is a 400 with one sentence. People only. |
| `GET /api/spend/linked-members` | `{ accountId, team, teamName, member, label }` for every member on a team the caller reaches whose billing account is set. People only. |

The billing account and expense routes are listed in [cost.md](cost.md#routes).
