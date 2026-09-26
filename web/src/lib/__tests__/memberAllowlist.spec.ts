import { describe, expect, it } from 'vitest'
import {
  allowedAgentOptions,
  allowlistIncludes,
  normalizeAllowlist,
} from '../memberAllowlist'

describe('member allowlist', () => {
  it('normalizes free text entries and dedupes case-insensitively', () => {
    expect(
      normalizeAllowlist(['  copilot-headless  ', '', 'CLAUDE-headless', 'claude-headless']),
    ).toEqual(['copilot-headless', 'CLAUDE-headless'])
  })

  it('keeps ordering from the allowlist when deriving selectable options', () => {
    const available = ['claude-headless', 'copilot-headless', 'echo']
    const allowlist = ['copilot-headless', 'CLAUDE-headless']

    expect(allowedAgentOptions(available, allowlist)).toEqual([
      'copilot-headless',
      'claude-headless',
    ])
  })

  it('compares inclusion case-insensitively', () => {
    expect(allowlistIncludes(['copilot-headless', 'claude-headless'], 'CLAUDE-HEADLESS')).toBe(
      true,
    )
    expect(allowlistIncludes(['copilot-headless'], 'grok-headless')).toBe(false)
  })
})
