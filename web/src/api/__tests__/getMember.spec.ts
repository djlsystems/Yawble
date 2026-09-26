import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getMember } from '../client'
import { asMemberId, asTeamId } from '../types';

/**
 * `GET /api/teams/{team}/containers/{name}` - the stored row behind a member. Answers 200 with the
 * stored row, so this is `json<T>`, following the same shape `updateMember.spec.ts` pins for its
 * PATCH sibling.
 */
describe('getMember', () => {
  let fetcher: ReturnType<typeof vi.fn>

  afterEach(() => vi.unstubAllGlobals())

  it('GETs the member route, encoding both the team and the name', async () => {
    fetcher = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ team: 'Team One', id: 'dev 1', name: 'Dev One' }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetcher)

    await getMember(asTeamId('Team One'), asMemberId('dev 1'))

    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/teams/Team%20One/containers/dev%201')
    expect((fetcher.mock.calls[0]?.[1] as RequestInit | undefined)?.method ?? 'GET').toBe('GET')
  })

  it('resolves with the stored row verbatim', async () => {
    fetcher = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ team: 'Alpha', id: 'dev1', name: 'Dev One' }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetcher)

    await expect(getMember(asTeamId('Alpha'), asMemberId('dev1'))).resolves.toEqual({
      team: 'Alpha',
      id: 'dev1',
      name: 'Dev One',
    })
  })

  it('surfaces the server’s own refusal wording', async () => {
    fetcher = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ error: "No member 'dev1'." }), {
        status: 404,
        headers: { 'content-type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetcher)

    await expect(getMember(asTeamId('Alpha'), asMemberId('dev1'))).rejects.toThrow(/No member/)
  })
})
