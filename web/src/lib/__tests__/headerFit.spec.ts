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

  it('takes the version off the row first, then makes the live chip a dot, and nothing else', () => {
    expect(GiveWaySteps).toEqual(['version', 'live-word']);
    expect(headerFit(0, false)).toEqual({ version: true, liveWord: true });
    expect(headerFit(1, false)).toEqual({ version: false, liveWord: true });
    expect(headerFit(2, false)).toEqual({ version: false, liveWord: false });
  });

  it('never carries the version on a phone row', () => {
    expect(headerFit(0, true)).toEqual({ version: false, liveWord: true });
  });

  it('steps once per fitting that keeps too few, and stops at the last step', () => {
    expect(nextGiveWay(0, 10, 10)).toBe(0);
    expect(nextGiveWay(0, 9, 10)).toBe(1);
    expect(nextGiveWay(1, 9, 10)).toBe(2);
    expect(nextGiveWay(2, 0, 10)).toBe(2);
  });
});
