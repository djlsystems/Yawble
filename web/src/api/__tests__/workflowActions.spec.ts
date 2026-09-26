import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { closeWorkflow, nudgeWorkflow, stopWorkflow } from '../client'
import { asTeamId } from '../types'

/**
 * `POST .../workflows/{correlation}/close|nudge|stop` all answer `Results.NoContent()` - a 204 with
 * an EMPTY body, same shape as `PUT /api/agents` (`saveAgents`, see `agents.spec.ts`'s own comment)
 * and `DELETE /api/teams/{team}/triggers/{id}`. `json<T>()` calls `response.json()`
 * unconditionally, and parsing an empty body throws a `SyntaxError` - so a caller built on
 * `json<void>()` here would report every SUCCESSFUL close, nudge or stop as a failure while the
 * row was written, exactly as `saveAgents` did before it was rebuilt on `send()`.
 *
 * `closeWorkflow`, `nudgeWorkflow` and `stopWorkflow` are one word apart from that mistake at their
 * own call sites (`send` vs `json`), and nothing but `vue-tsc` and a manual browser session stood
 * between that edit and production until this file existed - `vue-tsc` cannot see it because both
 * helpers type-check, and every OTHER suite in this repository stubs `fetch` to answer a body,
 * which a real 204 never carries. This is the real `fetch`/`send` path, following
 * `agents.spec.ts` and `deleteMember.spec.ts`'s own 204-handling tests.
 */
describe('closeWorkflow', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  it('accepts a 204 with an empty body without trying to parse it', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    await expect(
      closeWorkflow(asTeamId('Beta'), 11, 'closed as undeclared · last active 22h ago'),
    ).resolves.toBeUndefined()
  })

  it('posts the reason to the correlation-scoped close route', async () => {
    const fetched = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetched)

    await closeWorkflow(asTeamId('Beta Team'), 11, 'done for now')

    expect(fetched).toHaveBeenCalledWith(
      '/api/teams/Beta%20Team/workflows/11/close',
      expect.objectContaining({ method: 'POST' }),
    )

    const body = JSON.parse((fetched.mock.calls[0]![1] as RequestInit).body as string)
    expect(body).toEqual({ reason: 'done for now' })
  })

  it('surfaces the server’s own refusal wording', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({ error: 'No workflow with correlation 999.' }),
          { status: 404, headers: { 'content-type': 'application/json' } },
        ),
      ),
    )

    await expect(closeWorkflow(asTeamId('Beta'), 999)).rejects.toThrow(/No workflow with correlation/)
  })
})

describe('nudgeWorkflow', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  it('accepts a 204 with an empty body without trying to parse it', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    await expect(nudgeWorkflow(asTeamId('Beta'), 4)).resolves.toBeNull()
  })

  /**
   * A PAUSED TEAM ANSWERS 200 AND THE NOTICE HAS TO SURVIVE THE CLIENT.
   *
   * The server composes `pauseNotice` - the instruction was logged and runs after resume - and
   * this function must return it rather than `void`, or it is read and thrown away. On the board
   * that reads as a nudge that woke the Manager: the dialog closes, nothing moves, and the person has no way
   * to see the queue for themselves. A 200, a row, and nothing happening.
   */
  it('hands back the pause notice when the team is paused', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ paused: true, pauseNotice: "Team 'Beta' is paused." }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    ))

    await expect(nudgeWorkflow(asTeamId('Beta'), 4)).resolves.toBe("Team 'Beta' is paused.")
  })

  /** A 200 that carries no notice is not a notice reading "undefined". */
  it('answers null for a 200 with no notice in it', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ paused: false }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    ))

    await expect(nudgeWorkflow(asTeamId('Beta'), 4)).resolves.toBeNull()
  })

  it('posts to the correlation-scoped nudge route', async () => {
    const fetched = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetched)

    await nudgeWorkflow(asTeamId('Beta Team'), 4)

    expect(fetched).toHaveBeenCalledWith(
      '/api/teams/Beta%20Team/workflows/4/nudge',
      expect.objectContaining({ method: 'POST' }),
    )
  })
})

describe('stopWorkflow', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  it('accepts a 204 with an empty body without trying to parse it', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    await expect(stopWorkflow(asTeamId('Beta'), 4)).resolves.toBeUndefined()
  })

  it('posts to the correlation-scoped stop route', async () => {
    const fetched = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetched)

    await stopWorkflow(asTeamId('Beta Team'), 4)

    expect(fetched).toHaveBeenCalledWith(
      '/api/teams/Beta%20Team/workflows/4/stop',
      expect.objectContaining({ method: 'POST' }),
    )
  })
})
