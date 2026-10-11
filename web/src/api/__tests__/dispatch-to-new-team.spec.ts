import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { dispatchBacklogItemToNewTeam } from '../client'

/**
 * THE REQUEST THIS SENDS, ASSERTED ON THE WIRE.
 *
 * THE BODY IS JSON AND THE REQUEST MUST SAY SO. Without a `content-type`, ASP.NET's model binder
 * refuses it with **415** before the handler runs - and a 415 carries no `error` field, so `send`'s
 * refusal path would have nothing better to show than the bare status.
 *
 * WHY THE SERVER SUITE CANNOT SEE IT. The host tests drive this route through `PostAsJsonAsync`,
 * which sets `content-type` itself, so they pass whether or not the browser's call could succeed.
 * A fixture that cannot tell two things apart cannot catch confusing them, and the fixture there
 * is the HTTP client.
 */
describe('dispatchBacklogItemToNewTeam', () => {
  const fetchMock = vi.fn()

  const settings = {
    agent: 'claude-headless',
    memberAgents: ['claude-headless'],
    root: null,
    repos: [],
  }

  beforeEach(() => {
    fetchMock.mockReset()
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ team: 'b2-guard', correlation: 1, dispatch: 1 }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('declares that its body is JSON', async () => {
    await dispatchBacklogItemToNewTeam(2, 'b2-guard', settings)

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]

    expect(new Headers(init.headers).get('content-type')).toBe('application/json')
  })

  /**
   * NO TEAM IN THE PATH, which is the whole point of the redesign: the earlier version named a
   * SOURCE team to clone and therefore could not run at all on an instance with no teams - exactly
   * where somebody is most likely to be starting a spec.
   */
  it('posts to a path that names no team', async () => {
    await dispatchBacklogItemToNewTeam(2, 'b2-guard', settings)

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]

    expect(url).toBe('/api/backlog/2/dispatch-to-new')
    expect(init.method).toBe('POST')
  })

  it('sends the name and the remembered settings together', async () => {
    await dispatchBacklogItemToNewTeam(7, 'b7-thing', settings)

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]

    expect(JSON.parse(init.body as string)).toEqual({ name: 'b7-thing', ...settings })
  })

  /**
   * A NULL AGENT IS SENT AS NULL rather than dropped. The route refuses it BY NAME, and that
   * refusal is the honest answer for a browser with nothing remembered - a client that omitted the
   * field would get the same refusal with less to say about why.
   */
  it('sends a null agent rather than omitting it', async () => {
    await dispatchBacklogItemToNewTeam(7, 'b7-thing', { ...settings, agent: null })

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string) as Record<string, unknown>

    expect('agent' in body).toBe(true)
    expect(body.agent).toBeNull()
  })
})
