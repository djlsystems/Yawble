// THE MONITOR'S TOKEN FIGURE: a run counted as `InvocationUsage.BillableTokens` counts it, in whole
// numbers - uncached input and output in full, cache reads at 1/10, cache writes at 5/4, a combined
// total as reported - with the same figures `UsageBillableTests` pins on the Host. A run that
// reported nothing is unmeasured and said beside the figure, never added as a zero.
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Message } from '../../api/types'
import { MonitorModel, TokensTooltip, runBillable, runTokens, tokensFloat, tokensWords } from '../monitor/model'
import { localMinute } from '../localTime'

let seq = 0
function row(type: string, source: string, payload: Record<string, unknown>, occurredAt = '2026-10-15T10:42:00Z'): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId: 0, causationSeq: null, depth: 0, occurredAt }
}

const finished = (payload: Record<string, unknown>, occurredAt?: string) => row('agentContainer.completed', 'Alpha/Developer', payload, occurredAt)

afterEach(() => vi.restoreAllMocks())

describe('a run bills', () => {
  it('100 in, 40 out and 3250 cached reads as 465', () => {
    expect(runBillable({ tokensIn: 100, tokensOut: 40, tokensCachedIn: 3250 })).toBe(465)
  })

  it('cache creation 8 with no input or output as 10', () => {
    expect(runBillable({ tokensIn: 0, tokensOut: 0, tokensCacheCreation: 8 })).toBe(10)
    expect(runBillable({ tokensCacheCreation: 8 })).toBe(10)
  })

  it('a combined total of 900 as 900, not reweighted', () => {
    expect(runBillable({ tokensTotal: 900 })).toBe(900)
    expect(runBillable({ tokensTotal: 900, tokensIn: 1, tokensOut: 1, tokensCachedIn: 5000 })).toBe(900)
  })

  it('cached input above uncached input at a tenth, in whole numbers', () => {
    expect(runBillable({ tokensIn: 1, tokensOut: 1, tokensCachedIn: 50_000 })).toBe(5002)
    expect(runBillable({ tokensIn: 0, tokensOut: 0, tokensCachedIn: 19, tokensCacheCreation: 3 })).toBe(1 + 3)
  })

  it('nothing for a row that names usageCountedOn: another row carries its usage', () => {
    expect(runBillable({ tokensIn: 100, tokensOut: 40, usageCountedOn: 812 })).toBe('counted')
    expect(runTokens({ tokensIn: 100, tokensOut: 40, usageCountedOn: 812 })).toBe(0)
  })

  it('unmeasured, never zero, when it reported no usage', () => {
    expect(runBillable({})).toBeNull()
    expect(runBillable({ tokensIn: 'many' })).toBeNull()
    expect(runBillable({ tokensSource: 'excludedEstimate', tokensIn: 5, tokensOut: 5 })).toBeNull()
    expect(runBillable({ tokensSource: 'noModel' })).toBe(0)
  })
})

describe('the model', () => {
  it('adds each run as billed, and counts the runs that reported nothing apart', () => {
    const model = new MonitorModel()
    model.apply(finished({ tokensIn: 100, tokensOut: 40, tokensCachedIn: 3250 }))
    model.apply(finished({ tokensIn: 0, tokensOut: 0, tokensCacheCreation: 8 }))
    model.apply(finished({ tokensTotal: 900 }))
    model.apply(finished({ tokensIn: 100, tokensOut: 40, usageCountedOn: 3 }))

    expect(model.tokens).toBe(465 + 10 + 900)
    expect(model.unmeasured).toBe(0)
    expect(model.members.get('Alpha/Developer')).toMatchObject({ tokens: 1375, runs: 4 })

    model.apply(finished({}))
    model.apply(row('agentContainer.failed', 'Alpha/Tester', { error: 'boom' }))

    expect(model.tokens).toBe(1375)
    expect(model.unmeasured).toBe(2)
  })

  it('floats the same figure the stat adds', () => {
    const model = new MonitorModel()
    const effects = model.apply(finished({ tokensIn: 100, tokensOut: 40, tokensCachedIn: 3250 }))

    expect(effects).toContainEqual({ kind: 'float', at: { kind: 'member', key: 'Alpha/Developer' }, text: '+465 tokens', tone: 'done' })
    expect(tokensFloat(1234)).toBe('+1.2k tokens')
    expect(model.apply(finished({})).some((effect) => effect.kind === 'float')).toBe(false)
  })

  it('starts its stretch at the oldest row it holds', () => {
    const model = new MonitorModel()
    model.apply(finished({ tokensTotal: 1 }, '2026-10-15T11:00:00Z'))
    model.apply(row('agentContainer.started', 'Alpha/Developer', {}, '2026-10-15T10:42:00Z'))
    model.apply(finished({ tokensTotal: 1 }, '2026-10-15T11:30:00Z'))

    expect(model.since).toBe(Date.parse('2026-10-15T10:42:00Z'))
  })
})

describe('the stat', () => {
  const since = Date.parse('2026-10-15T10:42:00Z')
  const now = Date.parse('2026-10-15T11:15:00Z')

  it('says billable tokens and the stretch it covers, in the reader own time', () => {
    expect(tokensWords(since, now, 0)).toBe(`billable tokens · since ${localMinute(since)}`)
    expect(localMinute(since)).toMatch(/\d{1,2}[:.]\d{2}/)
  })

  it('says how many runs in view were not measured, when any were', () => {
    expect(tokensWords(since, now, 1)).toBe(`billable tokens · since ${localMinute(since)} · 1 run not measured`)
    expect(tokensWords(since, now, 3)).toBe(`billable tokens · since ${localMinute(since)} · 3 runs not measured`)
  })

  it('carries the date when the stretch began on another day', () => {
    const before = Date.parse('2026-10-13T10:42:00Z')
    expect(tokensWords(before, now, 0)).toBe(`billable tokens · since ${localMinute(before, { date: true })}`)
  })

  it('reads the model it is shown for', () => {
    const model = new MonitorModel()
    model.apply(finished({ tokensIn: 100, tokensOut: 40, tokensCachedIn: 3250 }, '2026-10-15T10:42:00Z'))
    model.apply(finished({}, '2026-10-15T10:50:00Z'))

    expect(tokensWords(model.since, now, model.unmeasured)).toBe(`billable tokens · since ${localMinute(since)} · 1 run not measured`)
  })

  it('says in one sentence that it is this window, not the all-time total', () => {
    expect(TokensTooltip.split(/[.!?](\s|$)/).filter((part) => part.trim() !== '')).toHaveLength(1)
    expect(TokensTooltip).toMatch(/this Monitor's window/)
    expect(TokensTooltip).toMatch(/not the team strip's all-time total/)
  })
})
