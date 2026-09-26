import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  createSchedule,
  deleteSchedule,
  listSchedules,
  updateSchedule,
} from '../client'
import { asTeamId } from '../types'

describe('team schedules API client', () => {
  let fetcher: ReturnType<typeof vi.fn>

  beforeEach(() => {
    fetcher = vi.fn().mockResolvedValue(
      new Response(JSON.stringify([]), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    vi.stubGlobal('fetch', fetcher)
  })

  afterEach(() => vi.unstubAllGlobals())

  it('lists schedules by team identifier', async () => {
    await listSchedules(asTeamId('Alpha Team'))
    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/teams/Alpha%20Team/triggers')
  })

  it('creates a schedule with the supplied request shape', async () => {
    await createSchedule(asTeamId('Alpha'), {
      name: 'Morning standup',
      container: 'Manager',
      instruction: 'Post update.',
      kind: 'cron',
      expression: '0 0 9 * * 1-5',
      timezone: 'Europe/London',
      idleOnly: true,
      enabled: true,
    })

    const request = fetcher.mock.calls[0]?.[1] as RequestInit
    expect(request.method).toBe('POST')
    expect(JSON.parse(request.body as string)).toMatchObject({
      kind: 'cron',
      timezone: 'Europe/London',
    })
  })

  it('sends PATCH bodies verbatim for tri-state semantics', async () => {
    await updateSchedule(asTeamId('Alpha'), 'abc123', {
      kind: 'every',
      expression: null,
      timezone: null,
      intervalSeconds: 300,
    })

    const call = fetcher.mock.calls[0]
    expect(call?.[0]).toBe('/api/teams/Alpha/triggers/abc123')
    expect((call?.[1] as RequestInit).method).toBe('PATCH')
    expect(JSON.parse((call?.[1] as RequestInit).body as string)).toEqual({
      kind: 'every',
      expression: null,
      timezone: null,
      intervalSeconds: 300,
    })
  })

  it('deletes with send() semantics for the route 204', async () => {
    fetcher.mockResolvedValueOnce(new Response(null, { status: 204 }))
    await deleteSchedule(asTeamId('Alpha'), 'abc123')
    expect((fetcher.mock.calls[0]?.[1] as RequestInit).method).toBe('DELETE')
  })
})
