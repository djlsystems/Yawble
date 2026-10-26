import { describe, expect, it } from 'vitest';
import { GiveWaySteps, headerFit, keepThrough, nextGiveWay } from '../headerFit';
import { Ribbon, ribbonEntries } from '../ribbon';

describe('the one header row gives way in one order', () => {
  it('keeps New Team through Backlog', () => {
    const entries = ribbonEntries(Ribbon);
    const keep = keepThrough(Ribbon);

    expect(entries[0]!.item.label).toBe('New Team');
    expect(entries[keep - 1]!.item.action).toBe('backlog');
    expect(entries.slice(keep).some((entry) => entry.tab.id === 'active')).toBe(false);
  });

  /** Each step's field on `HeaderFit`, and what that field reads before the step is taken. */
  const StepField = {
    version: ['version', true],
    tight: ['tight', false],
    mark: ['mark', false],
  } as const;

  /** The tools on the right are glyphs at every width, so only these three give way. */
  it('names three steps, in the ruled order: the version, the tight ribbon, the mark', () => {
    expect(GiveWaySteps).toEqual(['version', 'tight', 'mark']);
  });

  it('gives nothing way at no steps: the version, full padding, the full logo', () => {
    expect(headerFit(0, false)).toEqual({ version: true, tight: false, mark: false });
  });

  it('takes exactly one more thing at each step, the next in the order, and keeps the earlier ones taken', () => {
    GiveWaySteps.forEach((step, index) => {
      const before = headerFit(index, false);
      const after = headerFit(index + 1, false);
      const [field, untaken] = StepField[step];

      expect(before[field], step).toBe(untaken);
      expect(after[field], step).toBe(!untaken);
      const changed = (Object.keys(after) as (keyof typeof after)[]).filter((key) => after[key] !== before[key]);
      expect(changed, step).toEqual([field]);
    });
  });

  it('never carries the version on a phone row', () => {
    expect(headerFit(0, true)).toEqual({ version: false, tight: false, mark: false });
  });

  it('steps once per fitting that keeps too few, and stops at the last step', () => {
    for (let level = 0; level < GiveWaySteps.length; level++) expect(nextGiveWay(level, 9, 10)).toBe(level + 1);
    expect(nextGiveWay(GiveWaySteps.length, 0, 10)).toBe(GiveWaySteps.length);
  });

  /** A step is never taken while the ones before it suffice; a wide window that keeps all takes none. */
  it('takes no step while the row already keeps New Team through Backlog', () => {
    for (let level = 0; level <= GiveWaySteps.length; level++) {
      expect(nextGiveWay(level, 10, 10)).toBe(level);
      expect(nextGiveWay(level, 23, 10)).toBe(level);
    }
  });

  /** The 3rem title cap rides on `tight`: taken at the padding step and kept at the mark, never before. */
  it('caps the active team title only while the padding and mark steps are taken', () => {
    const tightAt = GiveWaySteps.indexOf('tight');
    for (let level = 0; level <= GiveWaySteps.length; level++) expect(headerFit(level, false).tight, String(level)).toBe(level > tightAt);
    expect(GiveWaySteps.slice(tightAt)).toEqual(['tight', 'mark']);
  });
});
