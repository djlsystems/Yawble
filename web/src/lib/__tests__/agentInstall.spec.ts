import { describe, expect, it } from 'vitest'

import type { AgentInstallation } from '../../api/types'
import {
  AgentNotInstalled,
  AgentsAction,
  agentsBadge,
  installGuidance,
  installStatus,
  installationFor,
  isNotInstalled,
  referencedNotInstalled,
} from '../agentInstall'

/**
 * THESE ASSERT CONTENT, which is the part a spec in this suite can actually reach.
 *
 * NO TEST HERE READS A COMPONENT'S SOURCE. Mounted specs exist, and the alternative here is still
 * not grepping the component for a string, because that proves only that somebody typed it. The
 * badge's content lives in `lib/` so it can be asserted directly.
 */

const installation = (
  overrides: Partial<AgentInstallation> & Pick<AgentInstallation, 'agent'>,
): AgentInstallation => ({
  command: overrides.agent,
  state: null,
  resolvedPath: '/usr/local/bin/x',
  referenced: false,
  message: `${overrides.agent} resolves on this machine's PATH.`,
  ...overrides,
})

const missing = (agent: string, referenced: boolean, install?: { url: string; hint?: string }) =>
  installation({
    agent,
    state: AgentNotInstalled,
    resolvedPath: null,
    referenced,
    message: `${agent} was not found on this machine's PATH.`,
    ...(install ? { install } : {}),
  })

describe('the fourth state', () => {
  it('is the exact wire value the server sends, and nothing else counts', () => {
    expect(AgentNotInstalled).toBe('AgentNotInstalled')
  })

  it('recognises a preset whose command did not resolve', () => {
    expect(isNotInstalled(missing('copilot', true))).toBe(true)
  })

  /**
   * EXACT MATCH, deliberately. A null state from a server that does not probe, an unknown value
   * from a newer one, an absent entry - none of those is a claim that the CLI is missing. The
   * mark says "this will not run on this machine", so the
   * failure direction is to not make that claim.
   */
  it.each([
    ['a preset that resolved', installation({ agent: 'claude' })],
    ['a state this build does not know', installation({ agent: 'claude', state: 'Something' })],
    ['nothing at all', undefined],
    ['null', null],
  ])('does not claim %s is missing', (_label, entry) => {
    expect(isNotInstalled(entry)).toBe(false)
  })
})

describe('finding a preset s probe result', () => {
  /**
   * The server folds case everywhere a preset name is looked up - `AgentCatalog.Definition`,
   * `team_members.agent`, the rename route's `COLLATE NOCASE`. Joining case-sensitively here would
   * show NO state for a preset spelled differently in two places, which reads as the probe not
   * having run rather than as a mismatch.
   */
  it('matches a preset name without regard to case', () => {
    const found = installationFor([installation({ agent: 'Claude-Headless' })], 'claude-headless')

    expect(found?.agent).toBe('Claude-Headless')
  })

  it('answers nothing for a list that has no such preset', () => {
    expect(installationFor([installation({ agent: 'claude' })], 'grok')).toBeUndefined()
  })

  it('survives a list that is not there at all', () => {
    expect(installationFor(undefined, 'claude')).toBeUndefined()
  })
})

describe('what one row on the Agents screen says', () => {
  /** TEXT PLUS ICON, NEVER COLOUR ALONE. */
  it('carries words and a glyph for every state, not a colour', () => {
    for (const status of [
      installStatus(installation({ agent: 'claude' })),
      installStatus(missing('copilot', true)),
      installStatus(undefined),
    ]) {
      expect(status.text.trim().length).toBeGreaterThan(0)
      expect(status.icon.trim().length).toBeGreaterThan(0)
    }
  })

  it('says a resolved command was found', () => {
    expect(installStatus(installation({ agent: 'claude' })).tone).toBe('ok')
  })

  it('says an unresolved command was not found on this machine', () => {
    const status = installStatus(missing('copilot', false))

    expect(status.tone).toBe('warn')
    expect(status.text).toContain('Not found')
  })

  /**
   * NOT CHECKED IS A REAL STATE. A client reading a server that does not probe knows nothing, and
   * rendering that as "found" would be a claim nothing made.
   */
  it('reports an absent entry as unknown rather than as healthy', () => {
    expect(installStatus(undefined).tone).toBe('unknown')
  })

  /**
   * THE WORDING MUST NOT EXCEED THE CHECK. Resolution is not execution: a command that resolves may
   * still be broken, the wrong version or unauthenticated.
   */
  it('never claims the CLI works, is current, or is signed in', () => {
    const words = [
      installStatus(installation({ agent: 'claude' })).text,
      installStatus(missing('copilot', true)).text,
    ].join(' ')

    for (const overclaim of ['working', 'ready', 'authenticat', 'version', 'up to date']) {
      expect(words.toLowerCase()).not.toContain(overclaim)
    }
  })
})

describe('install guidance', () => {
  it('renders the preset s own link when it has one', () => {
    const guidance = installGuidance(
      missing('codex', true, { url: 'https://example.invalid/codex', hint: 'install it' }),
    )

    expect(guidance.url).toBe('https://example.invalid/codex')
    expect(guidance.hint).toBe('install it')
  })

  /**
   * ABSENT DEGRADES TO TEXT, and NOTHING IS CONSTRUCTED. A table here mapping `codex` to a vendor's
   * documentation would work everywhere immediately and is the change refused: nothing in code may
   * name an Agent, so a name here is a reference nothing wrote down - no check sees it, no rename
   * moves it, and a vendor moving its documentation leaves a dead link only a rebuild can fix.
   */
  it('renders the same sentence with no link when the preset has none', () => {
    const withLink = installGuidance(missing('codex', true, { url: 'https://example.invalid/x' }))
    const without = installGuidance(missing('codex', true))

    expect(without.url).toBeNull()
    expect(without.hint).toBeNull()
    expect(without.text).toBe(withLink.text)
    expect(without.text.length).toBeGreaterThan(0)
  })

  /**
   * NOTHING IS EVER BUILT FROM THE PRESET'S NAME. Stated as a property over a set of brand names
   * rather than against one, so a lookup table added later fails this however it is spelled.
   */
  it.each(['claude', 'codex', 'copilot', 'grok'])(
    'invents no URL for %s when the catalog carries none',
    (agent) => {
      expect(installGuidance(missing(agent, true)).url).toBeNull()
    },
  )

  /**
   * A hand-edited `"install": {}` is reachable, and an anchor around an empty string is a link to
   * nowhere - the guessed-URL failure arriving by the back door.
   */
  it.each(['', '   '])('treats a blank URL (%p) as no link at all', (url) => {
    expect(installGuidance(missing('codex', true, { url })).url).toBeNull()
  })
})

describe('the ribbon badge', () => {
  /**
   * THE QUIET CASE, and it is the one that must be right. On a machine with every CLI installed the
   * mark is ABSENT - a badge that is always lit is wallpaper, and it stops being read long before
   * the day it matters.
   */
  it('is absent when everything a team uses resolves', () => {
    expect(
      agentsBadge([
        installation({ agent: 'claude', referenced: true }),
        installation({ agent: 'claude-headless', referenced: true }),
      ]),
    ).toBeNull()
  })

  /**
   * A CATALOG FULL OF UNREFERENCED MISSING PRESETS IS A BADGE OF ZERO. Warn about what is in use,
   * list what exists: a tenant with eight seeded presets and two CLIs installed would otherwise
   * carry a permanent badge of six.
   */
  it('is absent when every missing preset is one no team uses', () => {
    expect(
      agentsBadge([
        missing('codex', false),
        missing('codex-headless', false),
        missing('grok', false),
        missing('grok-headless', false),
      ]),
    ).toBeNull()

    expect(
      referencedNotInstalled([missing('codex', false), missing('grok', false)]),
    ).toHaveLength(0)
  })

  it('counts only the presets that are BOTH missing and in use', () => {
    const badge = agentsBadge([
      installation({ agent: 'claude', referenced: true }),
      missing('copilot', true),
      missing('copilot-headless', true),
      missing('grok', false),
    ])

    expect(badge?.count).toBe(2)
  })

  /** TEXT PLUS ICON, NEVER COLOUR ALONE. The number IS the text, so the mark survives a screen
   *  read without colour, and the label says what it means in words. */
  it('draws a number and a glyph, and says what it means in words', () => {
    const badge = agentsBadge([missing('copilot', true)])

    expect(badge?.text).toBe('1')
    expect(badge?.icon.trim().length).toBeGreaterThan(0)
    expect(badge?.label).toContain('copilot')
    expect(badge?.label).toContain('not installed on this machine')
  })

  it('names every preset it is counting, so the words say what to go and do', () => {
    const badge = agentsBadge([missing('copilot', true), missing('grok-headless', true)])

    expect(badge?.label).toContain('copilot')
    expect(badge?.label).toContain('grok-headless')
  })

  it('says nothing at all about a client that was handed no probe result', () => {
    expect(agentsBadge(undefined)).toBeNull()
    expect(agentsBadge([])).toBeNull()
  })
})

describe('where the badge attaches', () => {
  /**
   * ONE definition for the desktop strip and the mobile drawer. A mark on one and not the other is
   * the drift an earlier team mark shipped with, and it is worse than no mark: it teaches a person
   * that the absence of one means something.
   */
  it('is the Agents ribbon action, spelled the way the ribbon file spells it', () => {
    expect(AgentsAction).toBe('admin-agents')
  })
})
