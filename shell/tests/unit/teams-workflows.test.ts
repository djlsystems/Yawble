// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import type { YawbleApi } from '../../src/shared/api';

const wf = (id: number, state: string, busy: string[], lastAt: string) =>
  ({ id, title: `Workflow number ${id}`, owner: 'Manager', state, undeclared: false, busy, ending: null, startedAt: '', lastAt, messages: 2 });
const list = [
  wf(1, 'closed', [], '2026-11-10T08:00:00Z'), wf(2, 'completed', [], '2026-11-11T09:00:00Z'), wf(3, 'open', ['Ines'], '2026-11-11T12:00:00Z'),
  wf(4, 'open', [], '2026-11-11T11:00:00Z'), wf(5, 'completed', [], '2026-11-11T10:00:00Z'), wf(6, 'completed', [], '2026-11-09T10:00:00Z'),
];

function api(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, p?: any) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 1, limit: 8, queued: 0 }, teams: [
          { id: 1, name: 'payments', repoPath: 'C:/repos/payments', members: 3, running: 1, queued: 0 },
          { id: 2, name: 'quiet', repoPath: null, members: 1, running: 0, queued: 0 }] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'bus.workflows': return (p?.teamId === 1 ? list : []) as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

async function teamsPage() {
  const w = mount(App, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
  await flushPromises();
  await w.get('[data-test=nav-teams]').trigger('click');
  for (let i = 0; i < 4; i++) await flushPromises();
  return w;
}

describe("each team's card lists its recent workflows", () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); document.body.innerHTML = ''; });

  it('shows the five newest, last updated first, with their state and last-entry time', async () => {
    const w = await teamsPage();
    const card = w.findAll('[data-test=team-card]')[0];
    const rows = card.findAll('[data-test=team-workflow]');
    expect(rows.map((r) => r.attributes('data-id'))).toEqual(['3', '4', '5', '2', '1']);
    expect(rows.map((r) => r.attributes('data-status'))).toEqual(['working', 'waiting', 'completed', 'completed', 'closed']);
    expect(rows[0].text()).toContain('#3');
    expect(rows[0].text()).toContain('Workflow number 3');
    expect(rows[0].get('[data-test=team-workflow-time]').text()).not.toBe('');
    expect(rows[0].attributes('title')).toContain('Being worked');
    w.unmount();
  });

  it('says so when a team has no workflows yet', async () => {
    const w = await teamsPage();
    expect(w.findAll('[data-test=team-card]')[1].get('[data-test=team-workflows-empty]').text()).toContain('No workflows yet');
    w.unmount();
  });

  it("opens the team's workflows with the clicked one selected", async () => {
    const w = await teamsPage();
    await w.findAll('[data-test=team-workflow]').find((r) => r.attributes('data-id') === '4')!.trigger('click');
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(w.get('[data-test=workflow-4]').classes()).toContain('sel');
    w.unmount();
  });
  it("has a Settings button that opens the team's settings, which show its repository path (the card does not)", async () => {
    const w = await teamsPage();
    const card = w.findAll('[data-test=team-card]')[0];
    expect(card.text()).not.toContain('C:/repos/payments');
    await card.get('[data-test=card-settings]').trigger('click');
    for (let i = 0; i < 4; i++) await flushPromises();
    expect(w.get('[data-test=team-repo-path]').text()).toContain('C:/repos/payments');
    w.unmount();
  });
});
