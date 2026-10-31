import { describe, expect, it } from 'vitest'

import {
  NoOutcomeColour,
  OtherOutcomeColour,
  OutcomePaletteSlots,
  colourForRank,
  outcomeColour,
  outcomeColours,
} from '../outcomeColours'

/**
 * One outcome is one colour wherever it is drawn: the colour follows the outcome's place in the
 * instance-wide fixed order (`rank`), never its position in one team's list.
 */
describe('outcomeColours', () => {
  it('gives ranks 1..8 the palette slots in order', () => {
    expect(OutcomePaletteSlots).toBe(8)
    for (let rank = 1; rank <= 8; rank++) expect(colourForRank(rank)).toBe(`var(--os-series-${rank})`)
  })

  it('folds a rank past the last slot, or a missing or odd rank, into Other rather than cycling a hue', () => {
    for (const rank of [9, 40, 0, -1, 1.5, null, undefined]) expect(colourForRank(rank)).toBe(OtherOutcomeColour)
  })

  it('keeps No outcome on the neutral gray whatever rank it carries', () => {
    expect(outcomeColour({ id: null, rank: null })).toBe(NoOutcomeColour)
    expect(outcomeColour({ id: null, rank: 1 })).toBe(NoOutcomeColour)
  })

  it('colours the same outcome the same on two teams where its position differs', () => {
    // A, B and C are ranks 1..3. Team one lists all three; team two lists only C, first.
    const teamOne = [
      { id: null, rank: null },
      { id: 'a', rank: 1 },
      { id: 'b', rank: 2 },
      { id: 'c', rank: 3 },
    ]
    const teamTwo = [{ id: 'c', rank: 3 }]

    const one = outcomeColours(teamOne)
    const two = outcomeColours(teamTwo)

    expect(two.get('c')).toBe(one.get('c'))
    expect(two.get('c')).toBe('var(--os-series-3)')
    expect(one.get('a')).toBe('var(--os-series-1)')
    expect(one.get(null)).toBe(NoOutcomeColour)
  })

  it('builds one map over every team and keeps the first entry for a repeated outcome', () => {
    const colours = outcomeColours([
      { id: 'c', rank: 3 },
      { id: 'a', rank: 1 },
      { id: 'c', rank: 3 },
    ])
    expect([...colours.entries()]).toEqual([
      ['c', 'var(--os-series-3)'],
      ['a', 'var(--os-series-1)'],
    ])
  })
})
