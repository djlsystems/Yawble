import { describe, expect, it } from 'vitest'
import { Ribbon, fitCount, ribbonEntries } from '../ribbon'

/**
 * The ribbon is ONE 44px row, and what does not fit goes into an overflow
 * menu. Both the row and the drawer are read off `ribbonEntries(Ribbon)`, so the order the strip
 * spills in is the order the drawer lists in - there is no second list of commands to drift.
 */
describe('the ribbon as one command bar', () => {
  const entries = ribbonEntries(Ribbon)

  it('carries every item of every tab, in order', () => {
    expect(entries.map((e) => e.item)).toEqual(Ribbon.tabs.flatMap((t) => t.items))
  })

  it('marks the first entry of each tab as a group start, and only that one', () => {
    const starts = entries.filter((e) => e.groupStart).map((e) => e.tab.id)

    expect(starts).toEqual(Ribbon.tabs.map((t) => t.id))
  })

  it('names every entry by a key unique across the bar', () => {
    const keys = entries.map((e) => e.key)

    expect(new Set(keys).size).toBe(keys.length)
  })
})

describe('fitCount', () => {
  it('shows everything when nothing has been measured', () => {
    expect(fitCount([0, 0, 0], 0, 40)).toBe(3)
  })

  it('shows everything when the last edge fits, without reserving room for the overflow button', () => {
    expect(fitCount([100, 200, 300], 300, 40)).toBe(3)
  })

  it('reserves room for the overflow button once anything spills', () => {
    // 301 wide: the third does not fit, so the button takes 40 and the second no longer does either.
    expect(fitCount([100, 200, 300], 260, 40)).toBe(2)
    expect(fitCount([100, 200, 300], 239, 40)).toBe(1)
  })

  it('never shows a negative number', () => {
    expect(fitCount([100, 200], 10, 40)).toBe(0)
  })
})
