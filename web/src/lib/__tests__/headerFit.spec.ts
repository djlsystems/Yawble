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
    'update-word': ['updateWord', true],
    version: ['version', true],
    'live-word': ['liveWord', true],
    'account-name': ['accountName', true],
    'needs-you-word': ['needsYouWord', true],
    tight: ['tight', false],
    mark: ['mark', false],
  } as const;

  it('names seven steps, in the ruled order, the update notice first', () => {
    expect(GiveWaySteps).toEqual(['update-word', 'version', 'live-word', 'account-name', 'needs-you-word', 'tight', 'mark']);
  });

  it('gives nothing way at no steps: update words, version, live word, account name, Needs you word, full padding, full logo', () => {
    expect(headerFit(0, false)).toEqual({
      updateWord: true,
      version: true,
      liveWord: true,
      accountName: true,
      needsYouWord: true,
      tight: false,
      mark: false,
    });
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

  /** A phone row carries neither the version nor the account name nor the Needs you word, as before,
   *  nor the update notice's words, which pushed the account menu off a 390px row. */
  it('never carries the version, the account name, the Needs you word or the update words on a phone row', () => {
    expect(headerFit(0, true)).toEqual({
      updateWord: false,
      version: false,
      liveWord: true,
      accountName: false,
      needsYouWord: false,
      tight: false,
      mark: false,
    });
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
