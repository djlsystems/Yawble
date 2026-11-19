// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ItemTime, WorkflowCounts, WorkflowDetail, WorkflowRow, WorkflowsListAnswer, YawbleApi } from '../../src/shared/api';
import WorkflowsView from '../../src/renderer/src/workflows/WorkflowsView.vue';
import {
  backlogRequest, DefaultFilters, formatHash, highlight, inChip, loadFilters, matchesText, nextSort, parseHash, saveFilters, sortRows, statusTip,
  StorageKey, summaryLine, timeBar, workflowsRequest,
} from '../../src/renderer/src/workflows/page';
import { closeTimeline, timelineTarget } from '../../src/renderer/src/timeline/time';
import { confirmState } from '../../src/renderer/src/confirm';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';

// B104 (card 477): the Workflows page against a FAKE core answering the contract (YAWBLE_TEAM_DOCS/B104/contract.md):
// the filters (team, text with highlight, chips with counts), the hash and localStorage, the default order and the column
// sorts, the ticking elapsed time and its time bar, the Timeline link, the item link, the pane (spec, cards, activity,
// time), the actions (asked first, none for a Visitor) and reduced motion. Fakes only.

const NOW = Date.parse('2026-11-19T10:00:00Z');
const iso = (minAgo: number) => new Date(NOW - minAgo * 60_000).toISOString();
function time(ms: number, open: boolean, breakdown: [string, number][] = [['agent-working', ms]]): ItemTime {
  return { createdAt: iso(ms / 60_000), startedAt: null, endedAt: open ? null : iso(1), endedHow: open ? null : 'completed', open, overallMs: ms, rebuilt: false,
    breakdown: breakdown.map(([category, m]) => ({ category, ms: m })) as ItemTime['breakdown'] };
}
function row(p: Partial<WorkflowRow> & Pick<WorkflowRow, 'id' | 'status'>): WorkflowRow {
  const ended = ['completed', 'closed', 'expired'].includes(p.status);
  return {
    title: `Workflow ${p.id}`, owner: 'Manager', ownerId: 1, state: ended ? (p.status as WorkflowRow['state']) : 'open', undeclared: false, busy: [], ending: null,
    startedAt: iso(120), lastAt: iso(5), messages: 3, endedAt: ended ? iso(30) : null, expiredAt: null, expiresAt: null, expiryWarning: null, question: null,
    time: time(3_600_000, !ended), team: 'payments', teamId: 1, waitingFor: null, working: p.status === 'running', item: null, members: [], ...p,
  };
}
const rows: WorkflowRow[] = [
  row({ id: 10, status: 'completed', title: 'Refund model', endedAt: iso(60), lastAt: iso(60) }),
  row({ id: 11, status: 'running', title: 'Partial refunds', item: { label: 'B096', title: 'Partial refunds', archived: false }, members: [{ id: 2, name: 'DeveloperAda', state: 'running' }],
    time: time(5_400_000, true, [['agent-working', 3_600_000], ['stalled', 600_000], ['idle', 1_200_000]]) }),
  row({ id: 12, status: 'needs-you', title: 'Refund limits', question: 3, lastAt: iso(50) }),
  row({ id: 13, status: 'idle', title: 'Old export', team: 'billing', teamId: 2, expiresAt: new Date(NOW + 5 * 3_600_000).toISOString(), lastAt: iso(500),
    item: { label: 'B050', title: 'Old export', archived: true } }),
  row({ id: 14, status: 'closed', title: 'Abandoned idea', endedAt: iso(10), lastAt: iso(10) }),
];
const counts: WorkflowCounts = { all: 5, open: 3, needsYou: 1, done: 1, closed: 1, running: 1, waiting: 0, idle: 1, stopped: 0, completed: 1, expired: 0, completedToday: 1 };
const detail = (w: WorkflowRow): WorkflowDetail => ({
  workflow: w,
  cards: [
    { id: 476, title: 'B104 core', lane: 'in-progress', member: 'DeveloperTomas', status: 'running', time: time(600_000, true) },
    { id: 477, title: 'B104 shell', lane: 'todo', member: null, status: 'queued', time: time(60_000, true) },
  ],
  messages: [
    { seq: 9, at: iso(1), kind: 'progress', from: 'DeveloperAda', member: 'DeveloperAda', text: 'Writing the page' },
    { seq: 8, at: iso(9), kind: 'instruction', from: 'person', member: 'Manager', text: 'Build it' },
  ],
  spec: '**Why:** workflows are what the teams do.\n\n' + Array.from({ length: 12 }, (_, i) => `- line ${i}`).join('\n'),
  backlog: true,
});

interface Fake { api: YawbleApi; calls: { method: string; params: any }[]; backlog: boolean; list: WorkflowRow[] }
function fake(): Fake {
  const f: Fake = { calls: [], backlog: true, list: rows, api: null as unknown as YawbleApi };
  f.api = {
    call: async (method: string, params: any) => {
      f.calls.push({ method, params });
      if (method === 'board.card') return { id: params.id, team: 'payments', teamId: 1, member: null, item: null, title: 'B104 core', body: '', status: 'running', lane: 'in-progress', placed: false, awaitingManager: false, workflow: 11, createdAt: iso(10), updatedAt: iso(5), trailCount: 0, trail: [] };
      if (method === 'workflows.list') return { workflows: f.list, counts: f.list.length ? counts : { ...counts, all: 0, open: 0, needsYou: 0, done: 0, closed: 0 }, truncated: false, backlog: f.backlog, now: new Date(NOW).toISOString() } satisfies WorkflowsListAnswer;
      if (method === 'workflows.get') return { ...detail(f.list.find((w) => w.id === params.workflow)!), backlog: f.backlog, ...(f.backlog ? {} : { spec: null }) };
      return {};
    },
  } as unknown as YawbleApi;
  return f;
}
async function page(f = fake(), props: Record<string, unknown> = {}) {
  const w = mount(WorkflowsView, { props: { teams: [{ id: 1, name: 'payments' }, { id: 2, name: 'billing' }], api: f.api, pollMs: 600_000, ...props }, attachTo: document.body });
  await flushPromises();
  return w;
}
const ids = (w: Awaited<ReturnType<typeof page>>) => w.findAll('[data-test=workflows-row]').map((r) => Number(r.attributes('data-wf')));
const lastList = (f: Fake) => [...f.calls].reverse().find((c) => c.method === 'workflows.list')!.params;

enableAutoUnmount(afterEach);
beforeEach(() => {
  localStorage.clear();
  history.replaceState(null, '', '/');
  vi.spyOn(Date, 'now').mockReturnValue(NOW);
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
  applyRemoteAccess('full');
  closeTimeline();
  confirmState.value?.resolve(false);
  backlogRequest.value = null;
  workflowsRequest.value = null;
  document.body.innerHTML = '';
});

describe('the pure parts (page.ts)', () => {
  it('chips: Open is every open one; Needs you, Done and Closed/Expired by status', () => {
    expect(rows.filter((w) => inChip(w, 'open')).map((w) => w.id)).toEqual([11, 12, 13]);
    expect(rows.filter((w) => inChip(w, 'needs-you')).map((w) => w.id)).toEqual([12]);
    expect(rows.filter((w) => inChip(w, 'done')).map((w) => w.id)).toEqual([10]);
    expect(rows.filter((w) => inChip(w, 'closed')).map((w) => w.id)).toEqual([14]);
    expect(rows.filter((w) => inChip(w, 'all'))).toHaveLength(5);
  });

  it('text: every word must match one field (id, title, item, team, member); the matches are highlighted', () => {
    expect(rows.filter((w) => matchesText(w, 'b096 ada')).map((w) => w.id)).toEqual([11]);
    expect(rows.filter((w) => matchesText(w, '#13')).map((w) => w.id)).toEqual([13]);
    expect(rows.filter((w) => matchesText(w, 'billing')).map((w) => w.id)).toEqual([13]);
    expect(rows.filter((w) => matchesText(w, 'refund nothing')).map((w) => w.id)).toEqual([]);
    expect(highlight('Partial refunds', 'REF ial')).toEqual([{ text: 'Part', hit: false }, { text: 'ial', hit: true }, { text: ' ', hit: false }, { text: 'ref', hit: true }, { text: 'unds', hit: false }]);
    expect(highlight('abc', '')).toEqual([{ text: 'abc', hit: false }]);
  });

  it('the default order: needs you, running, other open, then ended ones newest end first', () => {
    expect(sortRows(rows, { key: 'default', desc: false }).map((w) => w.id)).toEqual([12, 11, 13, 14, 10]);
  });

  it('column sorts, and a header click flips the direction', () => {
    expect(sortRows(rows, { key: 'id', desc: false }).map((w) => w.id)).toEqual([10, 11, 12, 13, 14]);
    expect(sortRows(rows, { key: 'title', desc: false }).map((w) => w.id)).toEqual([14, 13, 11, 12, 10]);
    expect(sortRows(rows, { key: 'elapsed', desc: true })[0].id).toBe(11);
    expect(sortRows(rows, { key: 'status', desc: false }).map((w) => w.status)).toEqual(['needs-you', 'running', 'idle', 'completed', 'closed']);
    expect(sortRows(rows, { key: 'item', desc: false }).slice(0, 2).map((w) => w.id)).toEqual([13, 11]);
    expect(nextSort({ key: 'default', desc: false }, 'title')).toEqual({ key: 'title', desc: false });
    expect(nextSort({ key: 'title', desc: false }, 'title')).toEqual({ key: 'title', desc: true });
    expect(nextSort({ key: 'title', desc: false }, 'last')).toEqual({ key: 'last', desc: true });
  });

  it('the time bar has one segment per category with time, in the breakdown order, shares adding to 100', () => {
    const segs = timeBar(rows[1].time);
    expect(segs.map((s) => [s.category, s.label, s.ms])).toEqual([['agent-working', 'Agent working', 3_600_000], ['stalled', 'Stalled', 600_000], ['idle', 'Idle', 1_200_000]]);
    expect(segs.map((s) => s.pct)).toEqual([67, 11, 22]);
    expect(segs[0].color).toBe('var(--tl-agent-working)');
    expect(timeBar(null)).toEqual([]);
  });

  it('the hash keeps the filters and the selection, never with keys the remote hash reads', () => {
    const f = { teamId: 3, q: 'b096 ada', chip: 'needs-you' as const, sort: { key: 'last' as const, desc: true } };
    const h = formatHash(f, 12);
    expect(h).toBe('#workflows?t=3&q=b096+ada&s=needs-you&o=last.desc&sel=12');
    expect(h).not.toMatch(/[?&](team|workflow)=/);
    expect(parseHash(h)).toEqual({ ...f, selected: 12 });
    expect(formatHash(DefaultFilters, null)).toBe('#workflows');
    expect(parseHash('#workflow=10')).toBeNull();
    expect(parseHash('#workflows?s=bogus&t=-1')).toEqual({ teamId: null });
  });

  it('localStorage remembers the filters; anything broken reads as the defaults', () => {
    const f = { teamId: 2, q: 'x', chip: 'done' as const, sort: { key: 'team' as const, desc: true } };
    saveFilters(f);
    expect(loadFilters()).toEqual(f);
    localStorage.setItem(StorageKey, '{nope');
    expect(loadFilters()).toEqual(DefaultFilters);
  });

  it('the summary line and the idle tooltip with its expiry countdown', () => {
    expect(summaryLine(counts)).toBe('3 open · 1 needs you · 1 completed today');
    expect(statusTip(rows[3], NOW)).toBe('Open, nobody is working it. Expires in 5h 0m if nothing happens in it');
    expect(statusTip({ ...rows[3], status: 'waiting', waitingFor: 'lease' }, NOW)).toBe('Waiting for the heavy-work lease');
  });
});

describe('the Workflows page', () => {
  it('opens on the Open chip (default order), with the chips counted and the summary line', async () => {
    const f = fake();
    const w = await page(f);
    expect(lastList(f)).toEqual({ state: 'open', limit: 500 });
    expect(ids(w)).toEqual([12, 11, 13]);
    expect(w.get('[data-test=workflows-chip-open]').text()).toContain('3');
    expect(w.get('[data-test=workflows-chip-needs-you]').text()).toContain('1');
    expect(w.get('[data-test=workflows-chip-all]').text()).toContain('5');
    expect(w.get('[data-test=workflows-summary]').text()).toContain('3 open · 1 needs you · 1 completed today');
    expect(w.findAll('[data-test=workflows-status]').map((s) => s.text())).toEqual(['Needs you', 'Running', 'Idle']);
    w.unmount();
  });

  it('a chip asks the core with that state and shows its rows; the choice is in the hash and remembered', async () => {
    const f = fake();
    const w = await page(f);
    await w.get('[data-test=workflows-chip-all]').trigger('click');
    await flushPromises();
    expect(lastList(f).state).toBe('all');
    expect(ids(w)).toEqual([12, 11, 13, 14, 10]);
    expect(location.hash).toBe('#workflows?s=all');
    expect(JSON.parse(localStorage.getItem(StorageKey)!).chip).toBe('all');
    w.unmount();
    expect(location.hash).toBe(''); // the hash goes with the page
  });

  it('the team filter asks for that team only', async () => {
    const f = fake();
    const w = await page(f);
    await w.get('[data-test=workflows-team-filter]').setValue('2');
    await flushPromises();
    expect(lastList(f).teamId).toBe(2);
    expect(ids(w)).toEqual([13]);
    expect(location.hash).toBe('#workflows?t=2');
    w.unmount();
  });

  it('typing filters at once with the matches highlighted, then asks the core with q', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const f = fake();
    const w = mount(WorkflowsView, { props: { teams: [], api: f.api, pollMs: 600_000 }, attachTo: document.body });
    await flushPromisesFake();
    await w.get('[data-test=workflows-search]').setValue('refund');
    expect(ids(w)).toEqual([12, 11]);
    expect(w.findAll('[data-test=workflows-hit]').map((m) => m.text())).toEqual(['Refund', 'refund']);
    expect(f.calls.filter((c) => c.method === 'workflows.list' && c.params.q)).toEqual([]);
    vi.advanceTimersByTime(250);
    await flushPromisesFake();
    expect(lastList(f).q).toBe('refund');
    w.unmount();
  });

  it('opens with the filters a #workflows hash names', async () => {
    history.replaceState(null, '', '/#workflows?t=1&s=all&sel=11');
    const f = fake();
    const w = await page(f);
    expect(lastList(f)).toEqual({ teamId: 1, state: 'all', limit: 500 });
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('11');
    w.unmount();
  });

  it('a request opens it at a workflow: its team, All and that workflow selected', async () => {
    const f = fake();
    const w = await page(f, { request: { teamId: 1, workflow: 14, seq: 1 } });
    expect(lastList(f)).toEqual({ teamId: 1, state: 'all', limit: 500 });
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('14');
    w.unmount();
  });

  it('a header click sorts by that column, again the other way', async () => {
    const w = await page();
    await w.get('[data-test=workflows-sort-title]').trigger('click');
    expect(ids(w)).toEqual([13, 11, 12]);
    await w.get('[data-test=workflows-sort-title]').trigger('click');
    expect(ids(w)).toEqual([12, 11, 13]);
    expect(w.get('[data-test=workflows-sort-title]').attributes('aria-sort')).toBe('descending');
    w.unmount();
  });

  it('the elapsed time of an open workflow ticks on the shared 1 s timer; an ended one stands still', async () => {
    vi.restoreAllMocks();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'], now: NOW });
    const f = fake();
    f.list = rows;
    const w = mount(WorkflowsView, { props: { teams: [], api: f.api, pollMs: 600_000 }, attachTo: document.body });
    await flushPromisesFake();
    await w.get('[data-test=workflows-chip-all]').trigger('click');
    await flushPromisesFake();
    const value = (id: number) => w.get(`[data-wf="${id}"] [data-test=overall-time-value]`).text();
    expect(value(11)).toBe('1h 30m 00s');
    expect(value(10)).toBe('1h 00m 00s');
    vi.advanceTimersByTime(3000);
    await flushPromisesFake();
    expect(value(11)).toBe('1h 30m 03s');
    expect(value(10)).toBe('1h 00m 00s');
    w.unmount();
  });

  it("the time bar's segments match the breakdown, each named on hover", async () => {
    const w = await page();
    const segs = w.findAll('[data-wf="11"] [data-test=workflow-time-seg]');
    expect(segs.map((s) => s.attributes('data-category'))).toEqual(['agent-working', 'stalled', 'idle']);
    expect(segs[1].attributes('title')).toBe('Stalled: 10m (11%)');
    expect(w.get('[data-wf="11"] [data-test=workflow-time-bar]').attributes('title')).toContain('Agent working 1h (67%)');
    w.unmount();
  });

  it('the elapsed link and the timeline icon both open the Timeline dialog for the workflow', async () => {
    const w = await page();
    await w.get('[data-wf="11"] [data-test=overall-time]').trigger('click');
    expect(timelineTarget.value).toMatchObject({ kind: 'workflow', id: 11, team: 'payments' });
    closeTimeline();
    await w.get('[data-wf="13"] [data-test=workflows-timeline]').trigger('click');
    expect(timelineTarget.value).toMatchObject({ kind: 'workflow', id: 13, team: 'billing' });
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false); // the row was not opened by it
    w.unmount();
  });

  it('the item link opens the Backlog at the item (an archived one in the archive); "—" without one', async () => {
    const w = await page();
    await w.get('[data-wf="11"] [data-test=workflow-item-link]').trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: 'B096', archived: false });
    await w.get('[data-wf="13"] [data-test=workflow-item-link]').trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: 'B050', archived: true });
    expect(w.find('[data-wf="12"] [data-test=workflow-item-none]').text()).toBe('—');
    w.unmount();
  });

  it('when the session may not read the backlog the label is plain text and the pane has no spec', async () => {
    const f = fake();
    f.backlog = false;
    const w = await page(f);
    expect(w.find('[data-wf="11"] [data-test=workflow-item-link]').exists()).toBe(false);
    expect(w.get('[data-wf="11"] [data-test=workflow-item-text]').text()).toBe('B096');
    await w.get('[data-wf="11"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=workflow-pane-spec]').exists()).toBe(false);
    expect(w.find('[data-test=workflow-pane-cards]').exists()).toBe(true);
    w.unmount();
  });

  it('a row opens the pane: header, spec (collapsed, Show all), cards by lane, activity newest first, time', async () => {
    const f = fake();
    const w = await page(f);
    await w.get('[data-wf="11"]').trigger('click');
    await flushPromises();
    expect(f.calls.find((c) => c.method === 'workflows.get')!.params).toEqual({ workflow: 11 });
    const pane = w.get('[data-test=workflow-pane]');
    expect(pane.get('[data-test=workflow-pane-title]').text()).toBe('Partial refunds');
    expect(pane.get('[data-test=workflow-pane-status]').text()).toBe('Running');
    expect(pane.get('[data-test=workflow-pane-spec] .md').html()).toContain('<strong>Why:</strong>');
    expect(pane.get('[data-test=workflow-pane-spec] .md').classes()).toContain('collapsed');
    await pane.get('[data-test=workflow-pane-spec-toggle]').trigger('click');
    expect(pane.get('[data-test=workflow-pane-spec] .md').classes()).not.toContain('collapsed');
    // cards by lane: To Do before In Progress
    expect(pane.findAll('[data-test=workflow-pane-card]').map((c) => c.attributes('data-id'))).toEqual(['477', '476']);
    expect(pane.findAll('[data-test=workflow-pane-message]').map((m) => m.attributes('data-kind'))).toEqual(['progress', 'instruction']);
    expect(pane.findAll('[data-test=workflow-pane-message]')[1].text()).toContain('You');
    expect(pane.findAll('[data-test=workflow-pane-breakdown-row]')).toHaveLength(3);
    await pane.get('[data-test=workflow-pane-timeline]').trigger('click');
    expect(timelineTarget.value).toMatchObject({ kind: 'workflow', id: 11 });
    // a card opens in the board's card panel
    await pane.get('[data-test=workflow-pane-card][data-id="476"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=card-panel]').exists()).toBe(true);
    w.unmount();
  });

  it('actions ask first in the app\'s confirm dialog: Cancel calls nothing, OK calls with the row\'s team', async () => {
    const f = fake();
    const w = await page(f);
    await w.get('[data-wf="13"]').trigger('click');
    await flushPromises();
    // idle, nobody on it, will expire: Nudge, Extend and Close; nothing runs, so no Stop
    expect(w.find('[data-test=workflow-pane-nudge]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-pane-extend]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-pane-stop]').exists()).toBe(false);
    await w.get('[data-test=workflow-pane-nudge]').trigger('click');
    expect(confirmState.value?.message).toContain('Nudge #13');
    confirmState.value!.resolve(false);
    await flushPromises();
    expect(f.calls.map((c) => c.method)).not.toContain('bus.nudgeWorkflow');
    await w.get('[data-test=workflow-pane-close-wf]').trigger('click');
    expect(confirmState.value).toMatchObject({ okLabel: 'Close it', danger: true });
    confirmState.value!.resolve(true);
    await flushPromises();
    expect(f.calls.find((c) => c.method === 'bus.closeWorkflow')!.params).toEqual({ teamId: 2, workflow: 13 });
    w.unmount();
  });

  it('a running workflow offers Stop (not Nudge)', async () => {
    const w = await page();
    await w.get('[data-wf="11"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=workflow-pane-stop]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-pane-nudge]').exists()).toBe(false);
    w.unmount();
  });

  it('a Visitor sees no actions at all', async () => {
    applyRemoteAccess('read');
    const w = await page();
    await w.get('[data-wf="13"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-pane-actions]').exists()).toBe(false);
    w.unmount();
  });

  it('a working workflow\'s icon is animated, and not under prefers-reduced-motion', async () => {
    let w = await page();
    expect(w.get('[data-wf="11"] [data-test=workflow-status-icon]').attributes('data-animated')).toBe('true');
    expect(w.get('[data-wf="13"] [data-test=workflow-status-icon]').attributes('data-animated')).toBe('false');
    w.unmount();
    vi.stubGlobal('matchMedia', (q: string) => ({ matches: q.includes('reduced-motion'), media: q, addEventListener() {}, removeEventListener() {} }));
    try {
      w = await page();
      expect(w.get('[data-wf="11"] [data-test=workflow-status-icon]').attributes('data-animated')).toBe('false');
      w.unmount();
    } finally { vi.unstubAllGlobals(); }
  });

  it('the keyboard: down and up move the selection, Enter opens the pane, Esc closes it', async () => {
    const w = await page();
    const key = (k: string) => window.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true }));
    key('ArrowDown');
    await flushPromises();
    expect(w.get('[data-wf="12"]').classes()).toContain('cur');
    key('ArrowDown');
    await flushPromises();
    expect(w.get('[data-wf="11"]').classes()).toContain('cur');
    key('ArrowUp'); key('ArrowDown');
    await flushPromises();
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false);
    key('Enter');
    await flushPromises();
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('11');
    key('Escape');
    await flushPromises();
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false);
    w.unmount();
  });

  it('nothing open: a friendly empty state with a button to the Backlog', async () => {
    const f = fake();
    f.list = [];
    const w = await page(f);
    expect(w.get('[data-test=workflows-empty]').text()).toContain('All quiet');
    await w.get('[data-test=workflows-empty-backlog]').trigger('click');
    expect(w.emitted('backlog')).toHaveLength(1);
    w.unmount();
  });

  it('shows no tokens or cost anywhere', async () => {
    const w = await page();
    await w.get('[data-wf="11"]').trigger('click');
    await flushPromises();
    expect(w.text()).not.toMatch(/token|cost|\$\d/i);
    w.unmount();
  });
});

/** flushPromises without setTimeout (faked in some tests). */
async function flushPromisesFake() { for (let i = 0; i < 10; i++) await Promise.resolve(); }
