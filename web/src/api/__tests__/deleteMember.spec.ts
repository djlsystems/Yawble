import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { deleteMember } from '../client'
import { asMemberId, asTeamId } from '../types'

/**
 * `DELETE /api/teams/{team}/containers/{name}` answers 200 with WHAT IT REMOVED, not 204 - so that
 * a workspace it could not delete is named rather than swallowed. That is the whole reason this is
 * built on `json<T>()` while `saveAgents` beside it had to be rebuilt on `send()`, and it is worth
 * a test on the real `fetch` path for the same reason `agents.spec.ts` is: the two helpers are one
 * word apart at the call site, the wrong one throws only against the body the server actually
 * sends, and 116 green tests once said nothing about exactly this mistake.
 *
 * The URL is asserted as well as the parse, because both halves of it are a name-versus-identifier
 * trap: a team addressed by its NAME 404s the moment somebody renames it (a team's name equals its
 * id until then, so the bug hides through the first rename), and a member addressed by its editable
 * label does the same the moment somebody renames one.
 */
describe('deleteMember', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  const removed = {
    team: 'Beta',
    member: 'Digger',
    label: 'Digger',
    pendingDeliveries: 2,
    directories: ['/data/workspaces/Beta/Digger'],
    failures: [],
  }

  it('reads back what the deletion removed', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify(removed), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    )

    await expect(deleteMember(asTeamId('Beta'), asMemberId('Digger'))).resolves.toMatchObject({
      member: 'Digger',
      pendingDeliveries: 2,
    })
  })

  it('addresses the team and the member by identifier, and encodes both', async () => {
    const fetched = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(removed), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    vi.stubGlobal('fetch', fetched)

    await deleteMember(asTeamId('Beta Team'), asMemberId('Digger One'))

    expect(fetched).toHaveBeenCalledWith(
      '/api/teams/Beta%20Team/containers/Digger%20One',
      expect.objectContaining({ method: 'DELETE' }),
    )
  })

  /**
   * A manager is a 409, and the members list does not offer the control at all - so this asserts the
   * backstop rather than the path anyone takes. The server's own wording is what surfaces, because
   * a refusal rewritten here would drift from the one the API documents.
   */
  it('surfaces the server’s refusal for a manager', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            error:
              "A team's manager cannot be deleted. Delete the team 'Beta Team' itself to remove it.",
          }),
          { status: 409, headers: { 'content-type': 'application/json' } },
        ),
      ),
    )

    await expect(deleteMember(asTeamId('Beta'), asMemberId('Manager'))).rejects.toThrow(
      /manager cannot be deleted/,
    )
  })
})
