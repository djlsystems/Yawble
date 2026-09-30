import type { AgentUpdateResult, CliVersion } from '../api/types'

/**
 * What an Agents row says about its CLI's version, from `cliVersions` on `GET /api/agents` - the
 * Host's CLI version record, which the operator CLI's `doctor` and `agents` read too.
 *
 * NOTHING IS ESTIMATED. A version the record does not have reads "version not known"; a record that
 * never saw the version change says how far back it looked rather than inventing an update time.
 */

/** The entry for one preset, matched without regard to case, or undefined. */
export function cliVersionFor(
  list: readonly (CliVersion & { agent: string })[],
  agent: string,
): CliVersion | undefined {
  const wanted = agent.toLowerCase()

  return list.find((entry) => entry.agent.toLowerCase() === wanted)
}

/** Every entry for `cli` replaced by `now`, each keeping its preset: presets that launch the same
 *  command share one install, so an update of one is an update of them all. */
export function withCliVersion<T extends CliVersion & { agent: string }>(
  list: readonly T[],
  now: CliVersion,
): T[] {
  return list.map((entry) => (entry.cli === now.cli ? { ...entry, ...now, agent: entry.agent } : entry))
}

export function stamp(iso: string): string {
  const parsed = Date.parse(iso)
  if (Number.isNaN(parsed)) return iso

  return new Date(parsed).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}

/** Who brought the version: a container start, or a person (named when the record has them). */
function whoFor(entry: CliVersion): string {
  if (entry.updatedBy === 'person') return entry.person ? `by ${entry.person}` : 'by a person'

  return 'at a container start'
}

export interface VersionLine {
  /** The version, or null when it is not known. */
  readonly version: string | null
  /** When and by whom it last changed, or how far back the record looked. Null when not known. */
  readonly updated: string | null
}

export function versionLine(entry: CliVersion | undefined, format: (iso: string) => string = stamp): VersionLine {
  if (!entry?.version) return { version: null, updated: null }

  if (entry.updatedAt) return { version: entry.version, updated: `updated ${format(entry.updatedAt)} ${whoFor(entry)}` }

  return {
    version: entry.version,
    updated: entry.since ? `no update recorded since ${format(entry.since)}` : null,
  }
}

/** What happened to the version between the reads before and after an update, as measured. */
function versionMove(before: string | null, after: string | null): string {
  if (after === null) return 'the version is not known'
  if (before === after) return `the version stayed at ${after}`
  if (before === null) return `the version is now ${after} (not known before)`

  return `the version moved from ${before} to ${after}`
}

/**
 * What the row says once its update button has finished. It says ONLY WHAT WAS MEASURED: whether a
 * command ran, whether it succeeded, and the versions read before and after - a failed command can
 * still have moved the version, so the versions are compared, never assumed.
 */
export function updateOutcome(result: AgentUpdateResult, format: (iso: string) => string = stamp): string {
  const at = format(result.at)

  // No exit code: nothing ran, because the preset declares no update command.
  if (result.exitCode === null) return `Nothing was run at ${at}: this preset declares no update command.`

  if (!result.updated) {
    return `The update command failed at ${at} (exit ${result.exitCode}); ${versionMove(result.versionBefore, result.versionAfter)}.`
  }

  if (result.versionAfter === null) return `The update command ran at ${at}; the version is not known.`

  if (result.versionBefore === result.versionAfter) {
    return `Checked for an update at ${at}: already the newest, the version did not change.`
  }

  return `Updated at ${at}: ${result.versionBefore ?? 'version not known'} → ${result.versionAfter}.`
}
