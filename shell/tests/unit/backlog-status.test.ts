// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import type { YawbleApi } from '../../src/shared/api';

const item = (id: number, over: Record<string, unknown>) => ({
  id, label: `B00${id}`, team: 'payments', title: `Item ${id}`, body: '', state: 'ready', position: id, createdAt: '', updatedAt: '',
  archivedAt: null, dispatchedTeam: null, dispatchedWorkflow: null, dispatchedAt: null, inFlight: false, dispatchCount: 0, ...over,
});
const items = [
  item(1, { state: 'pending' }),
  item(2, { state: 'ready' }),
  item(3, { state: 'ready', dispatchedTeam: 'payments', dispatchedWorkflow: 9, dispatchedAt: '2026-11-11T10:00:00Z', dispatchCount: 1 }),
  item(4, { state: 'ready', dispatchedTeam: 'payments', dispatchedWorkflow: 10, dispatchedAt: '2026-11-11T10:00:00Z', dispatchCount: 1, inFlight: true }),
  item(5, { state: 'declared', dispatchedTeam: 'payments', dispatchedWorkflow: 11, dispatchedAt: '2026-11-11T10:00:00Z', dispatchCount: 1 }),
];
const api = { call: async (m: string) => (m === 'backlog.list' ? items : []), onEvent: () => () => {} } as unknown as YawbleApi;

describe("the backlog's status says what is happening to an item", () => {
  it('shows In flight while its cards are worked and Dispatched once sent, instead of Ready', async () => {
    const w = mount(BacklogView, { props: { teams: [], api, pollMs: 60_000 } });
    await flushPromises();
    const status = w.findAll('[data-test=backlog-row]').map((r) => r.get('[data-test=backlog-status]').text());
    expect(status).toEqual(['Pending', 'Ready', 'Dispatched', 'In flight', 'Declared done']);
    // The status says it, so the title carries no separate "in flight" tag.
    expect(w.findAll('[data-test=backlog-row]')[3].findAll('td')[2].text()).toBe('Item 4');
  });
});
