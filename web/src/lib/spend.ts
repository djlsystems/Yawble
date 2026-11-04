import type { CurrencyAmount, Expense, SpendEstimate, SpendExportFile } from '../api/spend'
import { EstimateTitle, hoursFigure } from './teamCost'

/**
 * THE SPEND PAGE'S WORDS AND ITS SMALL RULES. Three measurements, never mixed:
 *
 * - **Actual Spend** is confirmed vendor expenses only, per currency.
 * - **Projected** is recurring charges not yet invoiced, labelled projected, never in Actual Spend.
 * - **Estimated Execution Cost** is agent and plugin time at estimated hourly rates, beside **Agent Hours**.
 *
 * Currencies are never converted or added together, and Actual Spend is never inferred from hours.
 */

export const ActualSpendTitle = 'Actual Spend'
export const ProjectedTitle = 'Projected'
export const FixedVariableTitle = 'Fixed subscriptions vs variable'
export const EstimateTileTitle = `${EstimateTitle} · Agent Hours`

/** Shown wherever Actual Spend and Estimated Execution Cost appear together, exactly. */
export const SpendSentence =
  'Actual Spend is what you paid vendors. Estimated Execution Cost is agent and plugin time at your estimated hourly rates. They are different measurements and need not reconcile.'

/** Actual Spend with no confirmed expense in the period: said, never filled with an estimate. */
export const NoActualSpend = 'No confirmed expenses in this period'
export const NoProjected = 'No recurring charges projected for this period'
export const NoEstimate = 'Estimate unavailable: no priced agent or plugin time in this period'

/** The period edge rule, as the page states it. */
export const EdgeRule =
  'An expense counts whole in the period where its service period starts; it is never split by days.'

/** The Export control: one CSV file each, for the page's period. */
export const ExportTitle = 'Export CSV'
export const ExportFiles: readonly { file: SpendExportFile; label: string; caption: string }[] = [
  { file: 'billing-accounts', label: 'Billing accounts', caption: 'Every account, active and inactive' },
  { file: 'expenses', label: 'Expenses', caption: 'Every row of the period, with its voids and replacements' },
  { file: 'period-summary', label: 'Period summary', caption: 'Actual Spend, Projected and Estimated Execution Cost, per currency' },
  { file: 'estimated-execution-cost', label: `${EstimateTitle} by team and outcome`, caption: 'Agent Hours and rate source on every row' },
]

export const BillingTypeWords: Record<string, string> = {
  subscription: 'Subscription (fixed)',
  metered: 'Metered (variable)',
  service: 'Service (variable)',
}

export const KindWords: Record<string, string> = {
  invoice: 'Invoice',
  'subscription-charge': 'Confirmed subscription charge',
  adjustment: 'Adjustment',
}

/** An exact amount in its own currency, always to the cent: an expense is never rounded for show. */
export function amountText(amount: number | string, currency: string): string {
  const value = typeof amount === 'string' ? Number(amount) : amount
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency, minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)
  } catch {
    return `${value.toFixed(2)} ${currency}`
  }
}

/** One line per currency, by code; never one sum. */
export function perCurrency(amounts: readonly CurrencyAmount[]): string[] {
  return [...amounts].sort((a, b) => a.currency.localeCompare(b.currency)).map((a) => amountText(a.amount, a.currency))
}

/** The estimate's money, or the unavailable words - never 0 for "nothing priced". */
export function estimateText(estimate: SpendEstimate): string {
  return estimate.cost === null ? 'Estimate unavailable' : amountText(estimate.cost, estimate.currency)
}

export function agentHoursText(seconds: number): string {
  return `${hoursFigure(seconds)} Agent Hours`
}

function iso(date: Date): string {
  return date.toISOString().slice(0, 10)
}

/** The current UTC month as `[from, to)` dates: the page's default period. */
export function currentMonth(now: Date = new Date()): { from: string; to: string } {
  const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1))
  const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1))
  return { from: iso(from), to: iso(to) }
}

/** The month starting `from` (a `YYYY-MM-DD`), moved by `step` months. */
export function shiftMonth(from: string, step: number): { from: string; to: string } {
  const [year, month] = from.split('-').map(Number)
  const start = new Date(Date.UTC(year!, month! - 1 + step, 1))
  return currentMonth(start)
}

/** A custom range as a person picks it, the last day INCLUSIVE, turned into the route's exclusive `to`. */
export function rangeOf(first: string, last: string): { from: string; to: string } | null {
  const pattern = /^\d{4}-\d{2}-\d{2}$/
  if (!pattern.test(first) || !pattern.test(last)) return null
  const end = new Date(`${last}T00:00:00Z`)
  if (Number.isNaN(end.getTime()) || Number.isNaN(new Date(`${first}T00:00:00Z`).getTime())) return null
  end.setUTCDate(end.getUTCDate() + 1)
  const to = iso(end)
  return to > first ? { from: first, to } : null
}

/** How the header names a period: a whole month by its name, any other range by its first and last day. */
export function periodLabel(period: { from: string; to: string }): string {
  const month = currentMonth(new Date(`${period.from}T00:00:00Z`))
  if (month.from === period.from && month.to === period.to) {
    return new Date(`${period.from}T00:00:00Z`).toLocaleDateString(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' })
  }
  const last = new Date(`${period.to}T00:00:00Z`)
  last.setUTCDate(last.getUTCDate() - 1)
  return `${dayText(period.from)} – ${dayText(iso(last))}`
}

export function dayText(stamp: string): string {
  return new Date(stamp.length === 10 ? `${stamp}T00:00:00Z` : stamp).toLocaleDateString(undefined, {
    day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC',
  })
}

/** An exclusive service period end as the last day it covers. */
export function servicePeriodText(start: string, end: string): string {
  const last = new Date(end)
  last.setUTCDate(last.getUTCDate() - 1)
  return `${dayText(start)} – ${dayText(last.toISOString())}`
}

export function monthLabel(monthStart: string): string {
  return new Date(`${monthStart}T00:00:00Z`).toLocaleDateString(undefined, { month: 'short', year: '2-digit', timeZone: 'UTC' })
}

/**
 * EACH EXPENSE'S HISTORY: the current rows, newest recorded first, each with the rows it supersedes
 * behind it, oldest last. A void is a current row too (its status says so).
 */
export function expenseChains(rows: readonly Expense[]): { current: Expense; history: Expense[] }[] {
  const byId = new Map(rows.map((r) => [r.id, r]))
  return rows
    .filter((r) => r.current)
    .sort((a, b) => b.recordedAt.localeCompare(a.recordedAt) || b.id.localeCompare(a.id))
    .map((current) => {
      const history: Expense[] = []
      let at = current.supersedes ? byId.get(current.supersedes) : undefined
      while (at) {
        history.push(at)
        at = at.supersedes ? byId.get(at.supersedes) : undefined
      }
      return { current, history }
    })
}

/** Whether a row can still be corrected: current and confirmed. */
export function correctable(row: Expense): boolean {
  return row.current && row.status === 'confirmed'
}
