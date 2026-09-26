import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useSessionStore } from '../session';

describe('the session store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('takes the stored user from an email change, not the value that was typed', async () => {
    // The server normalises an address - trimmed and lowercased - so echoing the typed text back
    // would leave the name in the corner reading as something the next login would not accept.
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ id: '1', email: 'dana@example.test' }), { status: 200 }),
    ))

    const session = useSessionStore()

    await session.changeEmail('dana@example.com', '  Dana@Example.TEST ')

    expect(session.user?.email).toBe('dana@example.test')
  })

  it('leaves the signed-in user alone when a credential change is refused', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ error: 'That is not your current password.' }), { status: 403 })))

    const session = useSessionStore()
    session.user = { id: '1', email: 'dana@example.com' }

    // A mistyped current password must not disturb the session. It is a 403 rather than a 401 for
    // the same reason: the caller is still signed in.
    await expect(session.changePassword('nope', 'a longer secret'))
      .rejects.toThrow('That is not your current password.')

    expect(session.user?.email).toBe('dana@example.com')
  })

  it('reports a password change that answered 204 as success rather than a parse failure', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    const session = useSessionStore()
    session.user = { id: '1', email: 'dana@example.com' }

    // An empty body is what 204 means. Parsing it as JSON throws, which would surface a change
    // that DID happen as an error - and invite someone to try it again.
    await expect(session.changePassword('correct horse', 'a longer secret')).resolves.toBeUndefined()
  })

  it('reports that a fresh instance needs a first account', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ needsAdmin: true }), { status: 200 }),
    ))

    const session = await refreshed()

    expect(session.needsFirstAccount).toBe(true)
    expect(session.user).toBeNull()
  })

  it('holds the signed-in user once /me answers', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ needsAdmin: false }), { status: 200 }))
      .mockResolvedValueOnce(new Response(
        JSON.stringify({ id: '1', email: 'dana@example.com' }), { status: 200 })))

    const session = await refreshed()

    expect(session.user?.email).toBe('dana@example.com')
  })

  it('treats a 401 from /me as signed out rather than as an error', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ needsAdmin: false }), { status: 200 }))
      .mockResolvedValueOnce(new Response('', { status: 401 })))

    const session = await refreshed()

    // Being signed out is the ordinary state of a first page load, not a failure to report.
    expect(session.user).toBeNull()
    expect(session.checked).toBe(true)
  })

  it('rethrows a real failure from /me rather than reporting signed out', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ needsAdmin: false }), { status: 200 }))
      .mockResolvedValueOnce(new Response('', { status: 500 })))

    const session = useSessionStore()

    // A broken backend must not masquerade as "please log in". Only a 401 means signed out.
    await expect(session.refresh()).rejects.toThrow()
    expect(session.user).toBeNull()
  })

  it('stops offering to create the first account as soon as the account exists', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(new Response('{}', { status: 200 }))
      .mockResolvedValueOnce(new Response('', { status: 500 })))

    const session = useSessionStore()
    session.needsFirstAccount = true

    // The create succeeded and the sign-in did not. Leaving needsFirstAccount true would show the person
    // "Create the first account" for an account that now exists, and their next attempt would 409
    // with no way forward but a reload.
    await expect(session.createFirstAccount('dana@example.com', 'correct horse')).rejects.toThrow()
    expect(session.needsFirstAccount).toBe(false)
  })

  async function refreshed() {
    const session = useSessionStore()
    await session.refresh()
    return session
  }
})
