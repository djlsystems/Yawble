import { describe, expect, it } from 'vitest'

import type { AgentAuthReport } from '../../api/types'
import { authProblems } from '../useAgentAuth'

/**
 * The red banner warns only about presets in use. Warning about every installed CLI would let a
 * built-in `codex` no team runs and nobody ever signed in light it for every person.
 */
function report(overrides: Partial<AgentAuthReport>): AgentAuthReport {
  return {
    agent: 'grok-headless',
    command: 'grok',
    installed: true,
    authenticated: false,
    detail: 'not signed in',
    referenced: true,
    ...overrides,
  }
}

describe('authProblems', () => {
  it('warns about a signed-out CLI a team uses', () => {
    expect(authProblems([report({})])).toEqual(['grok-headless: not signed in'])
  })

  it('stays silent about a signed-out CLI nobody uses', () => {
    expect(authProblems([report({ agent: 'codex-headless', command: 'codex', referenced: false })])).toEqual([])
  })

  it('never warns about a CLI that is not installed, signed in, or not measured', () => {
    expect(
      authProblems([
        report({ installed: false }),
        report({ authenticated: true }),
        report({ authenticated: null }),
      ]),
    ).toEqual([])
  })
})
