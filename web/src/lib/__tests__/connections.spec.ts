import { describe, expect, it } from 'vitest'
import {
  permissionsOf,
  bindingsBody,
  callbackOutcome,
  connectionsForSlot,
  missingScopes,
  parseScopes,
  providerReturnAddress,
  scopeRefusal,
  redirectUriFor,
  redirectUriWarning,
  slotHint,
  slotSummary,
} from '../connections'
import { hostConnection, hostProvider, hostSlot } from '../../test/pluginFixtures'

describe('connections', () => {
  it('builds the redirect URI the Host builds for the address in use', () => {
    expect(redirectUriFor('https://instance.example.test')).toBe('https://instance.example.test/api/connections/callback')
    expect(redirectUriFor('http://127.0.0.1:8080/')).toBe('http://127.0.0.1:8080/api/connections/callback')
  })

  // What Google and Microsoft check when a redirect URI is registered: its spelling, not whether
  // the internet can reach it.
  it('warns only for a redirect URI a provider will refuse, and names localhost on the same port', () => {
    for (const accepted of [
      'http://localhost:8080',
      'http://127.0.0.1:8080',
      'http://[::1]:8080',
      'https://team.example.com',
      'https://instance.example.test',
    ]) {
      expect(redirectUriWarning(accepted, 'cli'), accepted).toBeNull()
    }

    const lan = redirectUriWarning('http://192.168.1.20:8080', 'cli')!
    expect(lan).toContain('IP address such as 192.168.1.20')
    expect(lan).toContain('http://localhost:8080')
    expect(lan).toContain('`cli connect`')

    expect(redirectUriWarning('http://myserver:8080', 'cli')).toContain('over http only for localhost')
    expect(redirectUriWarning('https://myserver', 'cli')).toContain('public top-level domain')
    expect(redirectUriWarning('https://box.local', 'cli')).toContain('public top-level domain')
    expect(redirectUriWarning('https://10.0.0.5', 'cli')).toContain('IP address')
    expect(redirectUriWarning('http://[fd00::5]:8080', 'cli')).toContain('IP address')
    expect(redirectUriWarning('http://192.168.1.20', 'cli')).toContain('http://localhost on the machine')
  })

  it('lets a `custom` slot take any custom provider, and asks the `custom` scopes of it', () => {
    const slot = hostSlot({ providers: ['google', 'custom'], scopes: { google: ['g'], custom: ['read'] } })
    const acme = hostConnection({ id: 'a', provider: 'custom-acme', scopes: [] })
    const outlook = hostConnection({ id: 'm', provider: 'microsoft' })

    expect(connectionsForSlot(slot, [acme, outlook]).map((c) => c.id)).toEqual(['a'])
    expect(missingScopes(slot, acme)).toEqual(['read'])
  })

  it("uses the Host's summary, and builds the same sentence when there is none", () => {
    const providers = [hostProvider({ id: 'google' }), hostProvider({ id: 'microsoft' })]
    expect(slotSummary(hostSlot({ providers: ['google'], summary: 'needs a Google connection' }))).toBe('needs a Google connection')
    expect(slotSummary(hostSlot({ providers: ['google', 'microsoft', 'custom'], summary: '' }), providers)).toBe(
      'needs a Google, Microsoft or custom connection',
    )
  })

  it("words a slot's hint as one sentence: its description with the summary, or the summary alone", () => {
    const google = { providers: ['google'], summary: 'needs a Google connection' }
    expect(slotHint(hostSlot({ ...google, description: 'The Google account to report on.', required: true }))).toBe(
      'The Google account to report on (needs a Google connection).',
    )
    expect(slotHint(hostSlot({ ...google, required: true }))).toBe('This slot needs a Google connection.')
    expect(slotHint(hostSlot({ ...google, description: 'The drive to read' }))).toBe(
      'The drive to read (needs a Google connection). Optional.',
    )
  })

  it('leaves an unbound slot out of the body', () => {
    const slots = { mail: hostSlot({ providers: ['google'], required: true }), drive: hostSlot({ providers: ['google'] }) }
    expect(bindingsBody(slots, { mail: 'c1', drive: '' })).toEqual({ mail: 'c1' })
  })

  it('reads scopes as typed: commas, spaces and new lines, each once', () => {
    expect(parseScopes(' a, b\nc  a ')).toEqual(['a', 'b', 'c'])
  })

  it("reads what the Host's callback redirect came back with", () => {
    expect(callbackOutcome({ connection: 'connected', id: 'conn-1' })).toEqual({ outcome: 'connected', id: 'conn-1' })
    expect(callbackOutcome({ connection: 'refused', reason: 'state expired' })).toEqual({ outcome: 'refused', reason: 'state expired' })
    expect(callbackOutcome({ tab: 'x' })).toBeNull()
  })

  it("names a connection as the Host's Named does: the account only when it differs from the name", () => {
    const own = hostConnection({ id: 'c1', provider: 'google', name: 'person@example.com' })
    const renamed = hostConnection({ id: 'c2', provider: 'google', name: 'Work mail' })

    expect(scopeRefusal('mail', own, ['s'])).toMatch(/^Connection 'person@example.com' was not granted/)
    expect(scopeRefusal('mail', renamed, ['s'])).toMatch(/^Connection 'Work mail' \(person@example.com\) was not granted/)
  })

  it('sends a person to the Reconnect shown beside the refusal, never to Admin → Connections', () => {
    const own = hostConnection({ id: 'c1', provider: 'google', name: 'person@example.com' })

    expect(scopeRefusal('mail', own, ['s'])).toContain('Press Reconnect to grant that scope.')
    expect(scopeRefusal('mail', own, ['s', 't'])).toContain('Press Reconnect to grant those scopes.')
    expect(scopeRefusal('mail', own, ['s'])).not.toContain('Admin')
  })

  it("moves the provider's return from the search into the hash route, and leaves anything else alone", () => {
    expect(providerReturnAddress({ pathname: '/console', search: '?connection=refused&reason=No+refresh+token' })).toBe(
      '/#/console?connection=refused&reason=No+refresh+token',
    )
    expect(providerReturnAddress({ pathname: '/console', search: '?connection=connected&id=c1' })).toBe('/#/console?connection=connected&id=c1')
    expect(providerReturnAddress({ pathname: '/', search: '' })).toBeNull()
    expect(providerReturnAddress({ pathname: '/', search: '?tab=x' })).toBeNull()
  })
})

describe('permissionsOf', () => {
  it("is the Host's words, once each", () => {
    expect(permissionsOf({ scopes: ['email', 'https://www.googleapis.com/auth/userinfo.email'], permissions: ['See your email address'] })).toEqual([
      'See your email address',
    ])
  })

  it('from a Host without words, is the scopes as themselves, once each', () => {
    expect(permissionsOf({ scopes: ['openid', 'email', 'openid'] })).toEqual(['openid', 'email'])
  })
})
