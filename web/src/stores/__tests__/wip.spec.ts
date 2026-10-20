import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { getWip } = vi.hoisted(() => ({ getWip: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getWip,
}));

import { runningAcrossTeams, useWipStore, wipLine } from '../wip';
import { useCapacityStore } from '../capacity';
import { sample } from '../../test/capacityFixtures';

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
  it('reads once when first watched and never polls: every later figure is the capacity push', async () => {
    const wip = useWipStore();

    wip.watch();
    wip.watch();
    await vi.advanceTimersByTimeAsync(0);
    expect(getWip).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(60_000);
    expect(getWip).toHaveBeenCalledTimes(1);

    useCapacityStore().apply(sample({
      runs: {
        limit: 3, managerReserved: 1, runningCount: 1, waitingCount: 1,
        running: [{ team: 'beta', member: 'Dev1', since: '' }],
        waiting: [{ team: 'alpha', member: 'Manager', since: '', reason: 'waiting for memory: 11.2 of 12.9 GB in use' }],
      },
    }));

    expect(wip.max).toBe(3);
    expect(wip.runningCount).toBe(1);
    expect(wip.reasonFor('alpha', 'Manager')).toBe('waiting for memory: 11.2 of 12.9 GB in use');
    expect(wip.isWaiting('alpha', 'Manager')).toBe(true);
    expect(getWip).toHaveBeenCalledTimes(1);
  });

  it('does not read at all when a sample has already arrived', async () => {
    const wip = useWipStore();
    useCapacityStore().apply(sample());

    wip.watch();
    await vi.advanceTimersByTimeAsync(0);

    expect(getWip).not.toHaveBeenCalled();
    expect(wip.view).not.toBeNull();
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

  it('reads the ledger line against the limit, without one at 0, and says it is every team', () => {
    expect(runningAcrossTeams(3, 4)).toBe('3 / 4 agents running, all teams');
    expect(runningAcrossTeams(3, 0)).toBe('3 agents running, all teams');
    expect(runningAcrossTeams(3, null)).toBe('3 agents running, all teams');
  });
});
