// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import { parseRemoteHash } from '../../src/renderer/src/remoteHash';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import type { YawbleApi } from '../../src/shared/api';

describe('parseRemoteHash (the remote view opened from the org dashboard)', () => {
  it('#deliverable={wf} asks for that deliverable, its team still to be found', () => {
    expect(parseRemoteHash('#deliverable=413')).toEqual({ kind: 'deliverable', deliverable: 413, team: null });
  });

  it('#team={id} asks for that team', () => {
    expect(parseRemoteHash('#team=3')).toEqual({ kind: 'team', team: 3 });
  });

  it('both: the deliverable wins and the team is its hint, in either order', () => {
    expect(parseRemoteHash('#deliverable=413&team=3')).toEqual({ kind: 'deliverable', deliverable: 413, team: 3 });
    expect(parseRemoteHash('#team=3&deliverable=413')).toEqual({ kind: 'deliverable', deliverable: 413, team: 3 });
  });

  it('both, with a bad deliverable id: the team alone', () => {
    expect(parseRemoteHash('#deliverable=abc&team=3')).toEqual({ kind: 'team', team: 3 });
  });

  it('neither: no hash, an empty one, or other keys', () => {
    for (const h of ['', '#', '#view=board', '#foo=1&bar=2']) expect(parseRemoteHash(h)).toBeNull();
  });

  it('garbage ids are ignored, not opened', () => {
    for (const h of ['#deliverable=', '#deliverable=0', '#deliverable=-4', '#deliverable=1.5', '#deliverable=12abc', '#deliverable=1e3',
      '#deliverable=99999999999999999999', '#team=%31', '#=5', '#deliverable', '#<script>', '#deliverable=413=1'])
      expect(parseRemoteHash(h)).toBeNull();
  });

  it('#terminal/N is left to main.ts (an agent\'s terminal window), whatever follows', () => {
    expect(parseRemoteHash('#terminal/7')).toBeNull();
    expect(parseRemoteHash('#terminal/7&deliverable=413')).toBeNull();
  });

  it('takes the first of a repeated key, case and spaces aside, and works without the #', () => {
    expect(parseRemoteHash('#team=3&team=9')).toEqual({ kind: 'team', team: 3 });
    expect(parseRemoteHash('#Deliverable= 413 ')).toEqual({ kind: 'deliverable', deliverable: 413, team: null });
    expect(parseRemoteHash('team=3')).toEqual({ kind: 'team', team: 3 });
  });
});

// ---- the remote view itself: App mounted as remote.html would, with the hash set ----

const wf = (id: number) => ({ id, title: `Work ${id}`, owner: 'Manager', state: 'open', undeclared: false, busy: [], ending: null, startedAt: '', lastAt: '2026-11-16T10:00:00Z', messages: 2 });
const teams = [
  { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 },
  { id: 2, name: 'web', repoPath: null, members: 2, running: 0, queued: 0 },
  { id: 3, name: 'busy', repoPath: null, members: 2, running: 0, queued: 0 },
  { id: 4, name: 'huge', repoPath: null, members: 2, running: 0, queued: 0 },
];
// Newest first, as core lists them. busy: 120 deliverables (#1120 newest … #1001), so #1030 is the 91st newest.
// huge: 250 (#2250 … #2001), so #2030 is the 221st newest, past core's most (200).
const newestFirst = (top: number, n: number) => Array.from({ length: n }, (_, i) => top - i);
const deliverablesOf: Record<number, number[]> = { 1: [10, 11], 2: [413, 20], 3: newestFirst(1120, 120), 4: newestFirst(2250, 250) };
const calls: { method: string; p: any }[] = [];
const teamOf = (id: number) => Number(Object.keys(deliverablesOf).find((t) => deliverablesOf[Number(t)]!.includes(id)) ?? 0);
/** A Deliverables page row (deliverables.list / deliverables.get) for a deliverable of the fake. */
const pageRow = (id: number) => {
  const t = teams.find((x) => x.id === teamOf(id))!;
  return { ...wf(id), ownerId: 1, endedAt: null, expiredAt: null, expiresAt: null, expiryWarning: null, question: null, time: null, team: t.name, teamId: t.id,
    waitingFor: null, working: false, item: null, members: [], status: 'idle' };
};
/** The page's list: each team's newest 50 only, so an older deliverable is not among its rows. */
const listed = (teamId?: number) => Object.entries(deliverablesOf).filter(([t]) => teamId === undefined || Number(t) === teamId)
  .flatMap(([, ids]) => ids.slice(0, 50)).map(pageRow);

function remoteApi(): YawbleApi {
  return {
    remote: true,
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, p?: any) => {
      calls.push({ method, p });
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams } as any;
        case 'bus.deliverables': {
          // Core's limit: default 30, clamped to 1..200 (BusEngine.DeliverablesAsync).
          return (deliverablesOf[p.teamId] ?? []).slice(0, Math.min(Math.max(p.limit ?? 30, 1), 200)).map(wf) as any;
        }
        case 'deliverables.list': {
          const rows = listed(p?.teamId);
          return { deliverables: rows, counts: { all: rows.length, open: rows.length, needsYou: 0, done: 0, closed: 0, completedToday: 0 }, truncated: true, backlog: true, now: '' } as any;
        }
        case 'deliverables.get': {
          if (!teamOf(p.deliverable)) throw Object.assign(new Error(`there is no deliverable ${p.deliverable}`), { code: 'no_such_deliverable' });
          return { deliverable: pageRow(p.deliverable), cards: [], messages: [], spec: null, backlog: true } as any;
        }
        case 'monitor.messages': return { end: 0, messages: [] } as any;
        case 'monitor.steps': return { end: 0, pending: false, steps: [] } as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

/** Waits for the place the link opens; the Deliverables page is loaded when first opened (App.vue's async component). */
async function settle(w: VueWrapper, page: boolean) {
  for (let i = 0; i < 8; i++) await flushPromises();
  if (page) await vi.waitFor(() => expect(w.find('[data-test=deliverables-view]').exists()).toBe(true));
  for (let i = 0; i < 8; i++) await flushPromises();
}
async function open(hash: string, opts: { readOnly?: boolean; page?: boolean } = {}) {
  window.location.hash = hash;
  applyRemoteAccess(opts.readOnly ? 'read' : 'full');
  const w = mount(App, { props: { api: remoteApi(), pollMs: 60_000 }, attachTo: document.body });
  await settle(w, opts.page ?? true);
  return w;
}
const teamFilter = (w: VueWrapper) => (w.get('[data-test=deliverables-team-filter]').element as HTMLSelectElement).value;
const selectedRows = (w: VueWrapper) => w.findAll('[data-test=deliverables-row]').filter((e) => e.classes().includes('sel')).map((e) => Number(e.attributes('data-wf')));
const pane = (w: VueWrapper) => w.find('[data-test=deliverable-pane]');

describe('the remote view opens where the link says', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); document.body.innerHTML = ''; calls.length = 0; });
  afterEach(() => { window.location.hash = ''; applyRemoteAccess('full'); });

  it('#deliverable=413 opens the Deliverables page at it: its team (web) the filter, selected, its pane open, no dialog', async () => {
    const w = await open('#deliverable=413');
    expect(teamFilter(w)).toBe('2');
    expect(selectedRows(w)).toEqual([413]);
    expect(pane(w).attributes('data-id')).toBe('413');
    expect(document.querySelector('[aria-modal="true"]')).toBeNull();
    expect(w.find('[data-test=deliverables-not-found]').exists()).toBe(false);
    w.unmount();
  });

  it('a read-only Visitor can still open it', async () => {
    const w = await open('#deliverable=413', { readOnly: true });
    expect(document.documentElement.dataset.remoteAccess).toBe('read');
    expect(pane(w).attributes('data-id')).toBe('413');
    w.unmount();
  });

  it('#team=1 opens that team', async () => {
    const w = await open('#team=1', { page: false });
    expect(document.title).toContain('payments');
    expect(w.find('[data-test=deliverables-view]').exists()).toBe(false);
    w.unmount();
  });

  it('an unknown team or garbage leaves the start page, with no error', async () => {
    for (const h of ['#team=42', '#deliverable=x']) {
      const w = await open(h, { page: false });
      expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
      expect(w.find('[data-test=deliverables-view]').exists()).toBe(false);
      expect(w.find('.error').exists()).toBe(false);
      w.unmount();
    }
  });

  it('a deliverable older than the page\'s rows (the 91st newest of 120) is read by itself and opens with its pane', async () => {
    const w = await open('#deliverable=1030');
    expect(teamFilter(w)).toBe('3');
    expect(pane(w).attributes('data-id')).toBe('1030');
    expect(selectedRows(w)).toEqual([1030]);
    expect(calls.some((c) => c.method === 'deliverables.get' && c.p.deliverable === 1030)).toBe(true);
    expect(calls.some((c) => c.method === 'bus.deliverables')).toBe(false); // no hunt through each team's newest
    w.unmount();
  });

  it('one past every team\'s newest 200 opens too (the core knows it)', async () => {
    const w = await open('#deliverable=2030');
    expect(teamFilter(w)).toBe('4');
    expect(pane(w).attributes('data-id')).toBe('2030');
    w.unmount();
  });

  it('a deliverable the core does not know: the page says "Deliverable #N was not found", no pane; Dismiss hides it', async () => {
    const w = await open('#deliverable=999');
    expect(w.get('[data-test=deliverables-not-found]').text()).toContain('Deliverable #999 was not found');
    expect(pane(w).exists()).toBe(false);
    expect(selectedRows(w)).toEqual([]);
    expect(teamFilter(w)).toBe('');
    expect(w.find('.error').exists()).toBe(false);
    await w.get('[data-test=deliverables-not-found-dismiss]').trigger('click');
    expect(w.find('[data-test=deliverables-not-found]').exists()).toBe(false);
    w.unmount();
  });

  it('unknown, with a known team as the hint: that team is the filter, with the note', async () => {
    const w = await open('#deliverable=999&team=4');
    expect(teamFilter(w)).toBe('4');
    expect(w.get('[data-test=deliverables-not-found]').text()).toContain('Deliverable #999 was not found');
    expect(pane(w).exists()).toBe(false);
    w.unmount();
  });

  it('a later change of the hash opens the new place', async () => {
    const w = await open('', { page: false });
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
    window.location.hash = '#deliverable=11';
    window.dispatchEvent(new HashChangeEvent('hashchange'));
    await settle(w, true);
    expect(teamFilter(w)).toBe('1');
    expect(pane(w).attributes('data-id')).toBe('11');
    w.unmount();
  });
});

