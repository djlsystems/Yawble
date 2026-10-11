import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Catalog, FileSystemRoot } from '../../api/types'
import {
  applyDefaults,
  readRemembered,
  remember,
  type RememberedNewTeamValues,
} from '../newTeamDefaults'

function catalog(over: Partial<Catalog> = {}): Catalog {
  return {
    agents: [
      { name: 'Manager-A', mode: 'Headless' },
      { name: 'Builder-A', mode: 'Headless' },
      { name: 'Shell-A', mode: 'Interactive' },
      { name: 'Console-A', mode: 'Interactive' },
    ],
    ...over,
  }
}

function roots(paths: string[]): FileSystemRoot[] {
  return paths.map((path, index) => ({
    name: `Root ${index + 1}`,
    path,
    isInstance: index === 0,
    allowCreate: true,
    allowUpdate: true,
    allowDelete: false,
  }))
}

function remembered(over: Partial<RememberedNewTeamValues> = {}): RememberedNewTeamValues {
  return {
    managerAgent: 'Manager-A',
    memberAgents: ['Builder-A', 'Manager-A'],
    root: 'C:\\teams',
    repos: ['https://github.com/os/repo-a.git', 'https://github.com/os/repo-b.git'],
    ...over,
  }
}

beforeEach(() => {
  vi.unstubAllGlobals()
})

describe('applyDefaults', () => {
  it('keeps today\'s defaults when nothing is remembered', () => {
    expect(applyDefaults(null, catalog(), roots(['C:\\teams']), [])).toEqual({
      name: 'Team-1',
      managerAgent: null,
      memberAgents: null,
      root: null,
      repos: [],
      repoSuggestions: [],
      repoSuggestionsScope: 'instance',
    })
  })

  it('offers remembered agents that are still present in the required launch kind', () => {
    const defaults = applyDefaults(remembered(), catalog(), roots(['C:\\teams']), [])

    expect(defaults.managerAgent).toBe('Manager-A')
  })

  it('does not select a remembered agent after that preset was removed', () => {
    const defaults = applyDefaults(
      remembered({ managerAgent: 'Gone-Agent' }),
      catalog(),
      roots(['C:\\teams']),
      [],
    )

    expect(defaults.managerAgent).toBeNull()
  })

  it('does not select a remembered agent after that preset was re-moded', () => {
    const defaults = applyDefaults(
      remembered({ managerAgent: 'Shell-A' }),
      catalog(),
      roots(['C:\\teams']),
      [],
    )

    expect(defaults.managerAgent).toBeNull()
  })

  it('does not select a remembered root that is no longer allowed', () => {
    const defaults = applyDefaults(
      remembered({ root: 'C:\\old' }),
      catalog(),
      roots(['C:\\teams']),
      [],
    )

    expect(defaults.root).toBeNull()
  })

  it('keeps valid allowlist entries and drops invalid ones', () => {
    const defaults = applyDefaults(
      remembered({ memberAgents: ['Builder-A', 'Shell-A', 'Missing-A'] }),
      catalog(),
      roots(['C:\\teams']),
      [],
    )

    expect(defaults.memberAgents).toEqual(['Builder-A'])
  })

  it('leaves allowlist not selected when every remembered entry is invalid', () => {
    const defaults = applyDefaults(
      remembered({ memberAgents: ['Shell-A', 'Missing-A'] }),
      catalog(),
      roots(['C:\\teams']),
      [],
    )

    expect(defaults.memberAgents).toBeNull()
  })

  it('proposes Team-3 when Team-1 and Team-2 identifiers already exist', () => {
    const defaults = applyDefaults(null, catalog(), roots(['C:\\teams']), ['Team-1', 'Team-2'])
    expect(defaults.name).toBe('Team-3')
  })

  it('offers remembered repos as suggestions with nothing pre-selected', () => {
    const defaults = applyDefaults(remembered(), catalog(), roots(['C:\\teams']), [])

    expect(defaults.repoSuggestions).toEqual([
      'https://github.com/os/repo-a.git',
      'https://github.com/os/repo-b.git',
    ])
    expect(defaults.repos).toEqual([])
  })

  it('treats throwing localStorage as nothing remembered', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('blocked')
      },
      setItem: () => {
        throw new Error('blocked')
      },
    })

    expect(readRemembered('instance-a')).toBeNull()
    expect(() =>
      remember({
        managerAgent: 'Manager-A',
        memberAgents: ['Builder-A'],
        root: null,
        repos: ['https://github.com/os/repo-a.git'],
      }, 'instance-a'),
    ).not.toThrow()

    expect(applyDefaults(readRemembered('instance-a'), catalog(), roots(['C:\\teams']), [])).toEqual({
      name: 'Team-1',
      managerAgent: null,
      memberAgents: null,
      root: null,
      repos: [],
      repoSuggestions: [],
      repoSuggestionsScope: 'instance',
    })
  })
})

describe('recent repositories are kept per instance', () => {
  const values = (repos: string[]) => ({
    managerAgent: 'Manager-A',
    memberAgents: ['Builder-A'],
    root: null,
    repos,
  })

  beforeEach(() => {
    const items = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => items.get(key) ?? null,
      setItem: (key: string, value: string) => void items.set(key, String(value)),
      removeItem: (key: string) => void items.delete(key),
    })
  })

  it('does not offer repositories saved under another instance at the same address', () => {
    remember(values(['https://github.com/os/old-install.git']), 'instance-a')

    const defaults = applyDefaults(readRemembered('instance-b'), catalog(), roots(['C:\\teams']), [])

    expect(defaults.repoSuggestions).toEqual([])
    expect(defaults.repoSuggestionsScope).toBe('instance')
  })

  it('offers repositories saved under the same instance', () => {
    remember(values(['https://github.com/os/repo-a.git']), 'instance-a')
    remember(values(['https://github.com/os/repo-b.git']), 'instance-a')

    const defaults = applyDefaults(readRemembered('instance-a'), catalog(), roots(['C:\\teams']), [])

    expect(defaults.repoSuggestions).toEqual([
      'https://github.com/os/repo-b.git',
      'https://github.com/os/repo-a.git',
    ])
    expect(defaults.repoSuggestionsScope).toBe('instance')
  })

  it('does not offer repositories saved before the list was kept per instance', () => {
    localStorage.setItem('harness.newTeamDefaults', JSON.stringify({
      managerAgent: 'Manager-A',
      memberAgents: ['Builder-A'],
      root: null,
      repos: ['https://github.com/os/unkeyed.git'],
    }))

    expect(applyDefaults(readRemembered('instance-a'), catalog(), roots(['C:\\teams']), []).repoSuggestions)
      .toEqual([])
    expect(applyDefaults(readRemembered(null), catalog(), roots(['C:\\teams']), []).repoSuggestions)
      .toEqual([])

    // The agent choices beside it are still remembered: only the repositories were address-wide.
    expect(readRemembered('instance-a')?.managerAgent).toBe('Manager-A')
  })

  it('drops the unkeyed repositories on the next save', () => {
    localStorage.setItem('harness.newTeamDefaults', JSON.stringify({
      managerAgent: 'Manager-A',
      memberAgents: ['Builder-A'],
      root: null,
      repos: ['https://github.com/os/unkeyed.git'],
    }))

    remember(values(['https://github.com/os/repo-a.git']), 'instance-a')

    expect(localStorage.getItem('harness.newTeamDefaults')).not.toContain('unkeyed')
    expect(readRemembered('instance-a')?.repos).toEqual(['https://github.com/os/repo-a.git'])
  })

  it('falls back to the repositories used in this browser, labelled so, when the instance cannot be told', () => {
    remember(values(['https://github.com/os/repo-a.git']), 'instance-a')

    const defaults = applyDefaults(readRemembered(null), catalog(), roots(['C:\\teams']), [])

    expect(defaults.repoSuggestions).toEqual(['https://github.com/os/repo-a.git'])
    expect(defaults.repoSuggestionsScope).toBe('browser')
  })
})
