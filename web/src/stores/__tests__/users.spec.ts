import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '../../api/client'

// Stubs `fetch` and exercises the real `send`/`json` helpers, following `session.spec.ts`'s
// idiom rather than `board.spec.ts`'s (which mocks the client module) - that is what actually
// exercises the 204 handling and the error-body unwrapping instead of mocking them away.
//
// This lives beside the other store specs rather than folded into `session.spec.ts` because it
// is not a session concern: there is no users store yet, and these calls are exercised directly
// off `api/client.ts`, same as the brief's given test does.
//
// THERE IS NO TIER. Every person is an administrator, so a row is an id and an email, a create is
// an email and a password, and there is no update call at all - `PATCH /api/users/{id}` went with
// the tier.
describe('the user administration calls', () => {
  beforeEach(() => {
    vi.unstubAllGlobals()
  })

  it('lists users as id and email - there is no per-team access list and no tier to read', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify([{ id: '1', email: 'bob@example.com' }]),
        { status: 200 },
      ),
    ))

    const users = await api.listUsers()

    expect(users.map((user) => [user.id, user.email])).toEqual([['1', 'bob@example.com']])
  })

  it('creates with email and password only', async () => {
    const fetcher = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ id: '1', email: 'bob@example.com' }), { status: 200 }),
    )
    vi.stubGlobal('fetch', fetcher)

    await api.createUser('bob@example.com', 'password1')

    const body = JSON.parse((fetcher.mock.calls[0]?.[1] as RequestInit).body as string)

    expect(body).toEqual({ email: 'bob@example.com', password: 'password1' })
  })

  it('has no update call, because an account has nothing left to edit but its password', () => {
    expect('updateUser' in api).toBe(false)
  })

  it('surfaces the refusal when the last account would be deleted', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ error: 'The last account cannot be deleted.' }),
        { status: 409 },
      ),
    ))

    await expect(api.deleteUser('1')).rejects.toThrow('The last account cannot be deleted.')
  })

  it('accepts a 204 from delete without trying to parse a body', async () => {
    // `json()` on an empty body throws a SyntaxError, which would surface a successful
    // delete as a parse failure. This is why `send` exists separately.
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    await expect(api.deleteUser('1')).resolves.toBeUndefined()
  })

  it('accepts a 204 from a password reset', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    await expect(api.resetPassword('1', 'password1')).resolves.toBeUndefined()
  })
})
