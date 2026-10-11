import { isValidTimezone } from './triggers'

/**
 * Every input rule the server enforces, in one place, so a regex or a limit lives once.
 *
 * Each rule is a Quasar rule function: it returns `true` or a sentence saying what is wrong, in
 * the interface's voice. Use them directly in `:rules`:
 *
 *     <q-input v-model="name" :rules="[identifier]" />
 *     <q-input v-model="urls[i]" :rules="repoUrlRules(urls, i)" />
 *
 * Rules for fields that are always required (names, identifiers, URLs, email, password, timezone)
 * refuse an empty value themselves. `usageFormat` and `positiveInt` guard fields that may be left
 * blank, so they pass an empty value; put `required()` in front of them where a value is needed.
 *
 * Each limit names the server constant it mirrors. When one changes there, change it here.
 */

/** A rule for `:rules`. Assignable to Quasar's `ValidationRule`. */
export type Rule = (value: unknown) => true | string

/** `TeamRegistry.MaximumLabelLength`: a team or member name, after trimming. */
export const MAXIMUM_LABEL_LENGTH = 60

/** `AuthEndpoints.MinimumPasswordLength`. */
export const MINIMUM_PASSWORD_LENGTH = 8

/** The longest agent, prompt, skill or new-team identifier. */
export const MAXIMUM_IDENTIFIER_LENGTH = 32

/** `TeamEnv.ReservedPrefix`, compared case-insensitively as the server does. */
export const RESERVED_ENV_PREFIX = 'HARNESS_'

/** `TeamEnv.MaxEntries`. */
export const MAXIMUM_ENV_ENTRIES = 64

/** `TeamEnv.MaxValueChars`. */
export const MAXIMUM_ENV_VALUE_LENGTH = 4096

/** Agent, prompt, skill and new-team identifiers. */
export const IDENTIFIER_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/

/**
 * Environment variable names, as `TeamEnv` checks them: letters of either case, digits and `_`,
 * not starting with a digit.
 */
export const ENV_NAME_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/

/** The usage formats `ProcessAgentRunner` can parse. Anything else reports no usage. */
export const USAGE_FORMATS = [
  'claude-json',
  'codex-total',
  'copilot-usage-file',
  'grok-json',
  'antigravity-json',
] as const

export type UsageFormat = (typeof USAGE_FORMATS)[number]

/** `USAGE_FORMATS` as `q-select` options, with the empty choice first. */
export const usageFormatOptions: ReadonlyArray<{ label: string; value: UsageFormat | '' }> = [
  { label: 'None', value: '' },
  ...USAGE_FORMATS.map((value) => ({ label: value, value })),
]

const text = (value: unknown): string =>
  value === null || value === undefined ? '' : String(value)

/** Refuses an empty or whitespace-only value. */
export function required(message = 'This is required.'): Rule {
  return (value) => (text(value).trim().length > 0 ? true : message)
}

function label(what: string): Rule {
  return (value) => {
    const name = text(value).trim()
    if (name.length === 0) return `A ${what} needs a name.`
    if (name.length > MAXIMUM_LABEL_LENGTH) {
      return `A ${what} name cannot be longer than ${MAXIMUM_LABEL_LENGTH} characters.`
    }
    return true
  }
}

/** A team's display name: 1 to 60 characters after trimming. */
export const teamLabel: Rule = label('team')

/** A member's display name: 1 to 60 characters after trimming. */
export const memberName: Rule = label('member')

/** An agent, prompt, skill or new-team identifier. */
export const identifier: Rule = (value) => {
  const name = text(value)
  if (name.length === 0) return 'A name is required.'
  if (IDENTIFIER_PATTERN.test(name)) return true
  if (!/^[A-Za-z0-9]/.test(name)) return 'Names start with a letter or digit.'
  return `Names use letters, digits, - and _, up to ${MAXIMUM_IDENTIFIER_LENGTH} characters.`
}

/** `SqliteSkillStore.LegalName`: skills are stricter than other identifiers. */
export const SKILL_NAME_PATTERN = /^[a-z0-9][a-z0-9-]{0,47}$/

/** A skill's name: lowercase kebab-case, up to 48 characters. */
export const skillName: Rule = (value) => {
  const name = text(value)
  if (name.length === 0) return 'A skill needs a name.'
  if (SKILL_NAME_PATTERN.test(name)) return true
  return 'Skill names use lowercase letters, digits and -, up to 48 characters, starting with a letter or digit.'
}

/**
 * The folder a repository URL clones into, as `RepoUrls.DeriveName` computes it: the last path
 * segment, unescaped, without a trailing `.git`. Null when the value is not an absolute http or
 * https URL.
 */
export function repoFolderName(url: string): string | null {
  if (isLocalRepoReference(url)) return localRepoNameOf(url)
  const parsed = parseHttpUrl(url)
  if (!parsed) return null
  const leaf = parsed.pathname.split('/').pop() ?? ''
  let name: string
  try {
    name = decodeURIComponent(leaf)
  } catch {
    name = leaf
  }
  return name.toLowerCase().endsWith('.git') ? name.slice(0, -4) : name
}

function parseHttpUrl(value: string): URL | null {
  const trimmed = value.trim()
  if (!/^https?:\/\//i.test(trimmed)) return null
  try {
    const url = new URL(trimmed)
    return url.hostname ? url : null
  } catch {
    return null
  }
}

/** `RepoUrls.IsLegalFolderName`: what Linux refuses in one name, and `.git`, which git refuses
 *  as a path component. The host runs only on Linux, so no Windows rule is repeated here. */
function isLegalFolderName(name: string): boolean {
  return (
    name.length > 0
    && !/[/\u0000]/.test(name)
    && name !== '.' && name !== '..'
    && name.toLowerCase() !== '.git'
  )
}

/** The prefix a team's repository list names one of the instance's local repositories with. */
export const LOCAL_REPO_SCHEME = 'local:'

/** `LocalRepos.IsLegalName`: what a local repository may be called. */
export const LOCAL_REPO_NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/

/** Whether a repository list entry is `local:<name>` rather than a URL. */
export function isLocalRepoReference(value: string): boolean {
  return value.trim().toLowerCase().startsWith(LOCAL_REPO_SCHEME)
}

/** The name in `local:<name>`, unchecked. */
export function localRepoNameOf(value: string): string {
  return value.trim().slice(LOCAL_REPO_SCHEME.length)
}

/** `LocalRepos.IllegalName`'s rule for a local repository name, as one sentence. */
export const LOCAL_REPO_NAME_RULE =
  "Use 1 to 100 letters, digits, '.', '_' or '-', starting with a letter or digit, not ending in '.git' or '.lock', with no '..'."

/** Whether `name` is a legal local repository name: `LocalRepos.IsLegalName`'s rule. */
export function isLegalLocalRepoName(name: string): boolean {
  return LOCAL_REPO_NAME_PATTERN.test(name)
    && !name.includes('..')
    && !name.toLowerCase().endsWith('.git')
    && !name.toLowerCase().endsWith('.lock')
}

/** The problem with a local repository name, or null, naming the name as the URL field and the API do. */
export function localRepoNameProblem(name: string): string | null {
  if (name.length === 0) return 'A local repository needs a name.'
  return isLegalLocalRepoName(name) ? null : `'${name}' is not a local repository name. ${LOCAL_REPO_NAME_RULE}`
}

/** A local repository's name, as the Create a local repository field checks it. */
export const localRepoName: Rule = (value) => localRepoNameProblem(text(value).trim()) ?? true

/**
 * One repository URL on its own: absolute http or https, ending in a usable folder name - or
 * `local:<name>` naming one of the instance's local repositories. Whether that one exists is the
 * server's answer.
 */
export const repoUrl: Rule = (value) => {
  const url = text(value).trim()
  if (url.length === 0) return 'Enter a repository URL, or remove this row.'
  if (isLocalRepoReference(url)) {
    return isLegalLocalRepoName(localRepoNameOf(url))
      ? true
      : `'${url}' is not a local repository name. ${LOCAL_REPO_NAME_RULE}`
  }
  const name = repoFolderName(url)
  if (name === null) return 'Use an absolute http or https URL, such as https://github.com/owner/repo.git, or local:<name> for a local repository.'
  if (!isLegalFolderName(name)) {
    return name.length === 0
      ? 'The URL must end in the repository name.'
      : `This URL would clone into '${name}', which cannot be a folder name.`
  }
  return true
}

/**
 * Refuses a value that another entry of `list` already holds. `index` is the entry being edited,
 * which is skipped; the value checked is the one Quasar passes in, so a stale list entry at
 * `index` does not matter. `key` decides what counts as the same (default: the trimmed value).
 */
export function uniqueIn(
  list: readonly string[],
  index: number,
  message: string | ((value: string) => string) = 'This appears twice in the list.',
  key: (value: string) => string = (value) => value.trim(),
): Rule {
  return (value) => {
    const mine = key(text(value))
    const clash = list.some((other, at) => at !== index && key(other) === mine)
    if (!clash) return true
    return typeof message === 'function' ? message(text(value)) : message
  }
}

/**
 * The rules for entry `index` of a list of repository URLs: `repoUrl`, and no two URLs cloning
 * into the same folder (compared case-insensitively, as the server does).
 */
export function repoUrlRules(list: readonly string[], index: number): Rule[] {
  return [
    repoUrl,
    uniqueIn(
      list,
      index,
      (value) => `Another URL in this list already clones into '${repoFolderName(value)}'.`,
      (value) => repoFolderName(value)?.toLowerCase() ?? `\u0000${value}`,
    ),
  ]
}

/** One environment variable name: `TeamEnv`'s pattern, and not the platform's `HARNESS_` prefix. */
export const envName: Rule = (value) => {
  const name = text(value).trim()
  if (name.length === 0) return 'A variable needs a name.'
  if (!ENV_NAME_PATTERN.test(name)) {
    return 'Names use letters, digits and _, starting with a letter or _.'
  }
  if (name.toUpperCase().startsWith(RESERVED_ENV_PREFIX)) {
    return `Names beginning ${RESERVED_ENV_PREFIX} are the platform's own and cannot be set by a team.`
  }
  return true
}

/**
 * The rules for entry `index` of a list of environment names: `envName`, and not set twice
 * (compared exactly, as the server does).
 */
export function envNameRules(names: readonly string[], index: number): Rule[] {
  return [envName, uniqueIn(names, index, (value) => `'${value.trim()}' is set twice.`)]
}

/** One non-blank line of a `KEY=value` block. */
export interface EnvRow {
  /** 1-based line number in the original text. */
  line: number
  key: string
  value: string
  /** Why this row would be refused, or null. */
  error: string | null
}

export interface ParsedEnv {
  rows: EnvRow[]
  /** The valid rows as a map, in order. Rows with an error are left out. */
  entries: Record<string, string>
  /** An error about the block as a whole (too many entries), or null. */
  error: string | null
  /** True when no row has an error and `error` is null. */
  valid: boolean
}

/**
 * Parses a `KEY=value` per line block, the shape AgentEditDialog and TeamSettingsDialog edit.
 * Blank lines are skipped; each line is trimmed; the value is everything after the first `=`.
 * Every row carries its own error, so a dialog can mark the offending line.
 */
export function parseEnvLines(block: string): ParsedEnv {
  const rows: EnvRow[] = []
  const seen = new Set<string>()

  block.split(/\r?\n/).forEach((raw, at) => {
    const line = raw.trim()
    if (line.length === 0) return
    const equals = line.indexOf('=')
    const key = equals < 0 ? line : line.slice(0, equals).trim()
    const value = equals < 0 ? '' : line.slice(equals + 1)

    let error: string | null = null
    if (equals <= 0) {
      error = `Line ${at + 1} needs the form NAME=value.`
    } else {
      const named = envName(key)
      if (named !== true) error = `Line ${at + 1}: ${named}`
      else if (seen.has(key)) error = `Line ${at + 1}: '${key}' is set twice.`
      else if (value.length > MAXIMUM_ENV_VALUE_LENGTH) {
        error = `Line ${at + 1}: the value is longer than ${MAXIMUM_ENV_VALUE_LENGTH} characters.`
      }
    }

    if (error === null) seen.add(key)
    rows.push({ line: at + 1, key, value, error })
  })

  const entries = Object.fromEntries(
    rows.filter((row) => row.error === null).map((row) => [row.key, row.value]),
  )
  const error = rows.length > MAXIMUM_ENV_ENTRIES
    ? `A team may hold at most ${MAXIMUM_ENV_ENTRIES} variables; this has ${rows.length}.`
    : null

  return { rows, entries, error, valid: error === null && rows.every((row) => row.error === null) }
}

/** A `KEY=value` textarea: true, or the first problem `parseEnvLines` finds. */
export const envLines: Rule = (value) => {
  const parsed = parseEnvLines(text(value))
  return parsed.error ?? parsed.rows.find((row) => row.error !== null)?.error ?? true
}

/** An email address, trimmed: contains `@`, as `UserEndpoints` checks. */
export const email: Rule = (value) => {
  const address = text(value).trim()
  if (address.length === 0) return 'An email address is required.'
  if (!address.includes('@')) return 'That is not an email address.'
  return true
}

/** A new password: at least `MINIMUM_PASSWORD_LENGTH` characters, untrimmed as the server counts. */
export const password: Rule = (value) =>
  text(value).length >= MINIMUM_PASSWORD_LENGTH
    ? true
    : `A password needs at least ${MINIMUM_PASSWORD_LENGTH} characters.`

/** One of `USAGE_FORMATS`, or empty for a preset that reports no usage. */
export const usageFormat: Rule = (value) => {
  const format = text(value).trim()
  if (format.length === 0 || (USAGE_FORMATS as readonly string[]).includes(format)) return true
  return `Usage format is one of ${USAGE_FORMATS.join(', ')}, or empty.`
}

const FALLBACK_TIMEZONES = [
  'UTC',
  'Africa/Johannesburg', 'America/Chicago', 'America/Los_Angeles', 'America/New_York',
  'America/Sao_Paulo', 'Asia/Kolkata', 'Asia/Shanghai', 'Asia/Singapore', 'Asia/Tokyo',
  'Australia/Sydney', 'Europe/Berlin', 'Europe/London', 'Europe/Paris', 'Pacific/Auckland',
]

let zones: string[] | null = null

/**
 * IANA timezone names for a picker, `UTC` first. From `Intl.supportedValuesOf` where the runtime
 * has it, otherwise a short list of common zones.
 */
export function timezoneOptions(): string[] {
  if (zones) return zones
  const intl = Intl as typeof Intl & { supportedValuesOf?: (key: 'timeZone') => string[] }
  let names: string[] = []
  try {
    names = intl.supportedValuesOf?.('timeZone') ?? []
  } catch {
    names = []
  }
  if (names.length === 0) names = FALLBACK_TIMEZONES
  zones = ['UTC', ...names.filter((name) => name !== 'UTC')]
  return zones
}

/**
 * A known IANA timezone name. Anything in `timezoneOptions()` passes; so does an alias the runtime
 * resolves (`US/Eastern`, `Asia/Calcutta`). A raw offset such as `+05:00` is refused: the browser
 * accepts it, the server's timezone lookup does not.
 */
export const timezone: Rule = (value) => {
  const zone = text(value).trim()
  if (zone.length === 0) return 'A timezone is required.'
  if (timezoneOptions().includes(zone)) return true
  if (/^[A-Za-z][A-Za-z0-9_+-]*(\/[A-Za-z0-9_+-]+)*$/.test(zone) && isValidTimezone(zone)) {
    return true
  }
  return 'Use a timezone name such as UTC or Europe/London.'
}

/** A whole number, 1 or more. Empty passes: put `required()` first where a value is needed. */
export const positiveInt: Rule = (value) => {
  const number = text(value).trim()
  if (number.length === 0) return true
  return /^\d+$/.test(number) && Number(number) >= 1 ? true : 'Use a whole number, 1 or more.'
}

/**
 * The longest backlog item title. NOT A SERVER LIMIT: `BacklogEndpoints` refuses only a blank
 * title. This bound exists so a title stays one readable line in the Backlog table; raise it freely.
 */
export const MAXIMUM_BACKLOG_TITLE_LENGTH = 200

/** A backlog item's title: required, and at most `MAXIMUM_BACKLOG_TITLE_LENGTH` after trimming. */
export const backlogTitle: Rule = (value) => {
  const title = text(value).trim()
  if (title.length === 0) return 'An item needs a title.'
  if (title.length > MAXIMUM_BACKLOG_TITLE_LENGTH) {
    return `A title cannot be longer than ${MAXIMUM_BACKLOG_TITLE_LENGTH} characters.`
  }
  return true
}

/** Any absolute http or https URL, such as an install link. Refuses empty; wrap in `optional`. */
export const httpUrl: Rule = (value) => {
  const url = text(value).trim()
  if (url.length === 0) return 'Enter a URL.'
  return parseHttpUrl(url) ? true : 'Use an absolute http or https URL, such as https://example.com/install.'
}

/** `rules`, but an empty or whitespace-only value passes: for an optional field. */
export function optional(...rules: Rule[]): Rule[] {
  return rules.map((rule) => (value) => (text(value).trim().length === 0 ? true : rule(value)))
}

/** The first sentence `rules` return for `value`, or null when every rule passes. */
export function firstProblem(rules: readonly Rule[], value: unknown): string | null {
  for (const rule of rules) {
    const result = rule(value)
    if (result !== true) return result
  }
  return null
}

/**
 * The first problem in a whole list of repository URLs, or null: `repoUrlRules` for each entry. For
 * a control that holds the list in one field (a chips `q-select`), where there is no field per URL.
 */
export function repoUrlListProblem(list: readonly string[]): string | null {
  for (const [index, url] of list.entries()) {
    const problem = firstProblem(repoUrlRules(list, index), url)
    if (problem !== null) return `${url.trim()}: ${problem}`
  }
  return null
}

/**
 * Whether a server refusal is about the NAME a person typed, so a dialog can mark that field as well
 * as showing the banner. The client throws the server's `error` text without its status, so these
 * read the sentence: `TeamNameTakenException` (409) and `AgentEndpoints.ValidityRefusalFor`'s
 * duplicate-name refusal (400). When either sentence changes on the server, change it here.
 */
export const teamNameTaken = (message: string): boolean =>
  /^A team called '.*' already exists\.$/.test(message.trim())

export const agentNameTaken = (message: string): boolean =>
  message.trim().startsWith('Two Agents share a name.')

/** One environment variable's value, for a dialog that edits name and value in separate boxes. */
export const envValue: Rule = (value) =>
  text(value).length <= MAXIMUM_ENV_VALUE_LENGTH
    ? true
    : `A value cannot be longer than ${MAXIMUM_ENV_VALUE_LENGTH} characters.`

/** `repoUrlListProblem` as a rule, for a chips `q-select` whose model is the whole list. */
export const repoUrlList: Rule = (value) =>
  repoUrlListProblem(Array.isArray(value) ? value.map((entry) => text(entry)) : []) ?? true
