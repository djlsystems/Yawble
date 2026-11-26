// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import type { YawbleApi } from '../../src/shared/api';

const wf = (id: number, state: string, busy: string[], lastAt: string) =>
  ({ id, title: `Deliverable number ${id}`, owner: 'Manager', state, undeclared: false, busy, ending: null, startedAt: '', lastAt, messages: 2 });
const list = [
  wf(1, 'closed', [], '2026-11-10T08:00:00Z'), wf(2, 'completed', [], '2026-11-11T09:00:00Z'), wf(3, 'open', ['Ines'], '2026-11-11T12:00:00Z'),
  wf(4, 'open', [], '2026-11-11T11:00:00Z'), wf(5, 'completed', [], '2026-11-11T10:00:00Z'), wf(6, 'completed', [], '2026-11-09T10:00:00Z'),
];

const agent = (id: number, name: string, role: string, state: string, over: Record<string, unknown> = {}) => ({
  member: { id, teamId: 1, team: 'payments', name, role, cli: 'claude' }, state, attention: null, queuePosition: null, queueLength: 0,
  queuedInstructions: 0, statusLine: null, runId: null, runStartedAt: null, lastRunState: null, lastRunReason: null, loosened: [], policyProblems: [], ...over,
});
const agents = [
  agent(1, 'Manager', 'manager', 'idle'),
  agent(2, 'DeveloperInes', 'member', 'running', { statusLine: 'Card 12: wiring the overview\nmore detail' }),
  agent(3, 'TesterWren', 'member', 'queued', { queuePosition: 2 }),
];

// The same deliverables as the Deliverables page's rows (deliverables.list), for where a tile's deliverable leads.
const rows = list.map((w) => ({ ...w, team: 'payments', teamId: 1, status: w.state === 'open' ? (w.busy.length ? 'running' : 'idle') : w.state, working: w.busy.length > 0,
  endedAt: w.state === 'open' ? null : w.lastAt, expiredAt: null, expiresAt: null, expiryWarning: null, question: null, time: null, waitingFor: null, item: null, members: [] }));

let asked: any[] = [];
let pageAsked: any[] = [];
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
        case 'bus.deliverables': asked.push(p); return (p?.teamId === 1 ? list : []) as any;
        case 'teams.agents': return (p?.teamId === 1 ? agents : []) as any;
        case 'deliverables.list': pageAsked.push(p); return { deliverables: rows.filter((r) => p?.teamId === undefined || r.teamId === p.teamId), counts: { all: rows.length, open: 2 }, truncated: false, backlog: true, now: '2026-11-11T12:00:00Z' } as any;
        case 'deliverables.get': return { deliverable: rows.find((r) => r.id === p?.deliverable), cards: [], messages: [], spec: null, backlog: true } as any;
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

describe("each team's card lists its recent deliverables", () => {
  beforeEach(() => { asked = []; pageAsked = []; localStorage.clear(); sessionStorage.clear(); document.body.innerHTML = ''; history.replaceState(null, '', '/'); });

  it('shows up to the 50 newest (every one the core lists), last updated first, with their state and last-entry time', async () => {
    const w = await teamsPage();
    expect(asked.filter((p) => p?.teamId === 1).every((p) => p.limit === 50)).toBe(true);
    const card = w.findAll('[data-test=team-card]')[0];
    const rows = card.findAll('[data-test=team-deliverable]');
    expect(rows.map((r) => r.attributes('data-id'))).toEqual(['3', '4', '5', '2', '1', '6']);
    expect(rows.map((r) => r.attributes('data-status'))).toEqual(['working', 'waiting', 'completed', 'completed', 'closed', 'completed']);
    expect(rows[0].text()).toContain('#3');
    expect(rows[0].text()).toContain('Deliverable number 3');
    expect(rows[0].get('[data-test=team-deliverable-time]').text()).not.toBe('');
    expect(rows[0].attributes('title')).toContain('Being worked');
    w.unmount();
  });

  it("shows each member's avatar with its state, and a tooltip with name, state and what it is doing", async () => {
    const w = await teamsPage();
    const card = w.findAll('[data-test=team-card]')[0];
    const faces = card.findAll('[data-test=team-member]');
    expect(faces.map((f) => f.attributes('data-state'))).toEqual(['idle', 'running', 'waiting']);
    expect(faces[1].attributes('title')).toBe('Developer Ines · member\nRunning\nCard 12: wiring the overview');
    expect(faces[2].attributes('title')).toBe('Tester Wren · member\nWaiting to start (#2 in the queue)');
    expect(card.text()).not.toContain('agents ·');
    // A team whose members are not known (yet) keeps the counts as text.
    expect(w.findAll('[data-test=team-card]')[1].get('[data-test=team-members-text]').text()).toBe('1 agents · 0 running · 0 queued');
    w.unmount();
  });

  it("the sidebar's Teams pill counts the teams, not the agents running", async () => {
    const w = await teamsPage();
    const pill = w.get('[data-test=nav-teams-count]');
    expect(pill.text()).toBe('2'); // two teams; one agent is running
    expect(pill.attributes('title')).toBe('2 teams, 1 running');
    expect(pill.classes()).toContain('live'); // green and pulsing while a team has work running
    w.unmount();
  });

  it('the Teams and Deliverables badges go back to the usual colour when every team is idle', async () => {
    const base = { view: 'teams', conversations: [], conversationId: null, teamId: null, slots: null, remote: false };
    const w = mount(Sidebar, { props: { ...base, teams: [
      { id: 1, name: 'a', repoPath: null, members: 2, running: 0, queued: 0 },
      { id: 2, name: 'b', repoPath: null, members: 1, running: 0, queued: 1 },
    ], openDeliverables: 3 } as any });
    expect(w.get('[data-test=nav-teams-count]').classes()).not.toContain('live');
    expect(w.get('[data-test=nav-teams-count]').attributes('title')).toBe('2 teams, all idle');
    expect(w.get('[data-test=nav-deliverables-count]').classes()).not.toContain('live');
    await w.setProps({ teams: [{ id: 1, name: 'a', repoPath: null, members: 2, running: 1, queued: 0 }] });
    expect(w.get('[data-test=nav-deliverables-count]').classes()).toContain('live');
    expect(w.get('[data-test=nav-deliverables-count]').attributes('title')).toBe('3 open deliverables, work running');
    w.unmount();
  });

  it('says so when a team has no deliverables yet', async () => {
    const w = await teamsPage();
    expect(w.findAll('[data-test=team-card]')[1].get('[data-test=team-deliverables-empty]').text()).toContain('No deliverables yet');
    w.unmount();
  });

  it("a deliverable on a tile opens the Deliverables page filtered to the team, with the clicked one selected and its pane open", async () => {
    const w = await teamsPage();
    await w.findAll('[data-test=team-deliverable]').find((r) => r.attributes('data-id') === '4')!.trigger('click');
    for (let i = 0; i < 6; i++) await flushPromises();
    // The Deliverables page, not a dialog over the list: filtered to the tile's team, on All, #4 selected with its pane open.
    await vi.waitFor(() => expect(w.find('[data-test=deliverable-pane]').exists()).toBe(true));
    expect(w.find('[data-test=deliverables-view]').exists()).toBe(true);
    expect(w.find('[data-test=team-card]').exists()).toBe(false);
    expect(w.find('[data-test=deliverables]').exists()).toBe(false);
    expect((w.get('[data-test=deliverables-team-filter]').element as HTMLSelectElement).value).toBe('1');
    expect(w.get('[data-test=deliverables-chip-all]').classes()).toContain('on');
    expect(pageAsked.at(-1)).toMatchObject({ teamId: 1, state: 'all' });
    expect(w.get('[data-test=deliverables-row][data-wf="4"]').attributes('aria-selected')).toBe('true');
    expect(w.get('[data-test=deliverable-pane]').attributes('data-id')).toBe('4');
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
