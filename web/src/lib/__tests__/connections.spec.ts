import { describe, expect, it } from 'vitest'
import {
  bindingsBody,
  callbackOutcome,
  connectionsForSlot,
  missingScopes,
  parseScopes,
  providerReturnAddress,
  scopeRefusal,
  redirectUriFor,
  slotSummary,
} from '../connections'
import { hostConnection, hostProvider, hostSlot } from '../../test/pluginFixtures'

describe('connections', () => {
  it('builds the redirect URI the Host builds for the address in use', () => {
    expect(redirectUriFor('https://instance.example.test')).toBe('https://instance.example.test/api/connections/callback')
    expect(redirectUriFor('http://127.0.0.1:8080/')).toBe('http://127.0.0.1:8080/api/connections/callback')
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

  it("moves the provider's return from the search into the hash route, and leaves anything else alone", () => {
    expect(providerReturnAddress({ pathname: '/console', search: '?connection=refused&reason=No+refresh+token' })).toBe(
      '/#/console?connection=refused&reason=No+refresh+token',
    )
    expect(providerReturnAddress({ pathname: '/console', search: '?connection=connected&id=c1' })).toBe('/#/console?connection=connected&id=c1')
    expect(providerReturnAddress({ pathname: '/', search: '' })).toBeNull()
    expect(providerReturnAddress({ pathname: '/', search: '?tab=x' })).toBeNull()
  })
})
