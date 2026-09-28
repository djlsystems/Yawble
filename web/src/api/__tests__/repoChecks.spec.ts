import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createTeam, createTeamLocalRepo, repoCheckRefusal, setTeamRepos } from '../client'
import { asTeamId } from '../types'

/**
 * B001F ON THE WIRE: `localRepository` and `repoChoices` on a create, the attach's two body shapes,
 * the team's local repository, and a 422 read back as a refused repository check. Stubs `fetch` and
 * exercises the real `send`/`json`, so the shapes are the contract's and not a mock's.
 */
describe('forgiving team repositories', () => {
  let fetcher: ReturnType<typeof vi.fn>

  const answer = (status: number, body: unknown) =>
    new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })

  beforeEach(() => {
    fetcher = vi.fn().mockImplementation(async () => answer(200, { id: 'Alpha', name: 'Alpha', repos: [] }))
    vi.stubGlobal('fetch', fetcher)
  })

  afterEach(() => vi.unstubAllGlobals())

  const call = () => fetcher.mock.calls[0] as [string, RequestInit]
  const bodyOf = () => JSON.parse(call()[1].body as string)

  it('sends localRepository: false when a person opts out', async () => {
    await createTeam('Alpha', 'Manager', ['echo'], undefined, [], null, undefined, undefined, false)
    expect(bodyOf().localRepository).toBe(false)
  })

  it('omits localRepository and repoChoices when not given', async () => {
    await createTeam('Alpha', 'Manager', ['echo'], undefined, ['https://github.com/o/W.git'], null, undefined, undefined, undefined, {})
    expect('localRepository' in bodyOf()).toBe(false)
    expect('repoChoices' in bodyOf()).toBe(false)
  })

  it('sends repoChoices keyed by the URL as listed', async () => {
    const url = 'https://github.com/acme/job-tracker'
    await createTeam('Alpha', 'Manager', ['echo'], undefined, [url], null, undefined, undefined, undefined, { [url]: 'create-on-github' })
    expect(bodyOf().repos).toEqual([url])
    expect(bodyOf().repoChoices).toEqual({ [url]: 'create-on-github' })
  })

  it('attaches with the bare array, and with { repos, repoChoices } to answer a refusal', async () => {
    const url = 'https://github.com/acme/new'
    await setTeamRepos(asTeamId('Alpha'), [url])
    expect(call()[0]).toBe('/api/teams/Alpha/repos')
    expect(call()[1].method).toBe('PUT')
    expect(bodyOf()).toEqual([url])

    fetcher.mockClear()
    await setTeamRepos(asTeamId('Alpha'), [url], { [url]: 'attach-anyway' })
    expect(bodyOf()).toEqual({ repos: [url], repoChoices: { [url]: 'attach-anyway' } })
  })

  it('creates the team\'s local repository with a bodiless POST to its route', async () => {
    fetcher.mockResolvedValueOnce(answer(201, {
      team: { id: 'Team One', repos: ['local:Team-One'] },
      localRepository: { name: 'Team-One', reference: 'local:Team-One', created: true },
    }))

    const made = await createTeamLocalRepo(asTeamId('Team One'))

    expect(call()[0]).toBe('/api/teams/Team%20One/local-repo')
    expect(call()[1].method).toBe('POST')
    expect(call()[1].body).toBeUndefined()
    expect(made.localRepository.reference).toBe('local:Team-One')
  })

  it('reads a 422 repo-check-failed back as the refusal, and nothing else as one', async () => {
    const refusal = {
      error: 'https://github.com/acme/job-tracker could not be read: remote: Repository not found. Nothing was created.',
      code: 'repo-check-failed',
      repos: [{ url: 'https://github.com/acme/job-tracker', failure: 'not-found', reason: 'remote: Repository not found.', choices: ['create-on-github', 'use-local'] }],
    }
    fetcher.mockResolvedValueOnce(answer(422, refusal))
    const refused = await createTeam('Alpha', 'Manager', ['echo'], undefined, [refusal.repos[0]!.url]).catch((cause: unknown) => cause)
    expect(repoCheckRefusal(refused)).toEqual(refusal)

    fetcher.mockResolvedValueOnce(answer(400, { error: 'A choice for a URL that is not listed.', code: 'repo-check-failed', repos: [] }))
    const badRequest = await createTeam('Alpha', 'Manager', ['echo']).catch((cause: unknown) => cause)
    expect(repoCheckRefusal(badRequest)).toBeNull()

    fetcher.mockResolvedValueOnce(answer(422, { error: 'Something else.' }))
    const other = await createTeam('Alpha', 'Manager', ['echo']).catch((cause: unknown) => cause)
    expect(repoCheckRefusal(other)).toBeNull()
  })
})
