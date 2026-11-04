import { json } from './client'

/**
 * EVERY CALL THE SPEND PAGE MAKES: the billing accounts (S1's routes), the expenses (S2's routes), and
 * the page's two reads. Every route is a person's only; a machine principal is refused each.
 *
 * | operation | route |
 * |---|---|
 * | overview for a period | `GET /api/spend/overview?from=&to=` |
 * | members linked to accounts | `GET /api/spend/linked-members` |
 * | list, create, edit accounts | `GET/POST /api/billing-accounts`, `POST /api/billing-accounts/{id}` |
 * | deactivate, reactivate | `POST /api/billing-accounts/{id}/deactivate`, `/reactivate` |
 * | list, record expenses | `GET/POST /api/expenses` |
 * | void, replace | `POST /api/expenses/{id}/void`, `/replace` |
 *
 * EVERY FIGURE IS THE ROUTE'S. Nothing here converts a currency, adds two currencies, or puts an
 * estimate where Actual Spend has none.
 */

export type BillingType = 'subscription' | 'metered' | 'service'
export type BillingPeriod = 'monthly' | 'annual'
export type ExpenseKind = 'invoice' | 'subscription-charge' | 'adjustment'

export interface BillingAccount {
  id: string
  name: string
  vendor: string
  billingType: BillingType
  currency: string
  billingPeriod: BillingPeriod
  recurringAmount: string | null
  effectiveFrom: string
  effectiveTo: string | null
  active: boolean
  description: string | null
  scope: string
  instanceId: string
  createdBy: string
  createdAt: string
  updatedAt: string
}

/** What a create or an edit sends. A fixed field (`id`, `scope`, `instanceId`, ...) is never sent. */
export interface BillingAccountFields {
  name?: string
  vendor?: string
  billingType?: BillingType
  currency?: string
  billingPeriod?: BillingPeriod
  recurringAmount?: string | null
  effectiveFrom?: string
  effectiveTo?: string | null
  description?: string | null
}

export interface Expense {
  id: string
  accountId: string
  amount: string
  currency: string
  servicePeriodStart: string
  servicePeriodEnd: string
  kind: ExpenseKind
  status: 'confirmed' | 'void'
  supersedes: string | null
  reference: string | null
  note: string | null
  recordedBy: string
  recordedAt: string
  supersededBy: string | null
  current: boolean
}

export interface ExpenseFields {
  accountId?: string
  amount?: string
  currency?: string
  servicePeriodStart?: string
  servicePeriodEnd?: string
  kind?: ExpenseKind
  reference?: string | null
  note?: string | null
}

export interface CurrencyAmount {
  currency: string
  amount: number
  count: number
}

export interface SpendEstimate {
  currency: string
  /** Null - never 0 - when nothing in the period was priced. */
  cost: number | null
  agentSeconds: number
  agentRate: number | null
  legacyRates: string[]
}

export interface SpendTypeAmount {
  billingType: BillingType
  group: 'fixed' | 'variable'
  currency: string
  actual: number | null
  projected: number | null
}

export interface SpendAccountLine {
  accountId: string
  name: string
  vendor: string
  billingType: BillingType
  currency: string
  active: boolean
  actual: number | null
  projected: number | null
  expenses: number
}

export interface SpendVendorLine {
  vendor: string
  currency: string
  actual: number | null
  projected: number | null
  accounts: number
}

export interface SpendProjection {
  accountId: string
  accountName: string
  periodStart: string
  periodEnd: string
  amount: number
  currency: string
}

export interface SpendMonth {
  monthStart: string
  actual: CurrencyAmount[]
  estimate: SpendEstimate
}

export interface SpendOverview {
  from: string
  to: string
  actual: CurrencyAmount[]
  projected: CurrencyAmount[]
  projections: SpendProjection[]
  billingTypes: SpendTypeAmount[]
  estimate: SpendEstimate
  accounts: SpendAccountLine[]
  vendors: SpendVendorLine[]
  months: SpendMonth[]
}

export interface LinkedMember {
  accountId: string
  team: string
  teamName: string
  member: string
  label: string
}

const body = (value: unknown): RequestInit => ({
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(value),
})

const account = (id: string) => `/api/billing-accounts/${encodeURIComponent(id)}`
const expense = (id: string) => `/api/expenses/${encodeURIComponent(id)}`

/** The overview for `[from, to)`, two `YYYY-MM-DD` dates; the current UTC month without them. */
export function getSpendOverview(period?: { from: string; to: string }) {
  const query = period ? `?from=${encodeURIComponent(period.from)}&to=${encodeURIComponent(period.to)}` : ''
  return json<SpendOverview>(`/api/spend/overview${query}`)
}

export const getLinkedMembers = () => json<LinkedMember[]>('/api/spend/linked-members')

export const listBillingAccounts = () => json<BillingAccount[]>('/api/billing-accounts')

export const createBillingAccount = (fields: BillingAccountFields) =>
  json<BillingAccount>('/api/billing-accounts', { method: 'POST', ...body(fields) })

export const editBillingAccount = (id: string, fields: BillingAccountFields) =>
  json<BillingAccount>(account(id), { method: 'POST', ...body(fields) })

export const setBillingAccountActive = (id: string, active: boolean) =>
  json<BillingAccount>(`${account(id)}/${active ? 'reactivate' : 'deactivate'}`, { method: 'POST' })

export const listExpenses = () => json<Expense[]>('/api/expenses')

export const recordExpense = (fields: ExpenseFields) =>
  json<Expense>('/api/expenses', { method: 'POST', ...body(fields) })

export const voidExpense = (id: string, note: string | null) =>
  json<Expense>(`${expense(id)}/void`, { method: 'POST', ...body({ note }) })

export const replaceExpense = (id: string, fields: ExpenseFields) =>
  json<Expense>(`${expense(id)}/replace`, { method: 'POST', ...body(fields) })
