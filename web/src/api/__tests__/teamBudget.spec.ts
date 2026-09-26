import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createTeam, resumeWorkflow, setTeamWorkflowBudget } from '../client'
import { asTeamId } from '../types'

/**
 * THE THREE BUDGET CALLS, AT THE WIRE.
 *
 * `setTeamWorkflowBudget`, `resumeWorkflow`, and `createTeam`'s budget parameter. Each is one word
 * away from a mistake:
 *
 * - a budget sent as a FOLLOW-UP PUT rather than inside the create leaves a window where the team
 *   exists on a figure nobody chose, and loses the chosen one silently if the second call never
 *   lands;
 * - `resumeWorkflow` answers 204 OR a 200 carrying `pauseNotice` (the TEAM is paused, so the
 *   instruction was logged and will run after the team resumes). A caller built on `json<T>()`
 *   would report every successful resume as a parse failure;
 * - and 0 must survive the journey. Under the one-of rule a stored 0 means the team explicitly
 *   chose unlimited, where NULL means it has chosen nothing and the instance figure applies. A
 *   client that collapsed one into the other would turn "unlimited" into 100,000,000.
 */

const jsonResponse = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })

describe('setTeamWorkflowBudget', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  it('puts the figure to the team’s own budget route', async () => {
    const fetched = vi.fn().mockResolvedValue(jsonResponse({ id: 'beta-team', name: 'Beta Team' }))
    vi.stubGlobal('fetch', fetched)

    await setTeamWorkflowBudget(asTeamId('Beta Team'), 250_000_000)

    expect(fetched).toHaveBeenCalledWith(
      '/api/teams/Beta%20Team/budget',
      expect.objectContaining({ method: 'PUT' }),
    )

    const body = JSON.parse((fetched.mock.calls[0]![1] as RequestInit).body as string)
    expect(body).toEqual({ budgetTokens: 250_000_000 })
  })

  /** ZERO IS A CHOICE, NOT AN ABSENCE. Storing 0 as NULL would, under the one-of rule, silently
   *  turn a person's "unlimited" into the instance figure. */
  it('sends an explicit zero rather than dropping it', async () => {
    const fetched = vi.fn().mockResolvedValue(jsonResponse({ id: 'beta', name: 'Beta' }))
    vi.stubGlobal('fetch', fetched)

    await setTeamWorkflowBudget(asTeamId('beta'), 0)

    const body = JSON.parse((fetched.mock.calls[0]![1] as RequestInit).body as string)
    expect(body).toEqual({ budgetTokens: 0 })
  })

  /** NULL IS THE OTHER CHOICE: the team has chosen nothing and follows the instance figure. */
  it('sends null for a team that has chosen nothing', async () => {
    const fetched = vi.fn().mockResolvedValue(jsonResponse({ id: 'beta', name: 'Beta' }))
    vi.stubGlobal('fetch', fetched)

    await setTeamWorkflowBudget(asTeamId('beta'), null)

    const body = JSON.parse((fetched.mock.calls[0]![1] as RequestInit).body as string)
    expect(body).toEqual({ budgetTokens: null })
  })

  it('answers the whole team the route returns', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        jsonResponse({
          id: 'beta',
          name: 'Beta',
          budgetTokens: 250_000_000,
          effectiveWorkflowBudget: 250_000_000,
        }),
      ),
    )

    const team = await setTeamWorkflowBudget(asTeamId('beta'), 250_000_000)

    expect(team.budgetTokens).toBe(250_000_000)
    expect(team.effectiveWorkflowBudget).toBe(250_000_000)
  })

  it('surfaces the server’s own refusal wording', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        jsonResponse({ error: 'A workflow budget cannot be negative.' }, 400),
      ),
    )

    await expect(setTeamWorkflowBudget(asTeamId('beta'), -1)).rejects.toThrow(/cannot be negative/)
  })
})

describe('resumeWorkflow', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  it('posts to the correlation-scoped resume route', async () => {
    const fetched = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetched)

    await resumeWorkflow(asTeamId('Beta Team'), 1707)

    expect(fetched).toHaveBeenCalledWith(
      '/api/teams/Beta%20Team/workflows/1707/resume',
      expect.objectContaining({ method: 'POST' }),
    )
  })

  it('accepts a 204 with an empty body without trying to parse it', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    await expect(resumeWorkflow(asTeamId('beta'), 1707)).resolves.toBeNull()
  })

  /**
   * THE SECOND SHAPE, AND THE REASON THIS DOES NOT RETURN `void`. The WORKFLOW resumes, but the
   * TEAM is paused - so nothing runs until somebody resumes the team too. A caller that discarded
   * that would report a queued resume as a completed one, on the one screen where the person
   * cannot see the queue for themselves. `nudgeWorkflow` carries the identical pair.
   */
  it('returns the pause notice when the TEAM is paused', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        jsonResponse({ paused: true, pauseNotice: 'Beta is paused; this will run after it resumes.' }),
      ),
    )

    await expect(resumeWorkflow(asTeamId('beta'), 1707)).resolves.toBe(
      'Beta is paused; this will run after it resumes.',
    )
  })

  it('surfaces the server’s own refusal wording', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(jsonResponse({ error: 'No workflow with correlation 999.' }, 404)),
    )

    await expect(resumeWorkflow(asTeamId('beta'), 999)).rejects.toThrow(/No workflow with correlation/)
  })
})

describe('createTeam carries the budget', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  /**
   * IN THE SAME REQUEST AS THE CREATE, never a follow-up PUT: a second call leaves a window where
   * the team exists on a figure nobody chose.
   */
  it('sends budgetTokens inside the create, not after it', async () => {
    const fetched = vi.fn().mockResolvedValue(jsonResponse({ id: 'beta', name: 'Beta' }))
    vi.stubGlobal('fetch', fetched)

    await createTeam(
      'Beta',
      'claude-headless',
      ['claude-headless'],
      undefined,
      [],
      250_000_000,
    )

    expect(fetched).toHaveBeenCalledOnce()
    expect(fetched.mock.calls[0]![0]).toBe('/api/teams')

    const body = JSON.parse((fetched.mock.calls[0]![1] as RequestInit).body as string)
    expect(body.budgetTokens).toBe(250_000_000)
  })

  /** An explicit 0 means the team chose unlimited and must reach the server as 0. */
  it('sends an explicit zero', async () => {
    const fetched = vi.fn().mockResolvedValue(jsonResponse({ id: 'beta', name: 'Beta' }))
    vi.stubGlobal('fetch', fetched)

    await createTeam('Beta', 'claude-headless', ['claude-headless'], undefined, [], 0)

    const body = JSON.parse((fetched.mock.calls[0]![1] as RequestInit).body as string)
    expect(body.budgetTokens).toBe(0)
  })
})
