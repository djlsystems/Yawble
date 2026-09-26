import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { addMember } from '../client'
import { asTeamId } from '../types';

// Stubs `fetch` and exercises the real `send`/`json` helpers, following users.spec.ts.
describe('addMember', () => {
  let fetcher: ReturnType<typeof vi.fn>

  beforeEach(() => {
    fetcher = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ team: 'Alpha', id: 'dev1', name: 'dev1' }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    vi.stubGlobal('fetch', fetcher)
  })

  afterEach(() => vi.unstubAllGlobals())

  const bodyOf = () => JSON.parse((fetcher.mock.calls[0]?.[1] as RequestInit).body as string)

  /**
   * The name goes up as the LABEL a person typed — spaces and all — and the identifier is derived
   * server-side. "Data Ingest" becomes `DataIngest` on disk and nobody is asked to know that.
   *
   * Not trimmed here, matching `createTeam`: the dialog trims before calling, and the server trims
   * again. Trimming in a third place would be a rule with no owner.
   */
  it('posts the name a person typed, leaving the identifier to the server', async () => {
    await addMember(asTeamId('Alpha'), 'Data Ingest', 'echo')

    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/teams/Alpha/containers')
    expect(bodyOf()).toMatchObject({ name: 'Data Ingest', agent: 'echo' })
  })

  it('encodes the team, so a name needing it cannot break the path', async () => {
    await addMember(asTeamId('Team One'), 'dev1', 'echo')

    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/teams/Team%20One/containers')
  })

  /**
   * THE point of this file.
   *
   * Completion types are global: a member subscribed to one wakes on every container's work on its
   * team, and two of them wake each other without end with nothing in the running system bounding
   * it. A worker is already reachable without any subscription, because its own addressed-instruction
   * type is appended for it.
   *
   * So the UI must never send this field. Adding a subscriptions box to the dialog would put that
   * runaway one keystroke from someone with no way to know it existed — and every other assertion
   * here would stay green.
   */
  it('never sends subscriptions', async () => {
    await addMember(asTeamId('Alpha'), 'dev1', 'echo')

    expect(bodyOf()).not.toHaveProperty('subscribes')
  })

  /** A member is told the built-in Member prompt by role, so no prompt of any kind is sent. */
  it('sends a name and an agent and no prompt', async () => {
    await addMember(asTeamId('Alpha'), 'dev1', 'claude')

    expect(bodyOf()).toEqual({ name: 'dev1', agent: 'claude' })
  })

  it('surfaces the server’s own refusal wording', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ error: "A member called 'dev1' already exists in this team." }), {
          status: 409,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    )

    await expect(addMember(asTeamId('Alpha'), 'dev1', 'echo')).rejects.toThrow(/already exists/)
  })
})
