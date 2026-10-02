import { describe, expect, it } from 'vitest'
import {
  conciergeLine,
  credentialFor,
  credentialState,
  sharedLine,
  withCredentialStatus,
  withSource,
} from '../agentCredentials'
import { sourceMapOf } from '../tenantSettings'
import type { AgentCredential, IssuedCredentialDeclaration } from '../../api/types'

const declaration = (loginPrecedence: IssuedCredentialDeclaration['loginPrecedence']): IssuedCredentialDeclaration => ({
  kinds: [{ kind: 'apiKey', variable: 'X_API_KEY' }],
  displaces: ['X_API_KEY'],
  loginPrecedence,
  measuredWith: '1.0.0',
})

const entry = (agent: string, command: string, sharedWith: string[]): AgentCredential => ({
  agent,
  command,
  sharedWith,
  source: 'home',
  issuedCredential: declaration('credential'),
  set: false,
  setBy: null,
  setAt: null,
})

const list = [entry('claude', 'claude', ['claude-headless']), entry('claude-headless', 'claude', ['claude']), entry('grok', 'grok', [])]

describe('agent credentials', () => {
  it('lays a command\'s state over every preset that runs it, and no other', () => {
    const next = withCredentialStatus(list, { command: 'Claude', set: true, setBy: 'p@example.com', setAt: '2026-10-02T10:00:00Z' })
    expect(next.map((e) => e.set)).toEqual([true, true, false])
    expect(next[1].setBy).toBe('p@example.com')
    expect(list[0].set).toBe(false)
  })

  it('moves only the one preset\'s source', () => {
    const next = withSource(list, 'CLAUDE-HEADLESS', 'issued')
    expect(next.map((e) => e.source)).toEqual(['home', 'issued', 'home'])
  })

  it('says who set it and when, or not set', () => {
    const set = { ...list[0], set: true, setBy: 'p@example.com', setAt: '2026-10-02T10:00:00Z' }
    expect(credentialState(set, (iso) => `<${iso}>`)).toBe('set by p@example.com at <2026-10-02T10:00:00Z>')
    expect(credentialState(list[0])).toBe('not set')
  })

  it('names the other presets that share it', () => {
    expect(sharedLine(list[0])).toBe('Shared by every preset that runs claude: also claude-headless.')
    expect(sharedLine(list[2])).toBe('Shared by every preset that runs grok: also no other preset.')
    expect(credentialFor(list, 'Claude-Headless')?.agent).toBe('claude-headless')
  })

  it('states the Concierge line for each measured precedence', () => {
    expect(conciergeLine(declaration('credential'))).toContain('uses the issued credential')
    expect(conciergeLine(declaration('login'))).toContain("person's own login")
    expect(conciergeLine(declaration('unmeasured'))).toContain('not measured')
  })

  it('reads agents.credentialSource tolerantly', () => {
    expect(sourceMapOf({ a: 'issued', b: 'home', c: 'elsewhere', d: 3 })).toEqual({ a: 'issued', b: 'home' })
    expect(sourceMapOf('{"a":"issued"}')).toEqual({ a: 'issued' })
    expect(sourceMapOf('not json')).toEqual({})
    expect(sourceMapOf(['a'])).toEqual({})
    expect(sourceMapOf(undefined)).toEqual({})
  })
})
