import type { BillingAccount } from '../api/spend'
import type { MemberEffectiveRate, TenantSetting } from '../api/types'

/**
 * A MEMBER'S COST SECTION in Member settings: its estimated hourly rate, its billing account and its
 * effective configuration. Every figure is one the Host answered - the member read's `hourlyRate`,
 * `billingAccountId` and `effectiveRate`, the billing accounts read, the tenant settings read - and
 * nothing here prices a run: what a run is snapshotted at is `effectiveRate`, as the server decided it.
 */

/** Said under the section, word for word. */
export const NextRunSentence = 'Changes apply to the next run; past runs keep the rate they started with.'

/** The model a run used cannot be told from a preset (a launch argument, never recorded). */
export const ModelNotIdentifiable = 'not identifiable'

export const RateLabel = 'Estimated hourly rate'
export const DefaultRateLabel = 'Default estimated hourly rate'
export const AccountLabel = 'Billing account'
export const NoAccountLabel = 'No account'

/** Whether the member inherits the default or holds an override. */
export type RateMode = 'inherit' | 'override'

/** An amount an hour as the Host wrote it: canonical text, never re-rounded. `62.5 USD/h`. */
export const perHour = (amount: string, currency: string) => `${amount} ${currency}/h`

/** The tenant default as Admin → Outcomes stored it: a row of `outcomes.agentHourlyRate` above 0, in
 *  `outcomes.currency`. A default nobody stored is not set - the snapshot writer reads stored rows only. */
export interface TenantDefault {
  amount: string | null
  currency: string
}

export function tenantDefaultOf(settings: readonly TenantSetting[]): TenantDefault {
  const named = (name: string) => settings.find((s) => s.name === name)
  const rate = named('outcomes.agentHourlyRate')
  const currency = String(named('outcomes.currency')?.value ?? 'usd').toUpperCase()
  const value = rate?.source === 'row' ? Number(rate.value) : NaN
  return { amount: Number.isFinite(value) && value > 0 ? String(rate!.value) : null, currency }
}

/**
 * The Inherit choice's words. A plugin member has no default: with no rate of its own it is not priced,
 * and never takes the agent default. An agent member inheriting now reads the Host's own effective rate;
 * one holding an override reads the stored tenant default.
 */
export function inheritLabel(
  kind: 'agent' | 'plugin',
  stored: { hourlyRate: string | null; effectiveRate: MemberEffectiveRate } | null,
  tenant: TenantDefault | null,
): string {
  if (kind === 'plugin') return 'Inherit default (not priced: a plugin member has no default)'
  if (stored && stored.hourlyRate === null) {
    const e = stored.effectiveRate
    return e.source === 'tenant' && e.amount !== null && e.currency !== null
      ? `Inherit default (${perHour(e.amount, e.currency)})`
      : 'Inherit default (not set: not priced)'
  }
  if (!tenant) return 'Inherit default'
  return tenant.amount === null ? 'Inherit default (not set: not priced)' : `Inherit default (${perHour(tenant.amount, tenant.currency)})`
}

/** The effective rate in words, exactly as the member read answered it. */
export function effectiveRateText(effective: MemberEffectiveRate): string {
  return effective.amount !== null && effective.currency !== null ? perHour(effective.amount, effective.currency) : 'Not priced'
}

/** Where the effective rate comes from, in words. */
export function rateSourceText(source: MemberEffectiveRate['source']): string {
  switch (source) {
    case 'member': return 'member override'
    case 'tenant': return 'tenant default'
    default: return 'none'
  }
}

/** One choice of the Billing account picker. `current` marks a held account that is not a new choice. */
export interface AccountOption {
  label: string
  value: string
  caption?: string
  current?: boolean
}

const accountLabel = (a: BillingAccount) => `${a.name} · ${a.vendor} · ${a.currency}`

/**
 * THE PICKER: No account, then each ACTIVE account. An inactive account is listed only when the member
 * already holds it, as its current value and labelled inactive; a held id that names no account (stored
 * before accounts existed) is listed as unknown, so it is kept unless the person picks something else.
 */
export function accountOptions(accounts: readonly BillingAccount[], heldId: string | null): AccountOption[] {
  const options: AccountOption[] = [{ label: NoAccountLabel, value: '' }]
  const held = heldId ? accounts.find((a) => a.id === heldId) : undefined

  if (heldId && !held) options.push({ label: `Unknown account (${heldId})`, value: heldId, caption: 'No billing account has this id; kept unless you change it', current: true })
  if (held && !held.active) options.push({ label: `${accountLabel(held)} (inactive)`, value: held.id, caption: 'Current account; inactive', current: true })

  for (const a of accounts) if (a.active) options.push({ label: accountLabel(a), value: a.id })
  return options
}

/** The held account in words, for the effective configuration. */
export function accountText(accounts: readonly BillingAccount[] | null, heldId: string | null): string {
  if (!heldId) return NoAccountLabel
  if (accounts === null) return heldId
  const held = accounts.find((a) => a.id === heldId)
  if (!held) return `Unknown account (${heldId})`
  return held.active ? accountLabel(held) : `${accountLabel(held)} (inactive)`
}

/** What the section holds while the person edits it. `account` is '' for No account. */
export interface CostDraft {
  mode: RateMode
  rate: string
  account: string
}

export function draftOf(stored: { hourlyRate: string | null; billingAccountId: string | null }): CostDraft {
  return {
    mode: stored.hourlyRate === null ? 'inherit' : 'override',
    rate: stored.hourlyRate ?? '',
    account: stored.billingAccountId ?? '',
  }
}

/** The PATCH fields a save sends: each ONLY when the person changed it, so a save that changes the
 *  rate never touches the account (absent keeps the stored value) and the other way round. Inherit,
 *  or an override left blank, clears the rate (null). */
export interface CostPatch {
  hourlyRate?: string | null
  billingAccountId?: string | null
}

export function costPatch(stored: { hourlyRate: string | null; billingAccountId: string | null }, draft: CostDraft): CostPatch {
  const patch: CostPatch = {}
  const rate = draft.mode === 'inherit' || draft.rate.trim() === '' ? null : draft.rate.trim()
  if (rate !== stored.hourlyRate) patch.hourlyRate = rate
  const account = draft.account === '' ? null : draft.account
  if (account !== (stored.billingAccountId ?? null)) patch.billingAccountId = account
  return patch
}
