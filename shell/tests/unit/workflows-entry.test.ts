// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';
import BoardView from '../../src/renderer/src/board/BoardView.vue';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import WorkflowsDialog from '../../src/renderer/src/components/WorkflowsDialog.vue';
import TeamWorkflows from '../../src/renderer/src/components/TeamWorkflows.vue';
import { backlogRequest, workflowsRequest } from '../../src/renderer/src/workflows/page';

// The ways to the Workflows page - the sidebar item with its open count, the board's Workflows button
// (filtered to the board's team), a team's "All workflows" - and the item and workflow links it takes over: the item label in the
// Workflows dialog and a team tile's workflows, the Backlog opened at an item, and a Backlog item's "workflow #N".
// Fakes only.

enableAutoUnmount(afterEach);
afterEach(() => { workflowsRequest.value = null; backlogRequest.value = null; document.body.innerHTML = ''; });

const at = '2026-11-19T09:00:00Z';
const wf = (id: number) => ({ id, title: `Workflow ${id}`, owner: 'Manager', state: 'open', undeclared: false, busy: [], ending: null, startedAt: at, lastAt: at, messages: 2 });
const listAnswer = (backlog = true) => ({
  workflows: [{ ...wf(10), team: 'payments', teamId: 1, status: 'idle', item: { label: 'B096', title: 'Partial refunds', archived: false }, members: [] }, { ...wf(11), team: 'payments', teamId: 1, status: 'idle', item: null, members: [] }],
  counts: { all: 2, open: 2 }, truncated: false, backlog, now: at,
});
function api(answers: Record<string, (p: any) => unknown>, calls: { method: string; params: any }[] = []) {
  return { call: async (method: string, params: any) => { calls.push({ method, params }); return answers[method]?.(params) ?? {}; } } as unknown as YawbleApi;
}

describe('the sidebar', () => {
  const props = { view: 'backlog' as const, conversations: [], teams: [], conversationId: null, teamId: null, slots: null, remote: false };
  it('has Workflows directly below Backlog, with the open count as a badge', async () => {
    const w = mount(Sidebar, { props: { ...props, openWorkflows: 4 } });
    const places = w.findAll('nav.places button').map((b) => b.attributes('data-test'));
    expect(places.indexOf('nav-workflows')).toBe(places.indexOf('nav-backlog') + 1);
    expect(w.get('[data-test=nav-workflows-count]').text()).toBe('4');
    await w.get('[data-test=nav-workflows]').trigger('click');
    expect(w.emitted('go')).toEqual([['workflows']]);
  });
  it('no badge when nothing is open or the count is not known', () => {
    expect(mount(Sidebar, { props: { ...props, openWorkflows: 0 } }).find('[data-test=nav-workflows-count]').exists()).toBe(false);
    expect(mount(Sidebar, { props: { ...props, openWorkflows: null } }).find('[data-test=nav-workflows-count]').exists()).toBe(false);
  });
  it('the folded rail has it too', () => {
    const w = mount(Sidebar, { props: { ...props, rail: true, openWorkflows: 2 } });
    expect(w.find('[data-test=rail-workflows]').exists()).toBe(true);
    expect(w.get('[data-test=rail-workflows-count]').text()).toBe('2');
  });
});

describe("the board's Workflows button", () => {
  const board = () => ({ lanes: [{ id: 'todo', label: 'To Do' }], cards: [], teams: [{ id: 1, name: 'payments' }, { id: 2, name: 'billing' }] });
  it('sits beside Backlog and opens the page filtered to the team the board shows', async () => {
    const w = mount(BoardView, { props: { api: api({ 'board.list': board }), pollMs: 600_000 } });
    await flushPromises();
    const buttons = w.findAll('.filters button').map((b) => b.attributes('data-test'));
    expect(buttons.indexOf('board-workflows')).toBe(buttons.indexOf('board-backlog') + 1);
    await w.get('[data-test=board-team]').setValue('billing');
    await w.get('[data-test=board-workflows]').trigger('click');
    expect(workflowsRequest.value).toMatchObject({ teamId: 2 });
  });
  it('with every team shown, all teams', async () => {
    const w = mount(BoardView, { props: { api: api({ 'board.list': board }), pollMs: 600_000 } });
    await flushPromises();
    await w.get('[data-test=board-workflows]').trigger('click');
    expect(workflowsRequest.value).toMatchObject({ teamId: null });
  });
});

describe('the Workflows dialog (the team page) and a team tile', () => {
  const team = { id: 1, name: 'payments' } as TeamSummary;
  it('the dialog shows the item label as a link to the Backlog, and links to the page filtered to the team', async () => {
    const w = mount(WorkflowsDialog, { props: { team, api: api({ 'bus.workflows': () => [wf(10), wf(11)], 'workflows.list': () => listAnswer() }), pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    const links = w.findAll('[data-test=workflow-10] [data-test=workflow-item-link]');
    expect(links.map((l) => l.text())).toEqual(['B096']);
    expect(w.find('[data-test=workflow-11] [data-test=workflow-item-link]').exists()).toBe(false);
    expect(w.find('[data-test=workflow-11] [data-test=workflow-item-none]').exists()).toBe(false); // no "—" in the dialog
    await links[0].trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: 'B096', archived: false });
    await w.get('[data-test=workflows-all]').trigger('click');
    expect(workflowsRequest.value).toMatchObject({ teamId: 1 });
    expect(w.emitted('close')).toHaveLength(1);
  });
  it('without backlog access the label is plain text; an older core (no workflows.list) shows none', async () => {
    let w = mount(WorkflowsDialog, { props: { team, api: api({ 'bus.workflows': () => [wf(10)], 'workflows.list': () => listAnswer(false) }), pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=workflow-10] [data-test=workflow-item-text]').text()).toBe('B096');
    w.unmount();
    const old = { call: async (m: string) => { if (m === 'bus.workflows') return [wf(10)]; throw Object.assign(new Error('nope'), { code: 'method_not_found' }); } } as unknown as YawbleApi;
    w = mount(WorkflowsDialog, { props: { team, api: old, pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    expect(w.find('[data-test=workflow-10]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-item-link]').exists()).toBe(false);
  });
  it("a team tile's workflow shows its item as a link inside the row, and All workflows opens the page", async () => {
    const calls: { method: string; params: any }[] = [];
    const w = mount(TeamWorkflows, { props: { teamId: 1, api: api({ 'bus.workflows': () => [wf(10), wf(11)], 'workflows.list': () => listAnswer() }, calls), pollMs: 600_000 } });
    await flushPromises();
    const link = w.get('[data-id="10"] [data-test=workflow-item-link]');
    expect(link.element.tagName).toBe('SPAN'); // not a button inside the row's button
    expect(link.attributes('role')).toBe('link');
    await link.trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: 'B096' });
    expect(w.emitted('open')).toBeUndefined(); // the row did not open
    await w.get('[data-test=team-workflows-all]').trigger('click');
    expect(workflowsRequest.value).toMatchObject({ teamId: 1 });
    expect(calls.filter((c) => c.method === 'workflows.list')).toHaveLength(1);
  });
});

describe('the Backlog', () => {
  const item = (id: number, label: string, archivedAt: string | null = null) => ({
    id, label, team: null, title: `Item ${label}`, body: 'spec', state: 'declared', archivedAt, dispatchedTeam: 'payments', dispatchedWorkflow: 20, dispatchedAt: at, inFlight: false, createdAt: at, updatedAt: at, dispatchCount: 2,
  });
  const dispatch = (team: string, teamId: number, workflow: number) => ({ team, teamId, teamGone: false, workflow, dispatchedAt: at, dispatchedBy: null, state: 'open', ending: null, busy: null, lastAt: at, cards: 1, cardsDone: 0, runs: 1, members: [], inputTokens: 0, outputTokens: 0, firstStart: null, lastEnd: null });
  const answers = {
    'backlog.list': (p: any) => (p?.archived ? [item(9, 'B050', at)] : [item(1, 'B096'), item(2, 'B097')]),
    // newest dispatch first: payments' latest workflow is #21, billing's #30
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
  it("each team row gains that team's latest workflow, one line per team, linking to the page with it selected", async () => {
    const w = mount(BacklogView, { props: { teams: [], api: api(answers), pollMs: 600_000 } });
    await flushPromises();
    await w.findAll('[data-test=backlog-row]')[0].trigger('click');
    await flushPromises();
    const rows = w.findAll('[data-test=backlog-team-links]');
    expect(rows).toHaveLength(2);
    expect(rows.map((r) => r.get('[data-test=backlog-team-workflow]').text())).toEqual(['workflow #21', 'workflow #30']);
    await rows[1].get('[data-test=backlog-team-workflow]').trigger('click');
    expect(workflowsRequest.value).toMatchObject({ teamId: 2, workflow: 30 });
  });
});
