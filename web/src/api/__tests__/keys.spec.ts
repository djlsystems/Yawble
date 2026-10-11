import { afterEach, describe, expect, it, vi } from 'vitest'
import { mintKey, listKeys, listAllKeys, revokeKey } from '../client'

afterEach(() => {
  vi.unstubAllGlobals()
})

/**
 * `DELETE /api/keys/{id}` answers 204. `json<T>()` calls `response.json()` unconditionally, and
 * `response.json()` on an empty body throws a SyntaxError - so a `revokeKey` built on `json<T>()`
 * would report every SUCCESSFUL revocation as a failure while the key was actually destroyed. This
 * exact bug has already shipped once in this codebase, on `PUT /api/agents` - see
 * `agents.spec.ts`. `revokeKey` must use `send()`.
 */
describe('api keys', () => {
  it('mints with the label in the body', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          id: 'apikey-1',
          label: 'laptop',
          prefix: 'osk_abcd',
          createdAt: '2026-08-23T00:00:00Z',
          credential: 'secret',
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const minted = await mintKey('laptop')

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/keys',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ label: 'laptop' }) }),
    )
    expect(minted.credential).toBe('secret')
  })

  it('revokes through send, so a 204 is not read as a failure', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(revokeKey('apikey-1')).resolves.toBeUndefined()

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/keys/apikey-1',
      expect.objectContaining({ method: 'DELETE' }),
    )
  })

  it('encodes an id that is not URL-safe', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    await revokeKey('Alpha/Manager')

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/keys/Alpha%2FManager',
      expect.objectContaining({ method: 'DELETE' }),
    )
  })

  it('reads your own keys and the tenant roster from different paths', async () => {
    // A real Response's body can only be read once, so a fresh instance is built per call rather
    // than reusing one across both `listKeys` and `listAllKeys`.
    const fetchMock = vi
      .fn()
      .mockImplementation(() => Promise.resolve(new Response(JSON.stringify([]), { status: 200 })))
    vi.stubGlobal('fetch', fetchMock)

    await listKeys()
    await listAllKeys()

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/keys')
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/admin/keys')
  })

  it('surfaces the server refusal rather than a bare status', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ error: 'Name this key.' }), {
        status: 400,
        statusText: 'Bad Request',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(mintKey('  ')).rejects.toThrow('Name this key.')
  })
})
