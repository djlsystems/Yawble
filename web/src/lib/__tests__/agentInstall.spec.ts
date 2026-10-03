import { describe, expect, it } from 'vitest'

import type { AgentInstallation } from '../../api/types'
import {
  AgentNotInstalled,
  AgentUpdating,
  AgentsAction,
  agentsBadge,
  installGuidance,
  installStatus,
  installationFor,
  isNotInstalled,
  isUpdating,
  listOf,
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

/**
 * A SERVER WHOSE AGENT CLIS ARE ON WORKERS sends `measuredOn`, and the words follow it: the worker
 * a CLI is missing on, both when two disagree, and "not measured" - never "found" and never a badge -
 * when no worker has answered. Without `measuredOn` the words are this machine's, as before.
 */
describe('in control, where the workers measured it', () => {
  const at = '2026-10-02T12:00:00Z'

  const measuredMissing = (agent: string, referenced: boolean, ...workers: [string, boolean][]) =>
    installation({
      agent,
      state: AgentNotInstalled,
      resolvedPath: null,
      referenced,
      message: `${agent} is not installed on worker-1.`,
      measuredOn: workers.map(([worker, installed]) => ({ worker, installed, at })),
    })

  it('names the worker a CLI is missing on', () => {
    const entry = measuredMissing('grok-headless', true, ['worker-1', false])

    expect(installStatus(entry)).toEqual({ text: 'Not installed on worker-1', icon: 'error', tone: 'warn' })
    expect(agentsBadge([entry])?.label).toBe('grok-headless is not installed on worker-1, and a team uses it.')
  })

  it('names the worker it is missing on when two workers disagree', () => {
    const entry = measuredMissing('grok-headless', true, ['worker-1', true], ['worker-2', false])

    expect(installStatus(entry).text).toBe('Not installed on worker-2')
    expect(agentsBadge([entry])?.label).toBe('grok-headless is not installed on worker-2, and a team uses it.')
  })

  it('groups presets missing on the same worker, and names each place when they differ', () => {
    const same = [
      measuredMissing('claude-headless', true, ['worker-1', false]),
      measuredMissing('grok-headless', true, ['worker-1', false]),
    ]
    expect(agentsBadge(same)?.label).toBe('claude-headless, grok-headless are not installed on worker-1, and teams use them.')

    const differ = [
      measuredMissing('claude-headless', true, ['worker-2', false]),
      measuredMissing('grok-headless', true, ['worker-1', false]),
    ]
    expect(agentsBadge(differ)?.label).toBe(
      'claude-headless is not installed on worker-2 and grok-headless is not installed on worker-1, and teams use them.',
    )
  })

  it('reads installed on the workers that measured it', () => {
    const entry = installation({
      agent: 'claude-headless',
      resolvedPath: null,
      referenced: true,
      message: 'claude is installed on worker-1 and worker-2.',
      measuredOn: [
        { worker: 'worker-1', installed: true, at },
        { worker: 'worker-2', installed: true, at },
      ],
    })

    expect(installStatus(entry)).toEqual({ text: 'Installed on worker-1 and worker-2', icon: 'check_circle', tone: 'ok' })
    expect(agentsBadge([entry])).toBeNull()
  })

  it('reads not measured when no worker has answered, and lights no badge', () => {
    const entry = installation({
      agent: 'claude-headless',
      resolvedPath: null,
      referenced: true,
      message: 'claude has not been measured: no worker is connected.',
      measuredOn: [],
    })

    expect(installStatus(entry)).toEqual({ text: 'Not measured', icon: 'help', tone: 'unknown' })
    expect(agentsBadge([entry])).toBeNull()
  })

  it("keeps 'this machine' when no measuredOn is sent", () => {
    expect(agentsBadge([missing('copilot', true)])?.label).toBe('copilot is not installed on this machine, and a team uses it.')
    expect(installStatus(missing('copilot', true)).text).toBe('Not found on this machine')
  })

  it('lists workers the way the server does', () => {
    expect(listOf(['worker-1'])).toBe('worker-1')
    expect(listOf(['worker-1', 'worker-2'])).toBe('worker-1 and worker-2')
    expect(listOf(['worker-1', 'worker-2', 'worker-3'])).toBe('worker-1, worker-2 and worker-3')
  })
})

describe('a preset whose command the platform is updating', () => {
  const at = '2026-10-03T09:00:00Z'
  const updating = (agent: string, phase: 'waiting' | 'updating', worker: string | null, referenced = true) =>
    installation({
      agent,
      state: AgentUpdating,
      resolvedPath: null,
      referenced,
      message: `Updating ${agent}; it is measured again when the update ends.`,
      measuredOn: [{ worker: 'worker-1', installed: false, at }],
      updating: { phase, worker, since: at },
    })

  it('is its own exact wire value, and never missing', () => {
    expect(AgentUpdating).toBe('AgentUpdating')
    expect(isUpdating(updating('claude-headless', 'updating', 'worker-1'))).toBe(true)
    expect(isNotInstalled(updating('claude-headless', 'updating', 'worker-1'))).toBe(false)
  })

  it("an updating preset reads 'Updating on worker-1', never not installed", () => {
    expect(installStatus(updating('claude-headless', 'updating', 'worker-1'))).toEqual({ text: 'Updating on worker-1', icon: 'sync', tone: 'unknown' })
  })

  it("waiting reads 'Updating: waiting for runs to finish' and names no worker", () => {
    const status = installStatus(updating('claude-headless', 'waiting', null))
    expect(status.text).toBe('Updating: waiting for runs to finish')
    expect(status.text).not.toMatch(/worker/)
  })

  it("in a server that runs its runs itself it reads 'Updating on this machine'", () => {
    const here = installation({ agent: 'claude-headless', state: AgentUpdating, resolvedPath: null, updating: { phase: 'updating', worker: null, since: at } })
    expect(installStatus(here).text).toBe('Updating on this machine')
  })

  it('an updating preset is not counted by the badge, and the badge says so in words', () => {
    const badge = agentsBadge([updating('claude-headless', 'updating', 'worker-1')])
    expect(badge).toEqual({ count: 0, text: '↻', icon: 'sync', label: 'claude-headless is being updated on worker-1; its runs wait for it.' })
    expect(referencedNotInstalled([updating('claude-headless', 'updating', 'worker-1')])).toEqual([])
  })

  it('an updating preset nobody uses lights no badge', () => {
    expect(agentsBadge([updating('claude-headless', 'updating', 'worker-1', false)])).toBeNull()
  })

  it('missing and updating together name both, and count only the missing one', () => {
    const badge = agentsBadge([missing('copilot', true), updating('claude-headless', 'updating', 'worker-1')])
    expect(badge?.count).toBe(1)
    expect(badge?.text).toBe('1')
    expect(badge?.label).toBe(
      'copilot is not installed on this machine, and a team uses it. claude-headless is being updated on worker-1; its runs wait for it.',
    )
  })
})
