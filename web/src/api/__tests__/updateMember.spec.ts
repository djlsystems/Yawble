import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { updateMember } from '../client'
import { asMemberId, asTeamId } from '../types';

/**
 * `PATCH /api/teams/{team}/containers/{name}` answers 200 with the updated snapshot - unlike
 * `saveAgents`'s `PUT /api/agents`, which answers 204 with an empty body. So this is built on
 * `json<T>`, not `send`, and this file's first test is what pins that: a `json<T>()` built on a
 * 204 throws trying to parse the empty body, exactly the failure mode `agents.spec.ts` documents
 * for `saveAgents`.
 */
describe('updateMember', () => {
  let fetcher: ReturnType<typeof vi.fn>

  beforeEach(() => {
    fetcher = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ team: 'Alpha', id: 'dev1', name: 'Dev One', agent: 'echo' }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    )

    vi.stubGlobal('fetch', fetcher)
  })

  afterEach(() => vi.unstubAllGlobals())

  const bodyOf = () => JSON.parse((fetcher.mock.calls[0]?.[1] as RequestInit).body as string)

  it('PATCHes the member route, encoding both the team and the name', async () => {
    await updateMember(asTeamId('Team One'), asMemberId('dev 1'), { name: 'Dev One' })

    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/teams/Team%20One/containers/dev%201')
    expect((fetcher.mock.calls[0]?.[1] as RequestInit).method).toBe('PATCH')
  })

  it('resolves with the updated snapshot, since the route answers 200 rather than 204', async () => {
    await expect(updateMember(asTeamId('Alpha'), asMemberId('dev1'), { name: 'Dev One' })).resolves.toMatchObject({
      name: 'Dev One',
    })
  })

  /** A key left undefined is dropped, so a label-only edit sends the label alone. */
  it('sends a label-only body as the label alone', async () => {
    await updateMember(asTeamId('Alpha'), asMemberId('dev1'), { name: 'Dev One' })

    expect(bodyOf()).toEqual({ name: 'Dev One' })
  })

  it('surfaces the server’s own refusal wording', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ error: "No member 'dev1'." }), {
          status: 404,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    )

    await expect(updateMember(asTeamId('Alpha'), asMemberId('dev1'), { name: 'x' })).rejects.toThrow(/No member/)
  })
})
