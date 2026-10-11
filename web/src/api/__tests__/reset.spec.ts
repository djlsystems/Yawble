import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resetTeam } from '../client'
import { asTeamId } from '../types'

/**
 * The contract, over the real `fetch`/`json` path rather than a component's source text.
 *
 * `resetTeam` uses `json<T>()` and not `send()` on purpose: the route answers 200 with what it did,
 * precisely so a caller can report a directory it could not empty and a message it could not purge.
 * A `send()` here would discard the half the dialog exists to show.
 */
describe('resetTeam', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  it('posts to the named team and reads the body back', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ team: 'Alpha', floored: ['Manager'], purged: 0, retained: 2 }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await resetTeam(asTeamId('Alpha'), { members: ['Manager'] })

    expect(result.floored).toEqual(['Manager'])
    expect(result.retained).toBe(2)

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit]

    expect(path).toBe('/api/teams/Alpha/reset')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ members: ['Manager'] })
  })

  it('sends the body VERBATIM, so an absent flag stays absent', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    // The server reads an absent flag as false — except `forgetHistory`, whose absence means TRUE.
    // Filling in defaults here would put a second answer to "what does a reset do" in the browser.
    await resetTeam(asTeamId('Alpha'), { members: ['Manager'], purge: true })

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]

    expect(JSON.parse(init.body as string)).toEqual({ members: ['Manager'], purge: true })
  })

  it('encodes the team, so an identifier needing it cannot break the path', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await resetTeam(asTeamId('A B'), { members: [] })

    expect(fetchMock.mock.calls[0]![0]).toBe('/api/teams/A%20B/reset')
  })

  it('surfaces the server’s own refusal wording rather than a bare status', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({ error: "'Alpha' cannot be reset while Digger is still working." }),
          { status: 409, headers: { 'content-type': 'application/json' } },
        ),
      ),
    )

    // A 409 is the ordinary way to meet this feature when a member is busy, and the wording names
    // WHO. A bare "409" tells the person nothing they can act on.
    await expect(resetTeam(asTeamId('Alpha'), { members: ['Digger'] })).rejects.toThrow(
      /Digger is still working/,
    )
  })
})
