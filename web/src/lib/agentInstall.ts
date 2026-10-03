import type { AgentInstall, AgentInstallation } from '../api/types'

/**
 * What the Agents screen and the ribbon badge SAY about a preset whose CLI may not be on this
 * machine - as pure functions, which is the only testable place for any of it.
 *
 * Mounted specs exist in this repository, through `web/src/test/mountQuasar.ts`, and this is
 * still the right place to test the rule. The alternative - a spec that reads the component's
 * SOURCE and asserts a string appears in it - proves only that somebody typed the string, and can
 * pass on a prop the component does not even have.
 *
 * The badge is TEXT PLUS ICON, NEVER COLOUR ALONE.
 */

/**
 * The wire value for the fourth state, and the ONE copy of it on this side.
 *
 * It is not `MissingAgent`, which means the CATALOG has no such entry and is fixed on the Agents
 * screen; this means the catalog is right and the MACHINE lacks the CLI, which is fixed in a
 * terminal by a person. It is deliberately not a `ContainerState` either - that enum crosses two
 * serialisers as a name and this file's own comparisons are why.
 */
export const AgentNotInstalled = 'AgentNotInstalled'

/**
 * The wire value for a preset whose command the platform's update holds, waiting or running. Its own
 * state, and never missing: the install is being replaced, and it is measured again when the update
 * ends. Exact match, like {@link AgentNotInstalled}.
 */
export const AgentUpdating = 'AgentUpdating'

/** Whether the platform's update holds this preset's command now. */
export function isUpdating(installation: AgentInstallation | undefined | null): boolean {
  return installation?.state === AgentUpdating
}

/**
 * WHERE an update runs, in words: the worker it runs on, "this machine" for a server that runs its
 * runs itself, or null while it waits for runs and no worker is picked yet. Never a guess.
 */
export function whereUpdating(installation: AgentInstallation): string | null {
  if (!installation.measuredOn) return 'this machine'
  return installation.updating?.phase === 'updating' ? (installation.updating.worker ?? null) : null
}

/**
 * The ribbon action the badge attaches to.
 *
 * ONE definition for both surfaces, so the desktop strip and the mobile drawer cannot mark
 * different buttons. It is a RIBBON ACTION, which the `Ribbon`
 * constant and MainLayout's handler already spell out; that is nothing like naming an Agent, which
 * belongs to whoever edits the catalog and may be relabelled at any time.
 */
export const AgentsAction = 'admin-agents'

/**
 * EXACT MATCH, deliberately. Anything that is not this value -
 * a null from a server that does not probe, an unknown state from a newer one, an absent
 * `installations` list - is NOT a claim that the CLI is missing. The mark says "this will not run
 * on this machine", so the failure direction is to not make that claim.
 */
export function isNotInstalled(installation: AgentInstallation | undefined | null): boolean {
  return installation?.state === AgentNotInstalled
}

/**
 * The probe result for one preset, matched the way the server matches a preset name.
 *
 * CASE-INSENSITIVE, because `team_members.agent`, `AgentCatalog.Definition` and the rename route
 * all fold case - a row joined case-sensitively here would silently show no state at all for a
 * preset spelled differently in two places, which reads as the probe not having run.
 */
export function installationFor(
  installations: readonly AgentInstallation[] | undefined | null,
  agent: string,
): AgentInstallation | undefined {
  return (installations ?? []).find(
    (entry) => entry.agent.toLowerCase() === agent.toLowerCase(),
  )
}

/**
 * "a", "a and b", "a, b and c" - the server's own way of listing workers in a sentence.
 */
export function listOf(names: readonly string[]): string {
  if (names.length <= 1) return names[0] ?? ''
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
}

/**
 * WHERE a preset's CLI is missing, in words: the workers that measured it missing, or "this
 * machine" for a server that answers from its own PATH.
 *
 * THE ROLE IS READ FROM THE ANSWER, never from a flag: a server whose agent CLIs are on workers
 * sends `measuredOn` (empty when nothing is measured), and one that runs its runs itself sends none.
 */
export function whereMissing(installation: AgentInstallation): string {
  const measured = installation.measuredOn
  if (!measured) return 'this machine'

  const missing = measured.filter((entry) => !entry.installed).map((entry) => entry.worker)
  return missing.length > 0 ? listOf(missing) : 'a worker'
}

/** What one row on the Agents screen shows: a word and a glyph, never a colour on its own. */
export interface InstallStatus {
  readonly text: string
  readonly icon: string
  /** For the colour, which is an ADDITION to the text and the icon and never a substitute. */
  readonly tone: 'ok' | 'warn' | 'unknown'
}

/**
 * The state for ONE ROW, and every preset gets one.
 *
 * A seeded `codex` a tenant never uses, reading "not found on this machine", is INFORMATION rather
 * than a problem - the screen lists what exists and the badge warns about what is in use. That is
 * the whole reason this function and {@link agentsBadge} are separate: one has no idea whether
 * anything references the preset and does not need one.
 *
 * `unknown` is a real answer rather than an optimistic one. A client reading a server that does not
 * probe - an answer with no `installations` at all - knows nothing, and rendering that as "found"
 * would be a claim nothing checked.
 *
 * MATERIAL SYMBOLS, NOT MATERIAL ICONS. The app loads Symbols Outlined only; an Icons-only name
 * renders as its own letters at the wrong width and throws the row it sits in out of line, with
 * nothing logged. `error_outline` and `help_outline` are Icons-only; `check_circle`, `error` and
 * `help` are in Symbols.
 */
export function installStatus(
  installation: AgentInstallation | undefined | null,
): InstallStatus {
  if (!installation) {
    return { text: 'Not checked', icon: 'help', tone: 'unknown' }
  }

  // UPDATING COMES FIRST: the install is being replaced, so it is never "not installed" or "found".
  if (isUpdating(installation)) {
    const where = whereUpdating(installation)
    return {
      text: installation.updating?.phase === 'waiting'
        ? 'Updating: waiting for runs to finish'
        : where ? `Updating on ${where}` : 'Updating',
      icon: 'sync',
      tone: 'unknown',
    }
  }

  const measured = installation.measuredOn

  if (!measured) {
    return isNotInstalled(installation)
      ? { text: 'Not found on this machine', icon: 'error', tone: 'warn' }
      : { text: 'Found on PATH', icon: 'check_circle', tone: 'ok' }
  }

  // MEASURED ON WORKERS. Nothing measured is a real answer and never "found": no worker that runs
  // agents has said, so no claim is made either way. Two workers that disagree read as missing on
  // the one that lacks it; the server's sentence beside the row names both.
  if (isNotInstalled(installation)) {
    return { text: `Not installed on ${whereMissing(installation)}`, icon: 'error', tone: 'warn' }
  }

  return measured.length === 0
    ? { text: 'Not measured', icon: 'help', tone: 'unknown' }
    : {
        text: `Installed on ${listOf(measured.map((entry) => entry.worker))}`,
        icon: 'check_circle',
        tone: 'ok',
      }
}

/** What a person is told, and where - if anywhere - they can go about it. */
export interface InstallGuidance {
  readonly text: string
  /** The preset's OWN `install.url`, or null. NEVER composed, completed or corrected. */
  readonly url: string | null
  readonly hint: string | null
}

/**
 * The sentence, plus the link when the preset carries one.
 *
 * ABSENT DEGRADES TO TEXT, and nothing is constructed to fill the gap. A table here mapping
 * `codex` to a vendor's documentation would work everywhere immediately and is precisely the
 * change this refuses: nothing in code may name an Agent, so a name here is a reference nothing
 * wrote down - no check sees it, no rename moves it, and a vendor moving its documentation leaves a
 * dead link only a rebuild can fix.
 *
 * A BLANK URL IS AN ABSENT ONE, on this side as well as the server's. `install` arrives from a file
 * an operator may edit by hand, and an anchor wrapped around an empty string is a link to nowhere -
 * the guessed-URL failure arriving by the back door.
 */
export function installGuidance(
  installation: AgentInstallation | undefined | null,
): InstallGuidance {
  const install: AgentInstall | null | undefined = installation?.install
  const url = install?.url?.trim()
  const hint = install?.hint?.trim()

  return {
    text: installation?.message ?? '',
    url: url ? url : null,
    hint: hint ? hint : null,
  }
}

/**
 * What the badge counts: presets that are BOTH missing and in use.
 *
 * A tenant with eight seeded presets and two CLIs installed would otherwise carry a permanent badge
 * of six, and a badge that is always lit is wallpaper - it stops being read, and the one time it
 * matters nobody notices. `referenced` is the server's answer to "is any team on this", across all
 * three routes a team can be: a member's `agent`, a team's `member_agents` allowlist, and a team's
 * `interactive_agent`.
 */
export function referencedNotInstalled(
  installations: readonly AgentInstallation[] | undefined | null,
): AgentInstallation[] {
  return (installations ?? []).filter((entry) => entry.referenced && isNotInstalled(entry))
}

/** The ribbon mark. TEXT PLUS ICON - `text` is what is drawn, `label` is what it means. */
export interface AgentsBadge {
  readonly count: number
  /** Drawn in the badge. A NUMBER AS TEXT, so the mark survives being read without colour. */
  readonly text: string
  readonly icon: string
  /** The accessible name and the tooltip - the same words, because they answer the same question. */
  readonly label: string
}

/**
 * The badge, or NULL when there is nothing to say.
 *
 * NULL RATHER THAN A ZERO BADGE. The quiet case is the one that has to be right: on a machine with
 * every CLI installed the mark must be ABSENT, or it is noise from the first day and nobody reads
 * it on the day it matters.
 *
 * The words name the REMEDY's location rather than the count alone - "not installed on this
 * machine", or on the named worker, is what sends somebody to a terminal, where "3" sends them to
 * the Agents screen to fix a catalog that is already correct. A preset nothing has measured is
 * never counted: its state is null.
 */
export function agentsBadge(
  installations: readonly AgentInstallation[] | undefined | null,
): AgentsBadge | null {
  const missing = referencedNotInstalled(installations)

  // BEING UPDATED IS NOT MISSING. In use and held by the platform's update, a preset is said in words
  // - its runs wait - and never counted, so nobody is sent to install a CLI that is there.
  const updating = (installations ?? []).filter((entry) => entry.referenced && isUpdating(entry))
  const updates = updating.map((entry) => {
    const where = whereUpdating(entry)
    return where
      ? `${entry.agent} is being updated on ${where}; its runs wait for it.`
      : `${entry.agent} is being updated once its runs finish; new runs wait for it.`
  })

  if (missing.length === 0) {
    return updates.length === 0
      ? null
      : { count: 0, text: '↻', icon: 'sync', label: updates.join(' ') }
  }

  // Grouped by WHERE, in the order first met: one place reads as before; several name each.
  const groups = new Map<string, string[]>()
  for (const entry of missing) {
    const where = whereMissing(entry)
    groups.set(where, [...(groups.get(where) ?? []), entry.agent])
  }

  const clauses = [...groups].map(([where, agents]) =>
    agents.length === 1
      ? `${agents[0]} is not installed on ${where}`
      : `${agents.join(', ')} are not installed on ${where}`,
  )

  return {
    count: missing.length,
    text: String(missing.length),
    icon: 'error',
    label: [
      missing.length === 1
        ? `${clauses[0]}, and a team uses it.`
        : `${clauses.join(' and ')}, and teams use them.`,
      ...updates,
    ].join(' '),
  }
}
