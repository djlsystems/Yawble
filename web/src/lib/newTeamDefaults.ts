import type { Catalog, FileSystemRoot } from '../api/types'
import { allowedAgentOptions, normalizeAllowlist } from './memberAllowlist'
import { nextTeamName } from './teamRoot'

const StorageKey = 'harness.newTeamDefaults'
const MaxRememberedRepos = 10

/**
 * RECENT REPOSITORIES ARE KEPT PER INSTANCE, keyed by the `instanceId` the instance itself answers.
 * Browser storage is keyed only by the address, so a list kept under one key outlives an uninstall:
 * a fresh install at the same address would offer repositories it has never seen. Keyed by the
 * instance's own id, a new install starts with an empty list.
 *
 * `repos` inside `StorageKey` is the old address-wide list. It is never read, and the next save
 * drops it.
 *
 * `BrowserReposKey` is every repository used in this browser, whatever the instance. It is offered
 * only when the instance cannot be told, and then labelled as exactly that rather than as this
 * instance's.
 */
const InstanceReposKeyPrefix = 'harness.recentRepos.instance.'
const BrowserReposKey = 'harness.recentRepos.browser'

/** Whose recent repositories `repos` are: this instance's, or anything used in this browser. */
export type RecentReposScope = 'instance' | 'browser'

export interface RememberedNewTeamValues {
  managerAgent: string | null
  memberAgents: string[] | null
  root: string | null
  repos: string[]
  reposScope?: RecentReposScope
}

export interface NewTeamValuesToRemember {
  managerAgent: string
  memberAgents: string[]
  root: string | null
  repos: string[]
}

export interface AppliedNewTeamDefaults {
  name: string
  managerAgent: string | null
  memberAgents: string[] | null
  root: string | null
  repos: string[]
  repoSuggestions: string[]
  repoSuggestionsScope: RecentReposScope
}

function readString(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  return trimmed === '' ? null : trimmed
}

function readStrings(value: unknown): string[] {
  if (!Array.isArray(value)) return []

  const items: string[] = []
  for (const entry of value) {
    if (typeof entry !== 'string') continue
    const trimmed = entry.trim()
    if (trimmed !== '') items.push(trimmed)
  }

  return items
}

function recentDistinctRepos(current: string[], previous: string[] = []): string[] {
  const merged = [...readStrings(current), ...readStrings(previous)]
  const seen = new Set<string>()
  const unique: string[] = []

  for (const url of merged) {
    if (seen.has(url)) continue
    seen.add(url)
    unique.push(url)
    if (unique.length >= MaxRememberedRepos) break
  }

  return unique
}

function readObject(value: unknown): Record<string, unknown> | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null
  return value as Record<string, unknown>
}

function reposKey(instanceId: string | null): string {
  return instanceId ? InstanceReposKeyPrefix + instanceId : BrowserReposKey
}

function readRepos(key: string): string[] {
  try {
    const stored = localStorage.getItem(key)
    return stored ? recentDistinctRepos(readStrings(JSON.parse(stored))) : []
  } catch {
    return []
  }
}

/** `instanceId` is what the instance answered, or null when it could not be told. */
export function readRemembered(instanceId: string | null): RememberedNewTeamValues | null {
  try {
    const stored = localStorage.getItem(StorageKey)
    const parsed = stored ? readObject(JSON.parse(stored)) : null
    const repos = readRepos(reposKey(instanceId))
    if (!parsed && repos.length === 0) return null

    const remembered: RememberedNewTeamValues = {
      managerAgent: readString(parsed?.managerAgent),
      memberAgents: (() => {
        const values = normalizeAllowlist(readStrings(parsed?.memberAgents))
        return values.length > 0 ? values : null
      })(),
      root: readString(parsed?.root),
      repos,
      reposScope: instanceId ? 'instance' : 'browser',
    }

    return remembered
  } catch {
    return null
  }
}

export function remember(values: NewTeamValuesToRemember, instanceId: string | null): void {
  try {
    const remembered = {
      managerAgent: values.managerAgent.trim(),
      memberAgents: normalizeAllowlist(values.memberAgents),
      root: readString(values.root),
    }

    localStorage.setItem(StorageKey, JSON.stringify(remembered))

    // The browser-wide list always, and this instance's list when the instance said who it is.
    const keys = instanceId ? [BrowserReposKey, reposKey(instanceId)] : [BrowserReposKey]
    for (const key of keys) {
      localStorage.setItem(key, JSON.stringify(recentDistinctRepos(values.repos, readRepos(key))))
    }
  } catch {
    // Keep team creation successful even when storage is unavailable.
  }
}

function pickRemembered(
  value: string | null,
  byName: Map<string, string>,
): string | null {
  if (!value) return null
  return byName.get(value.toLowerCase()) ?? null
}

export function applyDefaults(
  remembered: RememberedNewTeamValues | null,
  catalog: Catalog,
  roots: FileSystemRoot[],
  existingTeamIds: string[],
): AppliedNewTeamDefaults {
  const headlessByName = new Map(
    catalog.agents
      .filter((agent) => agent.mode === 'Headless' && !agent.hidden)
      .map((agent) => [agent.name.toLowerCase(), agent.name]),
  )
  const rootByPath = new Map(roots.map((entry) => [entry.path.toLowerCase(), entry.path]))
  const rememberedRepos = recentDistinctRepos(remembered?.repos ?? [])
  const rememberedAllowlist = remembered?.memberAgents
    ? allowedAgentOptions([...headlessByName.values()], remembered.memberAgents)
    : []

  return {
    name: nextTeamName(existingTeamIds),
    managerAgent: pickRemembered(remembered?.managerAgent ?? null, headlessByName),
    memberAgents: rememberedAllowlist.length > 0 ? rememberedAllowlist : null,
    root: pickRemembered(remembered?.root ?? null, rootByPath),
    repos: [],
    repoSuggestions: rememberedRepos,
    repoSuggestionsScope: remembered?.reposScope ?? 'instance',
  }
}
