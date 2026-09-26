import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { getWip } = vi.hoisted(() => ({ getWip: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getWip,
}));

import { WipPollMs, inProgressHeader, useWipStore, wipLine } from '../wip';

const view = {
  max: 4,
  running: [
    { team: 'alpha', member: 'Dev1', since: '' },
    { team: 'beta', member: 'Dev1', since: '' },
  ],
  waiting: [{ team: 'alpha', member: 'Manager', since: '' }],
};

beforeEach(() => {
  setActivePinia(createPinia());
  vi.useFakeTimers();
  getWip.mockReset();
  getWip.mockResolvedValue(view);
});

afterEach(() => vi.useRealTimers());

describe('the WIP store', () => {
  it('polls only while something watches, once however many readers there are', async () => {
    const wip = useWipStore();

    wip.watch();
    wip.watch();
    await vi.advanceTimersByTimeAsync(0);
    expect(getWip).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(WipPollMs);
    expect(getWip).toHaveBeenCalledTimes(2);

    wip.unwatch();
    await vi.advanceTimersByTimeAsync(WipPollMs);
    expect(getWip).toHaveBeenCalledTimes(3);

    wip.unwatch();
    await vi.advanceTimersByTimeAsync(WipPollMs * 3);
    expect(getWip).toHaveBeenCalledTimes(3);
  });

  it('answers per team and per member', async () => {
    const wip = useWipStore();
    await wip.refresh();

    expect(wip.runningFor('alpha')).toHaveLength(1);
    expect(wip.waitingFor('alpha')).toHaveLength(1);
    expect(wip.isWaiting('alpha', 'Manager')).toBe(true);
    expect(wip.isWaiting('beta', 'Manager')).toBe(false);
    expect(wip.max).toBe(4);
  });

  it('keeps the last answer when a read fails', async () => {
    const wip = useWipStore();
    await wip.refresh();
    getWip.mockRejectedValue(new Error('down'));

    await wip.refresh();

    expect(wip.view).toEqual(view);
    expect(wip.error).toBe('down');
  });
});

describe('the words', () => {
  it('reads a team line as running and waiting', () => {
    expect(wipLine(2, 1)).toBe('2 running, 1 waiting');
    expect(wipLine(0, 0)).toBe('');
  });

  it('reads the In Progress header against the limit, and without one at 0', () => {
    expect(inProgressHeader(3, 4)).toBe('3 / 4 running');
    expect(inProgressHeader(3, 0)).toBe('3 running');
    expect(inProgressHeader(3, null)).toBe('3 running');
  });
});
