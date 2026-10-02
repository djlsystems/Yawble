// @vitest-environment happy-dom
//
// THE RUNNING CONCIERGE SESSIONS, in the Concierge tab of Tenant Settings: each session's worker,
// viewer, last activity, when the idle window would end it - blank while open - and its memory,
// with End for each.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';

const { concierge, endConciergeOf } = vi.hoisted(() => ({
  concierge: vi.fn(),
  endConciergeOf: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  endConciergeOf,
}));

import ConciergeSessions from '../ConciergeSessions.vue';
import { resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import type { ConciergeSessionView } from '../../api/types';

const session = (overrides: Partial<ConciergeSessionView> = {}): ConciergeSessionView => ({
  user: 'user-1',
  email: 'person@example.test',
  worker: 'worker-1',
  startedAt: '2026-10-02T12:00:00Z',
  viewer: false,
  lastViewerAt: '2026-10-02T12:04:00Z',
  lastActivityAt: '2026-10-02T12:20:00Z',
  lastActivity: 'output',
  callsInFlight: 0,
  outputFloor: { bytesPerMinute: 2048, source: 'default', measuredWith: null },
  outputPerMinute: [0, 4096],
  wouldEndAt: '2026-10-02T13:20:00Z',
  memory: { residentBytes: 300 * 1024 * 1024, processes: 4, sampledAt: '2026-10-02T12:20:00Z' },
  ...overrides,
});

beforeEach(() => {
  concierge.mockReset();
  endConciergeOf.mockReset();
  endConciergeOf.mockResolvedValue(undefined);
});

afterEach(resetBody);

async function mounted(sessions: ConciergeSessionView[]) {
  concierge.mockResolvedValue({ agent: null, sessions });
  const wrapper = mount(ConciergeSessions, { attachTo: document.body });
  await settle();
  return wrapper;
}

describe('ConciergeSessions, mounted', () => {
  it('lists each session with its worker, last activity, would-end-at and memory', async () => {
    const wrapper = await mounted([session()]);

    const row = wrapper.find('.concierge-session');
    expect(row.text()).toContain('person@example.test');
    expect(row.text()).toContain('worker-1');
    expect(wrapper.find('.concierge-session-activity').text()).toContain('output');
    expect(wrapper.find('.concierge-session-ends').text()).toBe(new Date('2026-10-02T13:20:00Z').toLocaleString());
    expect(wrapper.find('.concierge-session-memory').text()).toBe('300 MB');
  });

  it('shows no end time while someone has the session open, and memory not measured as such', async () => {
    const wrapper = await mounted([session({ viewer: true, lastViewerAt: null, wouldEndAt: null, memory: null })]);

    expect(wrapper.find('.concierge-session-viewer').text()).toBe('open now');
    expect(wrapper.find('.concierge-session-ends').text()).toBe('not while open');
    expect(wrapper.find('.concierge-session-memory').text()).toBe('not measured');
  });

  it('ends the named person\'s session with End, and reads the list again', async () => {
    const wrapper = await mounted([session()]);
    concierge.mockResolvedValue({ agent: null, sessions: [] });

    await wrapper.find('.concierge-session-end').trigger('click');
    await settle();

    expect(endConciergeOf).toHaveBeenCalledWith('user-1');
    expect(wrapper.find('.concierge-sessions-none').exists()).toBe(true);
  });

  it('says when no session is running', async () => {
    const wrapper = await mounted([]);

    expect(wrapper.find('.concierge-sessions-none').text()).toBe('No Concierge session is running.');
  });
});
