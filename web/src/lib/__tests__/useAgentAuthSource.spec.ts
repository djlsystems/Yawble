import { describe, expect, it } from 'vitest'
import { authStatus, sourceLabel } from '../useAgentAuth'
import type { AgentAuthReport } from '../../api/types'

const report = (over: Partial<AgentAuthReport>): AgentAuthReport => ({
  agent: 'claude-headless',
  command: 'claude',
  installed: true,
  authenticated: true,
  detail: '',
  referenced: true,
  ...over,
})

describe('the sign-in source', () => {
  it('names the shared home and the issued credential', () => {
    expect(sourceLabel(report({ source: 'home' }))).toBe('shared home')
    expect(sourceLabel(report({ source: 'issued' }))).toBe('issued credential')
  })

  it('says nothing when the server did not say, rather than reading it as home', () => {
    expect(sourceLabel(report({}))).toBeNull()
    expect(sourceLabel(undefined)).toBeNull()
  })

  it('leaves the sign-in caption as the probe measured it under issued', () => {
    const missing = report({ source: 'issued', authenticated: false, detail: 'issued credential not set' })
    expect(authStatus(missing)).toEqual({ text: 'Not authenticated', icon: 'no_accounts', tone: 'warn', detail: 'issued credential not set' })
    expect(authStatus(report({ source: 'issued' })).tone).toBe('ok')
  })
})
