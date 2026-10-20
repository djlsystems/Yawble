import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listAgentUpdates } = vi.hoisted(() => ({ listAgentUpdates: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listAgentUpdates,
}));

import { AgentUpdatesIdlePollMs, AgentUpdatesPollMs, useAgentUpdatesStore } from '../agentUpdates';

const waiting = {
  command: 'claude', phase: 'waiting', since: '', running: [], held: [{ team: 'alpha', member: 'Dev' }],
};

beforeEach(() => {
  setActivePinia(createPinia());
  vi.useFakeTimers();
  listAgentUpdates.mockReset();
  listAgentUpdates.mockResolvedValue([]);
});

afterEach(() => vi.useRealTimers());

describe('the agent updates store', () => {
  it('reads once a minute while no update is in the gate, and every five seconds while one is', async () => {
    const updates = useAgentUpdatesStore();

    updates.watch();
    await vi.advanceTimersByTimeAsync(0);
    expect(listAgentUpdates).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(AgentUpdatesPollMs * 3);
    expect(listAgentUpdates).toHaveBeenCalledTimes(1);

    listAgentUpdates.mockResolvedValue([waiting]);
    await vi.advanceTimersByTimeAsync(AgentUpdatesIdlePollMs - AgentUpdatesPollMs * 3);
    expect(listAgentUpdates).toHaveBeenCalledTimes(2);
    expect(updates.heldBy('alpha', 'Dev')).toBe('claude');

    await vi.advanceTimersByTimeAsync(AgentUpdatesPollMs);
    expect(listAgentUpdates).toHaveBeenCalledTimes(3);

    updates.unwatch();
    await vi.advanceTimersByTimeAsync(AgentUpdatesIdlePollMs * 2);
    expect(listAgentUpdates).toHaveBeenCalledTimes(3);
  });

  it('re-reads at once when a held member is pushed, one read in flight at a time', async () => {
    const updates = useAgentUpdatesStore();
    updates.watch();
    await vi.advanceTimersByTimeAsync(0);
    expect(listAgentUpdates).toHaveBeenCalledTimes(1);

    updates.onHeld();
    updates.onHeld();
    await vi.advanceTimersByTimeAsync(0);
    expect(listAgentUpdates).toHaveBeenCalledTimes(2);
  });

  it('ignores a held member while nobody watches', async () => {
    const updates = useAgentUpdatesStore();

    updates.onHeld();
    await vi.advanceTimersByTimeAsync(0);

    expect(listAgentUpdates).not.toHaveBeenCalled();
  });
});
