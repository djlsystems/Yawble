// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';
import BoardView from '../../src/renderer/src/board/BoardView.vue';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import TeamDeliverables from '../../src/renderer/src/components/TeamDeliverables.vue';
import TeamsList from '../../src/renderer/src/components/TeamsList.vue';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import DeliverablesView from '../../src/renderer/src/deliverables/DeliverablesView.vue';
import { mountPane, paneCore, paneRow } from './deliverable-pane-fake';
import { backlogRequest, deliverablesRequest } from '../../src/renderer/src/deliverables/page';

// The ways to the Deliverables page - the sidebar item with its open count, the board's Deliverables button
// (filtered to the board's team), a team's "All deliverables", the team page's Deliverables button and a Teams-list tile's
// deliverable (the team, and that deliverable selected) - and the item and deliverable links: the item label in the page's pane
// and a team tile's deliverables, the Backlog opened at an item, and a Backlog item's "deliverable #N".
// Fakes only.

enableAutoUnmount(afterEach);
afterEach(() => { deliverablesRequest.value = null; backlogRequest.value = null; localStorage.clear(); history.replaceState(null, '', '/'); document.body.innerHTML = ''; });

const at = '2026-11-19T09:00:00Z';
const wf = (id: number) => ({ id, title: `Deliverable ${id}`, owner: 'Manager', state: 'open', undeclared: false, busy: [], ending: null, startedAt: at, lastAt: at, messages: 2 });
const listAnswer = (backlog = true) => ({
  deliverables: [{ ...wf(10), team: 'payments', teamId: 1, status: 'idle', item: { label: 'B096', title: 'Partial refunds', archived: false }, members: [] }, { ...wf(11), team: 'payments', teamId: 1, status: 'idle', item: null, members: [] }],
  counts: { all: 2, open: 2 }, truncated: false, backlog, now: at,
});
function api(answers: Record<string, (p: any) => unknown>, calls: { method: string; params: any }[] = []) {
  return { call: async (method: string, params: any) => { calls.push({ method, params }); return answers[method]?.(params) ?? {}; } } as unknown as YawbleApi;
}

describe('the sidebar', () => {
  const props = { view: 'backlog' as const, conversations: [], teams: [], conversationId: null, teamId: null, slots: null, remote: false };
  it('has Deliverables directly above Backlog, below Goals, with the open count as a badge', async () => {
    const w = mount(Sidebar, { props: { ...props, openDeliverables: 4 } });
    const places = w.findAll('nav.places button').map((b) => b.attributes('data-test'));
    expect(places.indexOf('nav-deliverables')).toBe(places.indexOf('nav-backlog') - 1);
    expect(places.indexOf('nav-deliverables')).toBe(places.indexOf('nav-goals') + 1);
    expect(w.get('[data-test=nav-deliverables-count]').text()).toBe('4');
    await w.get('[data-test=nav-deliverables]').trigger('click');
    expect(w.emitted('go')).toEqual([['deliverables']]);
  });
  it('no badge when nothing is open or the count is not known', () => {
    expect(mount(Sidebar, { props: { ...props, openDeliverables: 0 } }).find('[data-test=nav-deliverables-count]').exists()).toBe(false);
    expect(mount(Sidebar, { props: { ...props, openDeliverables: null } }).find('[data-test=nav-deliverables-count]').exists()).toBe(false);
  });
  it('the folded rail has it too', () => {
    const w = mount(Sidebar, { props: { ...props, rail: true, openDeliverables: 2 } });
    expect(w.find('[data-test=rail-deliverables]').exists()).toBe(true);
    expect(w.get('[data-test=rail-deliverables-count]').text()).toBe('2');
  });
});

describe("the board's Deliverables button", () => {
  const board = () => ({ lanes: [{ id: 'todo', label: 'To Do' }], cards: [], teams: [{ id: 1, name: 'payments' }, { id: 2, name: 'billing' }] });
  it('sits beside Backlog and opens the page filtered to the team the board shows', async () => {
    const w = mount(BoardView, { props: { api: api({ 'board.list': board }), pollMs: 600_000 } });
    await flushPromises();
    const buttons = w.findAll('.filters button').map((b) => b.attributes('data-test'));
    expect(buttons.indexOf('board-deliverables')).toBe(buttons.indexOf('board-backlog') + 1);
    await w.get('[data-test=board-team]').setValue('billing');
    await w.get('[data-test=board-deliverables]').trigger('click');
    expect(deliverablesRequest.value).toMatchObject({ teamId: 2 });
  });
  it('with every team shown, all teams', async () => {
    const w = mount(BoardView, { props: { api: api({ 'board.list': board }), pollMs: 600_000 } });
    await flushPromises();
    await w.get('[data-test=board-deliverables]').trigger('click');
    expect(deliverablesRequest.value).toMatchObject({ teamId: null });
  });
});

describe("the team page's Deliverables, a team tile and the page's pane", () => {
  const team = { id: 1, name: 'payments', repoPath: null, members: 0, running: 0, queued: 0 } as TeamSummary;
  const withItem = paneRow({ id: 10, item: { label: 'B006', title: 'Partial refunds', archived: false } });
  it('the Deliverables button on the team page opens the page filtered to the team, nothing selected; no dialog', async () => {
    const w = mount(TeamView, { props: { team, api: api({ 'teams.agents': () => [], 'bus.decisions': () => [], 'integrations.asks': () => [] }), pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=open-deliverables]').trigger('click');
    expect(deliverablesRequest.value).toMatchObject({ teamId: 1 });
    expect(deliverablesRequest.value?.deliverable ?? null).toBeNull();
    expect(document.querySelector('[role=dialog]')).toBeNull();
  });
  it('a deliverable on a Teams-list tile opens the page filtered to that team with the deliverable selected; no dialog', async () => {
    const other = { ...team, id: 2, name: 'billing' };
    const w = mount(TeamsList, { props: { teams: [team, other], archived: [], api: api({ 'bus.deliverables': (p) => (p?.teamId === 2 ? [wf(30)] : [wf(10)]), 'deliverables.list': () => listAnswer() }), pollMs: 600_000, refresh: () => {} }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=team-deliverable][data-id="30"]').trigger('click');
    expect(deliverablesRequest.value).toMatchObject({ teamId: 2, deliverable: 30 });
    expect(document.querySelector('[role=dialog]')).toBeNull();
    expect(w.emitted('open')).toBeUndefined(); // the list does not open the team's page
  });
  it("the page opened from there lands with the team filter, All, and the deliverable's pane", async () => {
    const core = paneCore(() => [withItem, paneRow({ id: 11 }), paneRow({ id: 30, team: 'billing', teamId: 2, status: 'completed' })]);
    const w = mount(DeliverablesView, { props: { teams: [team, { ...team, id: 2, name: 'billing' }], api: core.api, pollMs: 600_000, request: { teamId: 2, deliverable: 30, seq: 1 } }, attachTo: document.body });
    await flushPromises();
    expect((w.get('[data-test=deliverables-team-filter]').element as HTMLSelectElement).value).toBe('2');
    expect(w.get('[data-test=deliverables-chip-all]').classes()).toContain('on');
    expect(core.calls.find((c) => c.method === 'deliverables.list')!.params).toMatchObject({ teamId: 2, state: 'all' });
    expect(w.get('[data-test=deliverable-pane]').attributes('data-id')).toBe('30');
    w.unmount();
  });
  it("the pane shows the item label as a link to the Backlog; without backlog access it is plain text; none without an item", async () => {
    let w = await mountPane(withItem, paneCore(() => [withItem]).api);
    const link = w.get('[data-test=deliverable-pane] [data-test=deliverable-item-link]');
    expect(link.text()).toBe('B006');
    await link.trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: 'B006', archived: false });
    w.unmount();
    w = await mountPane(withItem, paneCore(() => [withItem], { backlog: false }).api);
    expect(w.get('[data-test=deliverable-item-text]').text()).toBe('B006');
    expect(w.find('[data-test=deliverable-item-link]').exists()).toBe(false);
    w.unmount();
    const plain = paneRow({ id: 11 });
    w = await mountPane(plain, paneCore(() => [plain]).api);
    expect(w.find('[data-test=deliverable-item-link]').exists()).toBe(false);
    expect(w.find('[data-test=deliverable-item-none]').exists()).toBe(false);
  });
  it("a team tile's deliverable shows its item as a link inside the row, and All deliverables opens the page", async () => {
    const calls: { method: string; params: any }[] = [];
    const w = mount(TeamDeliverables, { props: { teamId: 1, api: api({ 'bus.deliverables': () => [wf(10), wf(11)], 'deliverables.list': () => listAnswer() }, calls), pollMs: 600_000 } });
    await flushPromises();
    const link = w.get('[data-id="10"] [data-test=deliverable-item-link]');
    expect(link.element.tagName).toBe('SPAN'); // not a button inside the row's button
    expect(link.attributes('role')).toBe('link');
    await link.trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: 'B096' });
    expect(w.emitted('open')).toBeUndefined(); // the row did not open
    await w.get('[data-test=team-deliverables-all]').trigger('click');
    expect(deliverablesRequest.value).toMatchObject({ teamId: 1 });
    expect(calls.filter((c) => c.method === 'deliverables.list')).toHaveLength(1);
  });
});

describe('the Backlog', () => {
  const item = (id: number, label: string, archivedAt: string | null = null) => ({
    id, label, team: null, title: `Item ${label}`, body: 'spec', state: 'declared', archivedAt, dispatchedTeam: 'payments', dispatchedDeliverable: 20, dispatchedAt: at, inFlight: false, createdAt: at, updatedAt: at, dispatchCount: 2,
  });
  const dispatch = (team: string, teamId: number, deliverable: number) => ({ team, teamId, teamGone: false, deliverable, dispatchedAt: at, dispatchedBy: null, state: 'open', ending: null, busy: null, lastAt: at, cards: 1, cardsDone: 0, runs: 1, members: [], inputTokens: 0, outputTokens: 0, firstStart: null, lastEnd: null });
  const answers = {
    'backlog.list': (p: any) => (p?.archived ? [item(9, 'B050', at)] : [item(1, 'B096'), item(2, 'B097')]),
    // newest dispatch first: payments' latest deliverable is #21, billing's #30
    'backlog.item': (p: any) => ({ ...item(p.id, p.id === 2 ? 'B097' : 'B096'), dispatches: [dispatch('payments', 1, 21), dispatch('billing', 2, 30), dispatch('payments', 1, 20)] }),
  };
  it('opens at the item an Item link asked for', async () => {
    const w = mount(BacklogView, { props: { teams: [], api: api(answers), pollMs: 600_000, focus: { label: 'B097', archived: false, seq: 1 } } });
    await flushPromises();
    expect(w.get('[data-test=backlog-item] .mono').text()).toBe('B097');
  });
  it('an archived item opens in the archive view', async () => {
    const calls: { method: string; params: any }[] = [];
    const w = mount(BacklogView, { props: { teams: [], api: api(answers, calls), pollMs: 600_000, focus: { label: 'B050', archived: true, seq: 2 } } });
    await flushPromises();
    expect(w.get('[data-test=backlog-tab-archived]').classes()).toContain('on');
    expect(calls.some((c) => c.method === 'backlog.list' && c.params.archived === true)).toBe(true);
    expect(w.get('[data-test=backlog-item] .mono').text()).toBe('B050');
  });
  it("each team row gains that team's latest deliverable, one line per team, linking to the page with it selected", async () => {
    const w = mount(BacklogView, { props: { teams: [], api: api(answers), pollMs: 600_000 } });
    await flushPromises();
    await w.findAll('[data-test=backlog-row]')[0].trigger('click');
    await flushPromises();
    const rows = w.findAll('[data-test=backlog-team-links]');
    expect(rows).toHaveLength(2);
    expect(rows.map((r) => r.get('[data-test=backlog-team-deliverable]').text())).toEqual(['deliverable #21', 'deliverable #30']);
    await rows[1].get('[data-test=backlog-team-deliverable]').trigger('click');
    expect(deliverablesRequest.value).toMatchObject({ teamId: 2, deliverable: 30 });
  });
});
