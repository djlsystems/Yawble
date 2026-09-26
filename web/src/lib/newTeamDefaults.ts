import type { Catalog, FileSystemRoot } from '../api/types'
import { allowedAgentOptions, normalizeAllowlist } from './memberAllowlist'
import { nextTeamName } from './teamRoot'

const StorageKey = 'harness.newTeamDefaults'
const MaxRememberedRepos = 10

export interface RememberedNewTeamValues {
  managerAgent: string | null
  memberAgents: string[] | null
  root: string | null
  repos: string[]
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

export function readRemembered(): RememberedNewTeamValues | null {
  try {
    const stored = localStorage.getItem(StorageKey)
    if (!stored) return null

    const parsed = readObject(JSON.parse(stored))
    if (!parsed) return null

    const remembered: RememberedNewTeamValues = {
      managerAgent: readString(parsed.managerAgent),
      memberAgents: (() => {
        const values = normalizeAllowlist(readStrings(parsed.memberAgents))
        return values.length > 0 ? values : null
      })(),
      root: readString(parsed.root),
      repos: recentDistinctRepos(readStrings(parsed.repos)),
    }

    return remembered
  } catch {
    return null
  }
}

export function remember(values: NewTeamValuesToRemember): void {
  try {
    const previous = readRemembered()
    const remembered: RememberedNewTeamValues = {
      managerAgent: values.managerAgent.trim(),
      memberAgents: normalizeAllowlist(values.memberAgents),
      root: readString(values.root),
      repos: recentDistinctRepos(values.repos, previous?.repos),
    }

    localStorage.setItem(StorageKey, JSON.stringify(remembered))
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
  }
}
