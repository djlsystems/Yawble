import type { TenantRoot, TenantSetting, TenantSettings } from '../api/types'

/**
 * The Tenant Settings dialog's rules, out of the SFC so a spec can reach them.
 *
 * THE KEYS STAY HERE. A setting's wire name (`wip.maxRunning`) is how the server and this file talk;
 * a person reads the LABEL. Nothing in this file puts a key on screen, and the dialog renders only
 * `label`, `hint` and the source line.
 */

export type TenantSettingTab = 'admission' | 'spend' | 'concierge' | 'sweeps' | 'kanban' | 'system'

/**
 * How a field is typed and checked. `lanes` is `kanban.wipLimits`, edited one lane at a time;
 * `packages` is a list of OS package names typed into one box, separated by spaces or commas.
 * `tagMap` is `agents.tags`, preset name to its list of tags, and `sourceMap` is
 * `agents.credentialSource`, preset name to `home` or `issued`. Both are edited from the Agents
 * screen rather than this dialog, which lists neither.
 */
export type TenantSettingKind = 'count' | 'duration' | 'theme' | 'lanes' | 'packages' | 'tagMap' | 'sourceMap'

export interface TenantSettingField {
  name: string
  tab: TenantSettingTab
  kind: TenantSettingKind
  label: string
  hint: string
  /** The largest count the server accepts. Counts are never below 0. */
  max?: number
}

export const WipMaxRunning = 'wip.maxRunning'
export const KanbanWipLimits = 'kanban.wipLimits'
export const SystemPackages = 'system.packages'
export const AgentTags = 'agents.tags'
export const AgentCredentialSources = 'agents.credentialSource'

/** This sentence is put in front of a person, word for word. */
export const SystemPackagesRestartSentence = 'Adding one costs a restart, not an image rebuild.'

/** The board's running lane - its limit IS `wip.maxRunning`, not an advisory one. */
export const InProgressLaneId = 'in-progress'

/**
 * Every lane id that holds RUNNING work in some template. The server refuses a `kanban.wipLimits`
 * entry for any of them, because their limit is the ledger's.
 */
export const RunningLaneIds: readonly string[] = [InProgressLaneId, 'doing', 'investigating', 'in-flight']

export const isRunningLane = (laneId: string) => RunningLaneIds.includes(laneId)

/**
 * The tenant settings the dialog edits, in the order it shows them.
 *
 * THE ADMISSION WORDING IS DECIDED, NOT DRAFTED. Change both strings
 * only as a deliberate product decision.
 */
export const TenantSettingFields: readonly TenantSettingField[] = [
  {
    name: WipMaxRunning,
    tab: 'admission',
    kind: 'count',
    label: 'Agents running at once',
    hint: 'Across all teams, Managers included. Work over this number waits its turn; nothing is refused. 0 means no limit.',
    max: 1000,
  },
  // NO HINT: these two show the server's description, the one place their wording lives.
  {
    name: 'wip.memoryPerRunMb',
    tab: 'admission',
    kind: 'count',
    label: 'Memory counted per run (MB)',
    hint: '',
    max: 1_048_576,
  },
  {
    name: 'runs.memoryLimitMb',
    tab: 'admission',
    kind: 'count',
    label: 'Per-process memory limit (MB)',
    hint: '',
    max: 1_048_576,
  },
  {
    name: 'workflow.spendLimit',
    tab: 'spend',
    kind: 'count',
    label: 'Spend limit per workflow (tokens)',
    hint: 'Billable tokens one workflow may spend before the platform pauses it. 0 means no limit.',
  },
  {
    name: 'concierge.idleTimeout',
    tab: 'concierge',
    kind: 'duration',
    label: 'Close an idle Concierge after',
    hint: 'A Concierge nobody has open and that has done nothing for this long is ended. For example 1h or 30m.',
  },
  {
    name: 'quiet.window',
    tab: 'sweeps',
    kind: 'duration',
    label: 'Quiet team window',
    hint: 'A team with open work that has said nothing for this long is reported as quiet.',
  },
  {
    name: 'resume.maxAutomatic',
    tab: 'sweeps',
    kind: 'count',
    label: 'Automatic resumes per workflow',
    hint: 'How many times the platform resumes a workflow by itself after a quota or rate failure. 0 turns it off.',
    max: 100,
  },
  {
    name: 'causation.depthLimit',
    tab: 'sweeps',
    kind: 'count',
    label: 'Longest chain of instructions',
    hint: 'A tell deeper than this in one workflow is refused, which stops two agents instructing each other forever. 0 means no limit.',
    max: 10000,
  },
  {
    name: KanbanWipLimits,
    tab: 'kanban',
    kind: 'lanes',
    label: 'Per-lane limits',
    hint: 'Advisory: a lane header turns amber over its limit, and nothing is blocked. Empty means no limit.',
  },
  {
    name: 'theme.default',
    tab: 'sweeps',
    kind: 'theme',
    label: 'Default theme',
    hint: 'What a browser shows before its person picks a theme. Read on the next load.',
  },
  {
    name: SystemPackages,
    tab: 'system',
    kind: 'packages',
    label: 'System packages',
    hint: `OS packages installed with apt-get when the container starts. Agents are not root and cannot install one themselves. ${SystemPackagesRestartSentence}`,
  },
]

export const ThemeChoices = ['auto', 'light', 'dark'] as const

/** Durations the server accepts: one minute to thirty days. */
const MinDurationSeconds = 60
const MaxDurationSeconds = 30 * 24 * 3600

/** A package name as the server and the entrypoint take it (SystemPackages.IsValidName). */
const PackageName = /^[a-z0-9][a-z0-9+.-]{1,99}$/

/** The most packages the server accepts. */
const MaxPackages = 100

/** The names typed into a packages box, in order, without repeats. */
export function packageNames(text: string): string[] {
  return [...new Set(text.split(/[\s,]+/).filter((name) => name !== ''))]
}

/** The longest tag the server accepts in `agents.tags`. */
const MaxTagLength = 64

/**
 * `agents.tags` read tolerantly: an object of preset name to a list of strings, or a JSON string of
 * one. Anything else in it is dropped rather than shown.
 */
export function tagMapOf(value: unknown): Record<string, string[]> {
  let parsed = value

  if (typeof value === 'string') {
    try {
      parsed = JSON.parse(value)
    } catch {
      return {}
    }
  }

  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {}

  const map: Record<string, string[]> = {}
  for (const [preset, tags] of Object.entries(parsed as Record<string, unknown>)) {
    if (Array.isArray(tags)) map[preset] = tags.filter((tag): tag is string => typeof tag === 'string')
  }

  return map
}

/**
 * `agents.credentialSource` read tolerantly: an object of preset name to `home` or `issued`, or a
 * JSON string of one. Anything else in it is dropped rather than shown. Absent means home.
 */
export function sourceMapOf(value: unknown): Record<string, 'home' | 'issued'> {
  let parsed = value

  if (typeof value === 'string') {
    try {
      parsed = JSON.parse(value)
    } catch {
      return {}
    }
  }

  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {}

  const map: Record<string, 'home' | 'issued'> = {}
  for (const [preset, source] of Object.entries(parsed as Record<string, unknown>)) {
    if (source === 'home' || source === 'issued') map[preset] = source
  }

  return map
}

/** Why a preset's tags cannot be saved to `agents.tags`, or null when they can. As the server checks. */
export function validateTags(tags: readonly string[]): string | null {
  const bad = tags.find((tag) => tag.trim() === '' || tag.trim().length > MaxTagLength)
  if (bad !== undefined) return `A tag is a word of 1 to ${MaxTagLength} characters.`

  return null
}

/** The largest lane limit the server accepts. */
const MaxLaneLimit = 10000

/** The name prefix the server gives each file-browser root, which rides the list read-only. */
const RootPrefix = 'fileBrowser.roots.'

/**
 * The GET (and PUT 200) body as the dialog reads it.
 *
 * `settings` is a list of named records; the file-browser roots ride it as READ-ONLY entries named
 * `fileBrowser.roots.<name>`, and are split out here into `roots` so no field is ever drawn for
 * them. A map keyed by name, or a separate `roots` list, are read too.
 */
export function normaliseTenantSettings(body: unknown): TenantSettings {
  const record = (body ?? {}) as Record<string, unknown>
  const raw = Array.isArray(body) ? body : record.settings

  let entries: Record<string, unknown>[] = []

  if (Array.isArray(raw)) {
    entries = raw.map((entry) => entry as Record<string, unknown>)
  } else if (raw && typeof raw === 'object') {
    entries = Object.entries(raw as Record<string, unknown>).map(([name, entry]) => ({ ...(entry as object), name }))
  }

  const settings: TenantSetting[] = []
  const roots: TenantRoot[] = []

  for (const entry of entries) {
    const name = typeof entry.name === 'string' ? entry.name : ''
    if (name === '') continue

    if (name.startsWith(RootPrefix) || entry.readOnly === true) {
      roots.push({
        name: name.startsWith(RootPrefix) ? name.slice(RootPrefix.length) : name,
        path: typeof entry.value === 'string' ? entry.value : String(entry.value ?? ''),
        note: typeof entry.note === 'string' ? entry.note : '',
      })
      continue
    }

    settings.push(settingFrom(entry, name))
  }

  const extra = record.roots ?? record.fileBrowserRoots
  if (Array.isArray(extra)) {
    for (const root of extra) {
      const path = typeof root === 'string' ? root : String((root as { path?: unknown }).path ?? '')
      roots.push({ name: '', path, note: '' })
    }
  }

  return { settings, roots }
}

function settingFrom(entry: Record<string, unknown>, name: string): TenantSetting {
  return {
    name,
    value: entry.value ?? null,
    default: entry.default ?? null,
    defaultSource: entry.defaultSource === 'appsettings' || entry.defaultSource === 'builtIn' ? entry.defaultSource : null,
    source: entry.source === 'row' ? 'row' : 'appsettings',
    updatedAt: typeof entry.updatedAt === 'string' ? entry.updatedAt : null,
    updatedBy: typeof entry.updatedBy === 'string' ? entry.updatedBy : null,
    description: typeof entry.description === 'string' ? entry.description : '',
  }
}

/** A setting's value as the text in its box. Objects are not edited through this; a list is its items. */
export function draftOf(value: unknown): string {
  if (value === null || value === undefined) return ''
  if (Array.isArray(value)) return value.map(String).join(' ')
  if (typeof value === 'object') return JSON.stringify(value)

  return String(value)
}

/**
 * "Set by a@b.com, 23 Sep 14:02", "From the host configuration (default 4)" or "The built-in default (25)". The line under every
 * field, so a person can see whether the number in front of them was a choice or a default.
 */
export function sourceLine(setting: TenantSetting | undefined, format: (iso: string) => string = defaultStamp): string {
  if (!setting) return 'Not reported by this server.'

  if (setting.source === 'row') {
    const who = setting.updatedBy ?? 'someone'
    const when = setting.updatedAt ? `, ${format(setting.updatedAt)}` : ''

    return `Set by ${who}${when}`
  }

  const shown = setting.default === null || typeof setting.default === 'object' ? null : draftOf(setting.default)

  // Says what the reset confirmation said: a built-in default is not the host configuration.
  if (setting.defaultSource === 'builtIn') return shown === null ? 'The built-in default' : `The built-in default (${shown})`

  const fallback = shown === null ? '' : ` (default ${shown})`

  // Not "appsettings.json": any configuration source can set it, and the container sets the WIP
  // limit from an environment variable.
  return `From the host configuration${fallback}`
}

/**
 * True when "Reset to default" is offered: only for a value a person set (`source: row`). A value
 * already from appsettings or the built-in default has nothing to reset.
 */
export const canReset = (setting: TenantSetting | undefined): boolean => setting?.source === 'row'

/** A default as a person reads it. Empty is "nothing"; a lane map is its lanes, by title when known. */
export function defaultText(value: unknown, laneTitle: (id: string) => string = (id) => id): string {
  if (value === null || value === undefined || value === '') return 'nothing'
  if (Array.isArray(value)) return value.length === 0 ? 'nothing' : value.map(String).join(', ')

  if (typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
    if (entries.length === 0) return 'nothing'

    return entries.map(([key, entry]) => `${laneTitle(key)}: ${draftOf(entry)}`).join(', ')
  }

  return String(value)
}

/**
 * What a reset would leave, said before the person confirms: the value and where it comes from.
 * The value is the server's `default` - never worked out here - and when the server does not say
 * where it comes from, neither does this.
 */
export function resetLine(setting: TenantSetting, laneTitle?: (id: string) => string): string {
  const value = defaultText(setting.default, laneTitle)

  if (setting.defaultSource === 'appsettings') return `It will then be ${value}, from the host configuration (appsettings).`
  if (setting.defaultSource === 'builtIn') return `It will then be ${value}, the built-in default.`

  return `It will then be ${value}. This server does not say whether that is from appsettings or the built-in default.`
}

function defaultStamp(iso: string): string {
  const parsed = Date.parse(iso)
  if (Number.isNaN(parsed)) return iso

  return new Date(parsed).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}

/** Seconds in a duration typed as `8h`, `30m`, `1h30m`, `90s`, `2d` or .NET's `08:00:00`. Null when unreadable. */
export function durationSeconds(text: string): number | null {
  const trimmed = text.trim()
  if (trimmed === '') return null

  const span = /^(?:(\d+)\.)?(\d{1,2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?$/.exec(trimmed)
  if (span) {
    const [, days, hours, minutes, seconds] = span
    return Number(days ?? 0) * 86400 + Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds ?? 0)
  }

  const units = /^(?:(\d+)d)?\s*(?:(\d+)h)?\s*(?:(\d+)m)?\s*(?:(\d+)s)?$/i.exec(trimmed)
  if (units && units.slice(1).some((part) => part !== undefined)) {
    const [, d, h, m, s] = units
    return Number(d ?? 0) * 86400 + Number(h ?? 0) * 3600 + Number(m ?? 0) * 60 + Number(s ?? 0)
  }

  return null
}

/** Why a typed value cannot be saved, or null when it can. The words go under the field. */
export function validateDraft(kind: TenantSettingKind, text: string, max?: number): string | null {
  const trimmed = text.trim()

  if (kind === 'count') {
    if (!/^\d+$/.test(trimmed)) return 'A whole number, 0 or more.'
    if (!Number.isSafeInteger(Number(trimmed))) return 'That number is too large.'
    if (max !== undefined && Number(trimmed) > max) return `At most ${max}.`
    return null
  }

  if (kind === 'duration') {
    const seconds = durationSeconds(trimmed)
    if (seconds === null) return 'A duration such as 8h, 30m or 00:45:00.'
    if (seconds < MinDurationSeconds || seconds > MaxDurationSeconds) return 'Between one minute and 30 days.'
    return null
  }

  if (kind === 'theme') {
    return (ThemeChoices as readonly string[]).includes(trimmed) ? null : 'Auto, light or dark.'
  }

  if (kind === 'packages') {
    const names = packageNames(trimmed)
    const bad = names.find((name) => !PackageName.test(name))
    if (bad !== undefined) return `'${bad}' is not a package name: lower-case letters, digits and + - . only.`
    if (names.length > MaxPackages) return `At most ${MaxPackages} packages.`
    return null
  }

  return null
}

/** A lane limit box: empty is no limit, otherwise a whole number of at least 1. */
export function validateLaneLimit(text: string): string | null {
  const trimmed = text.trim()
  if (trimmed === '') return null
  if (!/^\d+$/.test(trimmed) || Number(trimmed) < 1) return 'A whole number, 1 or more, or empty for no limit.'
  if (Number(trimmed) > MaxLaneLimit) return `At most ${MaxLaneLimit}.`

  return null
}

/** A duration as the server spells it: `hh:mm:ss`, or `d.hh:mm:ss` from a day up. */
export function timeSpanOf(seconds: number): string {
  const days = Math.floor(seconds / 86400)
  const rest = seconds % 86400
  const pad = (n: number) => String(n).padStart(2, '0')
  const clock = `${pad(Math.floor(rest / 3600))}:${pad(Math.floor((rest % 3600) / 60))}:${pad(rest % 60)}`

  return days > 0 ? `${days}.${clock}` : clock
}

/**
 * The value a draft is sent as. Counts go as numbers, durations as `hh:mm:ss` whatever spelling was
 * typed (`8h` goes as `08:00:00`), the theme as the word.
 */
export function wireValue(kind: TenantSettingKind, text: string): unknown {
  if (kind === 'count') return Number(text.trim())
  if (kind === 'duration') return timeSpanOf(durationSeconds(text) ?? 0)
  if (kind === 'packages') return packageNames(text)

  return text.trim()
}

/** A draft equals the stored value: by number for counts, by length of time for durations. */
function sameValue(kind: TenantSettingKind, draft: string, stored: unknown): boolean {
  const text = draftOf(stored).trim()

  if (kind === 'duration') {
    const a = durationSeconds(draft)
    return a !== null && a === durationSeconds(text)
  }
  if (kind === 'count') return draft.trim() !== '' && Number(draft.trim()) === Number(text)
  if (kind === 'packages') return packageNames(draft).join(' ') === packageNames(text).join(' ')

  return draft.trim() === text
}

/** The lane map as the server holds it: only the lanes with a limit, in lane order. */
export function laneLimitsFrom(drafts: Record<string, string>): Record<string, number> {
  const limits: Record<string, number> = {}

  for (const [lane, text] of Object.entries(drafts)) {
    if (isRunningLane(lane)) continue
    if (text.trim() !== '') limits[lane] = Number(text.trim())
  }

  return limits
}

/** The stored lane map read tolerantly: an object of numbers, or a JSON string of one. */
export function laneLimitsOf(value: unknown): Record<string, number> {
  let parsed = value

  if (typeof value === 'string') {
    try {
      parsed = JSON.parse(value)
    } catch {
      return {}
    }
  }

  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {}

  const limits: Record<string, number> = {}
  for (const [lane, limit] of Object.entries(parsed as Record<string, unknown>)) {
    if (isRunningLane(lane)) continue
    const number = Number(limit)
    if (Number.isFinite(number) && number > 0) limits[lane] = number
  }

  return limits
}

function sameLimits(a: Record<string, number>, b: Record<string, number>): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)])

  return [...keys].every((key) => a[key] === b[key])
}

/**
 * THE PARTIAL MAP SAVE SENDS: only what moved. Empty means Save stays disabled.
 *
 * `drafts` holds the text of each scalar field by setting name; `laneDrafts` the lane boxes.
 */
export function changedSettings(
  settings: readonly TenantSetting[],
  drafts: Record<string, string>,
  laneDrafts: Record<string, string>,
): Record<string, unknown> {
  const changes: Record<string, unknown> = {}

  for (const field of TenantSettingFields) {
    const setting = settings.find((entry) => entry.name === field.name)
    if (!setting) continue

    if (field.kind === 'lanes') {
      const next = laneLimitsFrom(laneDrafts)
      if (!sameLimits(next, laneLimitsOf(setting.value))) changes[field.name] = next
      continue
    }

    const draft = drafts[field.name]
    if (draft === undefined || sameValue(field.kind, draft, setting.value)) continue

    changes[field.name] = wireValue(field.kind, draft)
  }

  return changes
}

/** Every field's complaint, by setting name - or by `lane:<id>` for a lane box. Empty is valid. */
export function draftErrors(
  settings: readonly TenantSetting[],
  drafts: Record<string, string>,
  laneDrafts: Record<string, string>,
): Record<string, string> {
  const errors: Record<string, string> = {}

  for (const field of TenantSettingFields) {
    if (!settings.some((entry) => entry.name === field.name)) continue

    if (field.kind === 'lanes') {
      for (const [lane, text] of Object.entries(laneDrafts)) {
        if (isRunningLane(lane)) continue
        const error = validateLaneLimit(text)
        if (error) errors[`lane:${lane}`] = error
      }
      continue
    }

    const error = validateDraft(field.kind, drafts[field.name] ?? '', field.max)
    if (error) errors[field.name] = error
  }

  return errors
}

/**
 * Which setting a 400 is about. The server names the field; `field` on the body when it sends one,
 * otherwise the first known setting name mentioned in the message.
 */
export function rejectedField(field: string | null | undefined, message: string): string | null {
  if (field) return field

  return TenantSettingFields.find((entry) => message.includes(entry.name))?.name ?? null
}

/**
 * A server 400's message as the screen shows it: every setting KEY replaced by that setting's
 * label, so `wip.maxRunning must be at least 0.` reads `Agents running at once must be at least 0.`
 * The server words its refusals for the API, key first; a person set a labelled box, and a key never
 * reaches the screen - `field` included when it is one the dialog has no label for.
 */
export function settingErrorText(field: string | null | undefined, message: string): string {
  const labels: [string, string][] = TenantSettingFields.map((entry) => [entry.name, entry.label])
  if (field && !labels.some(([name]) => name === field)) labels.push([field, 'This setting'])

  return withLabels(labels, message)
}

/**
 * A server description as the field shows it: every setting key it mentions replaced by that
 * setting's label, so "the default for wip.maxRunning" reads "the default for Agents running at once".
 */
export function descriptionText(description: string): string {
  return withLabels(TenantSettingFields.map((entry) => [entry.name, entry.label]), description)
}

function withLabels(labels: [string, string][], message: string): string {
  // Longest key first, so a key that is a prefix of another cannot take a bite out of it.
  const ordered = [...labels].sort(([a], [b]) => b.length - a.length)

  return ordered.reduce(
    (text, [name, label]) => text.split(`'${name}'`).join(label).split(name).join(label),
    message,
  )
}

/** One line of the Catalog health tab: what was probed, what it found, and whether that is trouble. */
export interface HealthLine {
  label: string
  text: string
  ok: boolean | null
}

/**
 * The Catalog health summaries, read-only. `null` inputs are NOT MEASURED, which is not the same as
 * healthy, and the line says so.
 */
export function catalogHealth(input: {
  /** Agents whose command is not on this machine, or null when the probe has not answered. */
  notInstalled: readonly string[] | null
  auth: readonly { agent: string; installed: boolean | null; authenticated: boolean | null }[] | null
}): HealthLine[] {
  const lines: HealthLine[] = []

  if (input.notInstalled === null) {
    lines.push({ label: 'Install probe', text: 'Not measured.', ok: null })
  } else {
    const missing = input.notInstalled
    lines.push({
      label: 'Install probe',
      text: missing.length === 0 ? 'Every Agent’s command is installed.' : `Not installed: ${missing.join(', ')}.`,
      ok: missing.length === 0,
    })
  }

  if (input.auth === null) {
    lines.push({ label: 'Auth probe', text: 'Not measured.', ok: null })
  } else {
    const signedOut = input.auth.filter((entry) => entry.installed && entry.authenticated === false).map((entry) => entry.agent)
    const unknown = input.auth.filter((entry) => entry.installed !== false && entry.authenticated === null).length
    const tail = unknown > 0 ? ` ${unknown} not measured.` : ''
    lines.push({
      label: 'Auth probe',
      text: (signedOut.length === 0 ? 'No installed Agent is signed out.' : `Signed out: ${signedOut.join(', ')}.`) + tail,
      ok: signedOut.length === 0,
    })
  }

  return lines
}
