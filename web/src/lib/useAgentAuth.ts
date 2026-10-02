import { ref } from 'vue'
import { getAgentAuth } from '../api/client'
import type { AgentAuthReport } from '../api/types'

/**
 * Whether each preset's CLI is signed in, as `GET /api/agents/auth` last measured it - held ONCE,
 * for every surface that reads it.
 *
 * MODULE SCOPE, for the reason `useAgentInstallations` is: one store of the fact, several readers.
 * The Agents screen refreshes it on open, so signing a CLI in inside the container and reopening
 * the screen shows the change without a restart.
 *
 * A LIST BESIDE THE CATALOG, never merged into an `Agent`. `PUT /api/agents` replaces the catalog
 * wholesale from what the Agents screen holds, so anything folded into a definition is something
 * the next save writes into `agents.json`. This is a measurement of the machine, not configuration.
 */
const reports = ref<AgentAuthReport[]>([])

/**
 * Re-reads the probe.
 *
 * A FAILURE EMPTIES THE LIST RATHER THAN THROWING. This runs from a dialog opening, which has
 * nowhere to report to, and an empty list renders every row as "Not measured" - which is true, and
 * is the safe direction: the alternative is a stale "Authenticated" on a CLI that has since signed
 * out.
 */
export async function refreshAgentAuth(): Promise<void> {
  try {
    reports.value = await getAgentAuth()
  } catch {
    reports.value = []
  }
}

/** The report for one preset, matched on name without regard to case, or undefined. */
export function authReportFor(
  list: readonly AgentAuthReport[],
  agent: string,
): AgentAuthReport | undefined {
  const wanted = agent.toLowerCase()

  return list.find((report) => report.agent.toLowerCase() === wanted)
}

/** What a row says about sign-in, and how it is toned. Text plus icon, never colour alone. */
export interface AuthStatus {
  readonly text: string
  readonly icon: string
  /** `ok` is green, `warn` is amber, `unknown` is grey and is NEVER styled as a failure. */
  readonly tone: 'ok' | 'warn' | 'unknown'
  /** The probe's own sentence, shown only when the answer is no. */
  readonly detail: string | null
}

/**
 * The third caption on an Agents row.
 *
 * `null` IS "NOT MEASURED", NOT "NO". A CLI with no way to ask, an uninstalled command, and a
 * probe that did not run all land here, and every one of them is a state in which the platform
 * has no idea whether a launch would work - so the row must not say it will not. Rendering null
 * as a failure would light up every preset the probe cannot reach and teach people to ignore the
 * one that is real.
 *
 * MATERIAL SYMBOLS, NOT MATERIAL ICONS, for the reason `installStatus` gives: the app loads
 * Symbols Outlined only, and an Icons-only name renders as its own letters at the wrong width.
 */
export function authStatus(report: AgentAuthReport | undefined | null): AuthStatus {
  // NOT MEASURED BECAUSE NO WORKER ANSWERED: whether the CLI is even installed is not known, and the
  // probe's sentence says why. Never "not installed": nothing looked.
  if (report && report.installed === null) {
    return { text: 'Not measured', icon: 'help', tone: 'unknown', detail: report.detail || null }
  }

  if (!report || !report.installed || report.authenticated === null) {
    return { text: 'Not measured', icon: 'help', tone: 'unknown', detail: null }
  }

  return report.authenticated
    ? { text: 'Authenticated', icon: 'verified_user', tone: 'ok', detail: null }
    : { text: 'Not authenticated', icon: 'no_accounts', tone: 'warn', detail: report.detail || null }
}

/**
 * The lines the status strip's red banner shows: one per preset that is IN USE, installed, and
 * measured as signed out.
 *
 * IN USE IS THE WHOLE POINT. Warning about every installed CLI would let a seeded `codex`
 * that no team runs and nobody ever signed in light the banner for every person on every page. A
 * banner that is always lit stops being read. `authenticated === null` is not measured and never
 * a warning - see `authStatus`.
 */
export function authProblems(list: readonly AgentAuthReport[]): string[] {
  return list
    .filter((report) => report.referenced && report.installed && report.authenticated === false)
    .map((report) => `${report.agent}: ${report.detail}`)
}

/** The shared list. */
export function useAgentAuth() {
  return { reports }
}

/**
 * How the probe says the preset signs in, in words: "shared home" or "issued credential". Null when
 * the server did not say - an older Host - which is never read as home.
 */
export function sourceLabel(report: AgentAuthReport | undefined | null): string | null {
  switch (report?.source) {
    case 'home':
      return 'shared home'
    case 'issued':
      return 'issued credential'
    default:
      return null
  }
}
