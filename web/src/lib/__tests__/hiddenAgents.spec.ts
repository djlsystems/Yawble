import { describe, expect, it } from 'vitest'
import { visibleAgents } from '../hiddenAgents'
import type { Agent } from '../../api/types'

const agent = (name: string, hidden?: boolean): Agent => ({ name, mode: 'Headless', hidden }) as Agent

describe('visibleAgents', () => {
  it('drops a hidden preset', () => {
    expect(visibleAgents([agent('claude'), agent('echo', true)]).map((a) => a.name)).toEqual([
      'claude',
    ])
  })

  it('treats an absent flag as visible, which is every catalog written before the field existed', () => {
    expect(visibleAgents([agent('claude')]).map((a) => a.name)).toEqual(['claude'])
  })

  it('treats an explicit false as visible', () => {
    expect(visibleAgents([agent('claude', false)]).map((a) => a.name)).toEqual(['claude'])
  })

  it('never mutates what it was given, because the caller still submits the whole catalog', () => {
    // The guard on the defect this lib exists to prevent: PUT replaces the catalog, so the list the
    // dialog HOLDS must survive a call that only decides what it RENDERS.
    const all = [agent('claude'), agent('echo', true)]

    visibleAgents(all)

    expect(all).toHaveLength(2)
    expect(all.map((a) => a.name)).toEqual(['claude', 'echo'])
  })

  it('keeps the order it was given', () => {
    const names = visibleAgents([agent('grok'), agent('echo', true), agent('claude')]).map(
      (a) => a.name,
    )

    expect(names).toEqual(['grok', 'claude'])
  })
})
