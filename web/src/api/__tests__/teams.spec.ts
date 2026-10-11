import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createTeam, setMemberAgents, setTeamAdditionalInstructions } from '../client'
import { asTeamId } from '../types'

/**
 * What a team is created with, and the words a person adds for it.
 *
 * Stubs `fetch` and exercises the real `send`/`json` helpers, following members.spec.ts.
 */
describe('a team', () => {
  let fetcher: ReturnType<typeof vi.fn>

  beforeEach(() => {
    fetcher = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ id: 'Alpha', name: 'Alpha' }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    vi.stubGlobal('fetch', fetcher)
  })

  afterEach(() => vi.unstubAllGlobals())

  const bodyOf = () => JSON.parse((fetcher.mock.calls[0]?.[1] as RequestInit).body as string)

  /** THE ID, never the name. A team's two names are equal until it is relabelled, so addressing by
   *  the wrong one works exactly once and 404s forever after. */
  it('puts Additional instructions to the team by its identifier, encoded', async () => {
    await setTeamAdditionalInstructions(asTeamId('Team One'), '  Prefer small commits.  ')

    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/teams/Team%20One/additional-instructions')
    expect((fetcher.mock.calls[0]?.[1] as RequestInit).method).toBe('PUT')
    expect(bodyOf()).toEqual({ additionalInstructions: 'Prefer small commits.' })
  })

  it('clears Additional instructions with null when the box is emptied', async () => {
    await setTeamAdditionalInstructions(asTeamId('Alpha'), '   ')

    expect(bodyOf()).toEqual({ additionalInstructions: null })
  })

  /** A team is created knowing what its Manager and new members run - and NO prompt: what either is
   *  told is the built-in role prompt.
   *
   *  AND IT SENDS NO CONCIERGE. The Concierge is a tenant setting; a create that carried one would
   *  repoint the Concierge for every team that already existed.
   *  `toEqual` is what pins this: an extra key fails it, so a reinstated field cannot pass. */
  it('creates a team carrying what its Manager and new members run, and no prompt', async () => {
    await createTeam('Alpha', 'Manager', ['echo'])

    expect(bodyOf()).toEqual({
      name: 'Alpha',
      agent: 'Manager',
      memberAgents: ['echo'],
    })
  })

  it('sends Additional instructions inside the create, trimmed', async () => {
    await createTeam('Alpha', 'Manager', ['echo'], undefined, [], null, '  Be brief.  ')

    expect(bodyOf().additionalInstructions).toBe('Be brief.')
  })

  // In the create itself, keyed by the repository URL, so the clone is made with its
  // upstream remote; omitted when no repository has one, so an owned team's body is unchanged.
  it('sends each repository\'s upstream inside the create', async () => {
    const fork = 'https://github.com/fork-owner/Widget.git'
    await createTeam('Alpha', 'Manager', ['echo'], undefined, [fork], null, undefined, {
      [fork]: 'https://github.com/project/Widget.git',
    })
    expect(bodyOf().upstreams).toEqual({ [fork]: 'https://github.com/project/Widget.git' })
  })

  it('sends no upstreams when no repository has one', async () => {
    await createTeam('Alpha', 'Manager', ['echo'], undefined, ['https://github.com/o/W.git'], null, undefined, {})
    expect('upstreams' in bodyOf()).toBe(false)
  })

  it('omits blank Additional instructions, so nothing is appended', async () => {
    await createTeam('Alpha', 'Manager', ['echo'], undefined, [], null, '   ')

    expect('additionalInstructions' in bodyOf()).toBe(false)
  })

  /**
   * `root` is what lets a team follow the instance root if it is ever moved: omitted means NULL,
   * not an empty string, and NULL is what the server treats as "never pinned". Sending `root: ""`
   * or `root: null` would be a second, client-side answer to what "unset" means, and would pin the
   * team to whatever the server resolves an empty value to instead of leaving it unpinned.
   */
  it('omits the root entirely when the caller leaves it untouched', async () => {
    await createTeam('Alpha', 'Manager', ['echo'])

    const body = bodyOf()
    expect('root' in body).toBe(false)
  })

  it('puts member allowlist updates by team id', async () => {
    await setMemberAgents(asTeamId('PlatformEngineering'), ['copilot-headless', 'claude-headless'])

    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/teams/PlatformEngineering/member-agent')
    expect(bodyOf()).toEqual({ agents: ['copilot-headless', 'claude-headless'] })
  })

  it('encodes the team id for member allowlist updates', async () => {
    await setMemberAgents(asTeamId('Team One'), ['echo'])

    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/teams/Team%20One/member-agent')
  })

  it('sends the root the caller supplies', async () => {
    await createTeam(
      'Alpha', 'Manager', ['echo'],
      'C:\\data\\projects',
    )

    expect(bodyOf().root).toBe('C:\\data\\projects')
  })
})
