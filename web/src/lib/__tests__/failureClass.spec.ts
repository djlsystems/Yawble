import { describe, expect, it } from 'vitest'
import type { ContainerSnapshot, Message } from '../../api/types'
import { asMemberId, asTeamId } from '../../api/types'
import { summarise } from '../summarise'
import { containerMark } from '../teamKpis'
import {
  failureClassLabel,
  failureClassWords,
  resumeTimeText,
  resumesAutomatically,
} from '../failureClass'

const failed = (payload: Record<string, unknown>): Message => ({
  seq: 1,
  type: 'agentContainer.failed',
  payload: JSON.stringify(payload),
  source: 'Alpha/Manager',
  correlationId: 1,
  causationSeq: null,
  depth: 0,
  occurredAt: '',
})

// Branded at the fixture's edge, exactly as `teamKpis.spec.ts` does it and for its reason: a
// literal in a test genuinely is an identifier arriving from outside the app.
const container = (over: Partial<Omit<ContainerSnapshot, 'id'>> = {}): ContainerSnapshot => ({
  team: asTeamId('Alpha'),
  id: asMemberId('Manager'),
  name: 'Manager',
  agent: 'claude',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 8,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  ...over,
})

describe('resumesAutomatically', () => {
  /**
   * THE PRODUCT RULE, in the browser's copy of it. The card reads this to
   * decide whether to promise a resume, so a widening here is a card promising something the server
   * has no intention of doing.
   */
  it('is true for quota and rate and nothing else', () => {
    expect(resumesAutomatically('quota')).toBe(true)
    expect(resumesAutomatically('rate')).toBe(true)

    for (const never of ['transport', 'timeout', 'agent-fault', 'launch-missing', 'out-of-memory', 'interrupted', 'unknown']) {
      expect(resumesAutomatically(never)).toBe(false)
    }
  })

  /** A row with no class, and a class from a newer server, both answer no. */
  it('is false for anything it cannot read', () => {
    expect(resumesAutomatically(null)).toBe(false)
    expect(resumesAutomatically(undefined)).toBe(false)
    expect(resumesAutomatically('QUOTA')).toBe(false)
    expect(resumesAutomatically('a-class-from-2027')).toBe(false)
  })
})

describe('failureClassWords', () => {
  it('has words for every class the server writes', () => {
    for (const known of [
      'quota',
      'rate',
      'transport',
      'agent-fault',
      'launch-missing',
      'out-of-memory',
      'timeout',
      'interrupted',
      'unknown',
    ]) {
      expect(failureClassWords(known)).toBeTruthy()
    }
  })

  /**
   * NULL IS THE LOAD-BEARING ANSWER. Every caller renders nothing for it, which is how rows and
   * cards written without a class keep their meaning — the log is append-only, and so is the board's
   * reading of it.
   */
  it('is null for no class and for a class it does not know', () => {
    expect(failureClassWords(null)).toBeNull()
    expect(failureClassWords(undefined)).toBeNull()
    expect(failureClassWords('a-class-from-2027')).toBeNull()
    expect(failureClassLabel('a-class-from-2027')).toBeNull()
  })

  /** A program missing at launch asks for a re-send, and asks nobody to repair anything. */
  it('says a launch-missing run is re-sent, not repaired', () => {
    const words = failureClassWords('launch-missing')!
    expect(words).toContain('not found when the run started')
    expect(words).toContain('re-sending the instruction will try again')
    expect(words).not.toMatch(/repair/i)
    expect(words).not.toContain('agent itself failed')
    expect(failureClassLabel('launch-missing')).toBe('launch-missing')
  })

  /** A run stopped by its own memory limit names the setting and is not the agent's fault. */
  it('says an out-of-memory run hit its limit, names the setting and blames no agent', () => {
    const words = failureClassWords('out-of-memory')!
    expect(words).toContain('runs.memoryLimitMb')
    expect(words).toContain('not an agent fault')
    expect(failureClassWords('out-of-memory', 'plugin')).toContain('runs.memoryLimitMb')
    expect(failureClassLabel('out-of-memory')).toBe('out-of-memory')
  })

  /** `unknown` is a real class with something to say, not an absence dressed up as one. */
  it('says that unknown is not resumed', () => {
    expect(failureClassWords('unknown')).toContain('not resumed')
    expect(failureClassLabel('unknown')).toBe('unknown')
  })
})

describe('resumeTimeText', () => {
  it('renders a stamp as local wall-clock', () => {
    expect(resumeTimeText('2026-09-20T04:00:00.0000000Z')).toBe(
      new Date('2026-09-20T04:00:00.0000000Z').toLocaleString(),
    )
  })

  /**
   * A card that promises a resume at a moment it cannot name is worse than one that promises
   * nothing — and `Invalid Date` on a board is exactly that.
   */
  it('is null for anything it cannot parse', () => {
    expect(resumeTimeText(null)).toBeNull()
    expect(resumeTimeText(undefined)).toBeNull()
    expect(resumeTimeText('')).toBeNull()
    expect(resumeTimeText('midnight-ish')).toBeNull()
  })
})

describe('containerMark', () => {
  it('carries the class and the resume beside the reason', () => {
    const mark = containerMark(
      container({
        failed: 'spend limit',
        failureClass: 'quota',
        resumeAt: '2026-09-20T04:00:00.0000000Z',
      }),
    )

    expect(mark).toEqual({
      kind: 'failed',
      reason: 'spend limit',
      failureClass: 'quota',
      resumeAt: '2026-09-20T04:00:00.0000000Z',
    })
  })

  /** A snapshot without a failure class has neither field, and a card must not invent one. */
  it('reports nulls for a failure with no class', () => {
    const mark = containerMark(container({ failed: 'the process died' }))

    expect(mark).toEqual({
      kind: 'failed',
      reason: 'the process died',
      failureClass: null,
      resumeAt: null,
    })
  })
})

describe('summarise of a failed row', () => {
  /**
   * OLD ROWS RENDER EXACTLY AS THEY DID. Asserted as an exact equality rather than a `toContain`:
   * a class quietly prefixed to every historical row would pass a looser test.
   */
  it('renders a row with no class exactly as it did before classes', () => {
    expect(summarise(failed({ output: '', launchError: 'not on PATH' }))).toBe('not on PATH')
  })

  it('renders a row whose class it does not know exactly the same way', () => {
    expect(
      summarise(failed({ output: '', launchError: 'not on PATH', failureClass: 'a-class-from-2027' })),
    ).toBe('not on PATH')
  })

  /**
   * THE CLASS GOES FIRST. A feed row is clamped to two lines, so a qualifier at the end of a
   * provider's own sentence is a qualifier nobody reads — and "FAILED" alone sends a person to
   * investigate a run that was working perfectly and hit a spend limit.
   */
  it('leads with the class and keeps the reason', () => {
    const line = summarise(
      failed({ output: '', launchError: "You've hit your monthly spend limit", failureClass: 'quota' }),
    )

    expect(line.startsWith('[quota]')).toBe(true)
    expect(line).toContain('a budget is spent')
    expect(line).toContain("You've hit your monthly spend limit")
  })

  it('says when a pending resume fires', () => {
    const line = summarise(
      failed({
        output: '',
        launchError: 'spend limit',
        failureClass: 'quota',
        retryAfter: '2026-09-20T04:00:00.0000000Z',
      }),
    )

    expect(line).toContain(
      `Resuming at ${new Date('2026-09-20T04:00:00.0000000Z').toLocaleString()}`,
    )
  })

  /**
   * A CLASS THAT NEVER RESUMES PROMISES NOTHING, even carrying a `retryAfter` — that field is what
   * the PROVIDER said, and transport was offered an automatic resume and refused.
   */
  it('makes no promise for a class that never resumes', () => {
    const line = summarise(
      failed({
        output: '',
        launchError: 'socket hang up',
        failureClass: 'transport',
        retryAfter: '2026-09-20T04:00:00.0000000Z',
      }),
    )

    expect(line).toContain('[transport]')
    expect(line).not.toContain('Resuming at')
  })

  /** A failure with a class and nothing else still says something. */
  it('renders a class with no reason without a trailing dangle', () => {
    expect(summarise(failed({ output: '', launchError: '', failureClass: 'unknown' }))).toBe(
      '[unknown] nothing could say why, so this is treated as an agent fault and is not resumed.',
    )
  })
})
