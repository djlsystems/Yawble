// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import WorkflowsDialog from '../../src/renderer/src/components/WorkflowsDialog.vue';
import { findWorkflowTeam, parseRemoteHash } from '../../src/renderer/src/remoteHash';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import type { YawbleApi } from '../../src/shared/api';

describe('parseRemoteHash (the remote view opened from the org dashboard)', () => {
  it('#workflow={wf} asks for that workflow, its team still to be found', () => {
    expect(parseRemoteHash('#workflow=413')).toEqual({ kind: 'workflow', workflow: 413, team: null });
  });

  it('#team={id} asks for that team', () => {
    expect(parseRemoteHash('#team=3')).toEqual({ kind: 'team', team: 3 });
  });

  it('both: the workflow wins and the team is its hint, in either order', () => {
    expect(parseRemoteHash('#workflow=413&team=3')).toEqual({ kind: 'workflow', workflow: 413, team: 3 });
    expect(parseRemoteHash('#team=3&workflow=413')).toEqual({ kind: 'workflow', workflow: 413, team: 3 });
  });

  it('both, with a bad workflow id: the team alone', () => {
    expect(parseRemoteHash('#workflow=abc&team=3')).toEqual({ kind: 'team', team: 3 });
  });

  it('neither: no hash, an empty one, or other keys', () => {
    for (const h of ['', '#', '#view=board', '#foo=1&bar=2']) expect(parseRemoteHash(h)).toBeNull();
  });

  it('garbage ids are ignored, not opened', () => {
    for (const h of ['#workflow=', '#workflow=0', '#workflow=-4', '#workflow=1.5', '#workflow=12abc', '#workflow=1e3',
      '#workflow=99999999999999999999', '#team=%31', '#=5', '#workflow', '#<script>', '#workflow=413=1'])
      expect(parseRemoteHash(h)).toBeNull();
  });

  it('#terminal/N is left to main.ts (an agent\'s terminal window), whatever follows', () => {
    expect(parseRemoteHash('#terminal/7')).toBeNull();
    expect(parseRemoteHash('#terminal/7&workflow=413')).toBeNull();
  });

  it('takes the first of a repeated key, case and spaces aside, and works without the #', () => {
    expect(parseRemoteHash('#team=3&team=9')).toEqual({ kind: 'team', team: 3 });
    expect(parseRemoteHash('#Workflow= 413 ')).toEqual({ kind: 'workflow', workflow: 413, team: null });
    expect(parseRemoteHash('team=3')).toEqual({ kind: 'team', team: 3 });
  });
});

describe('findWorkflowTeam', () => {
  const lists: Record<number, number[]> = { 1: [10, 11], 2: [20], 3: [30, 413] };
  const asked: number[] = [];
  const list = async (t: number) => { asked.push(t); if (t === 2) throw new Error('refused'); return lists[t] ?? []; };
  beforeEach(() => { asked.length = 0; });

  it('finds the team whose workflows hold the id, skipping a team whose list fails', async () => {
    expect(await findWorkflowTeam(413, [1, 2, 3], list)).toBe(3);
    expect(asked).toEqual([1, 2, 3]);
  });

  it('asks the hinted team first', async () => {
    expect(await findWorkflowTeam(413, [1, 2, 3], list, 3)).toBe(3);
    expect(asked).toEqual([3]);
  });

  it('a wrong or unknown hint still finds the right team', async () => {
    expect(await findWorkflowTeam(11, [1, 2, 3], list, 3)).toBe(1);
    expect(await findWorkflowTeam(11, [1, 2, 3], list, 99)).toBe(1);
  });

  it('null when no team has it', async () => {
    expect(await findWorkflowTeam(999, [1, 2, 3], list)).toBeNull();
    expect(await findWorkflowTeam(999, [], list)).toBeNull();
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
// Newest first, as core lists them. busy: 120 workflows (#1120 newest … #1001), so #1030 is the 91st newest.
// huge: 250 (#2250 … #2001), so #2030 is the 221st newest, past core's most (200).
const newestFirst = (top: number, n: number) => Array.from({ length: n }, (_, i) => top - i);
const workflowsOf: Record<number, number[]> = { 1: [10, 11], 2: [413, 20], 3: newestFirst(1120, 120), 4: newestFirst(2250, 250) };
const limits: Array<{ teamId: number; limit: number }> = [];

function remoteApi(): YawbleApi {
  return {
    remote: true,
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, p?: any) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams } as any;
        case 'bus.workflows': {
          // Core's limit: default 30, clamped to 1..200 (BusEngine.WorkflowsAsync).
          const limit = Math.min(Math.max(p.limit ?? 30, 1), 200);
          limits.push({ teamId: p.teamId, limit });
          return (workflowsOf[p.teamId] ?? []).slice(0, limit).map(wf) as any;
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

async function open(hash: string, readOnly = false) {
  window.location.hash = hash;
  applyRemoteAccess(readOnly ? 'read' : 'full');
  const w = mount(App, { props: { api: remoteApi(), pollMs: 60_000 }, attachTo: document.body });
  for (let i = 0; i < 8; i++) await flushPromises();
  return w;
}

describe('the remote view opens where the link says', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); document.body.innerHTML = ''; });
  afterEach(() => { window.location.hash = ''; applyRemoteAccess('full'); });

  it('#workflow=413 opens team web with workflow 413 selected', async () => {
    const w = await open('#workflow=413');
    expect(document.title).toContain('web');
    expect(w.get('[data-test=workflow-413]').classes()).toContain('sel');
    w.unmount();
  });

  it('a read-only Visitor can still open the thread', async () => {
    const w = await open('#workflow=413', true);
    expect(document.documentElement.dataset.remoteAccess).toBe('read');
    expect(w.get('[data-test=workflow-413]').classes()).toContain('sel');
    w.unmount();
  });

  it('#team=1 opens that team', async () => {
    const w = await open('#team=1');
    expect(document.title).toContain('payments');
    expect(w.find('[data-test=workflow-10]').exists()).toBe(false);
    w.unmount();
  });

  it('an unknown workflow, an unknown team, or garbage leaves the start page, with no error', async () => {
    for (const h of ['#workflow=999', '#team=42', '#workflow=x']) {
      const w = await open(h);
      expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
      expect(w.find('[data-test^=workflow-]').exists()).toBe(false);
      expect(w.find('.error').exists()).toBe(false);
      // A well-formed workflow id no team lists is named in a short note; a team or garbage is not.
      expect(w.find('[data-test=hash-note]').exists()).toBe(h === '#workflow=999');
      w.unmount();
    }
  });

  it('a workflow older than the newest 50 (the 91st newest of 120) opens selected', async () => {
    limits.length = 0;
    const w = await open('#workflow=1030');
    expect(document.title).toContain('busy');
    expect(w.get('[data-test=workflow-1030]').classes()).toContain('sel');
    expect(w.findAll('[data-test^=workflow-]').filter((e) => e.classes().includes('sel'))).toHaveLength(1);
    expect(w.find('[data-test=workflow-not-found]').exists()).toBe(false);
    // The dialog asked for 50, then for 200 to find it (other reads of the team page come and go around it).
    const reads = limits.filter((l) => l.teamId === 3).map((l) => l.limit);
    expect(reads.indexOf(50)).toBeGreaterThanOrEqual(0);
    expect(reads.slice(reads.indexOf(50) + 1)).toContain(200);
    w.unmount();
  });

  it('a workflow past the newest 200 says it was not found instead of nothing', async () => {
    const w = await open('#workflow=2030');
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
    expect(w.get('[data-test=hash-note]').text()).toContain('Workflow #2030 was not found');
    expect(w.find('.error').exists()).toBe(false);
    await w.get('[data-test=hash-note-dismiss]').trigger('click');
    expect(w.find('[data-test=hash-note]').exists()).toBe(false);
    w.unmount();
  });

  it('past 200 with its team as the hint: that team opens at its Workflows, with the note there', async () => {
    const w = await open('#workflow=2030&team=4');
    expect(document.title).toContain('huge');
    expect(w.get('[data-test=workflow-not-found]').text()).toBe("Workflow #2030 was not found among this team's newest 200 workflows.");
    expect(w.find('[data-test=workflow-2250]').exists()).toBe(true);
    expect(w.findAll('[data-test^=workflow-]').some((e) => e.classes().includes('sel'))).toBe(false);
    expect(w.find('[data-test=hash-note]').exists()).toBe(false);
    w.unmount();
  });

  it('a later change of the hash opens the new place', async () => {
    const w = await open('');
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
    window.location.hash = '#workflow=11';
    window.dispatchEvent(new HashChangeEvent('hashchange'));
    for (let i = 0; i < 8; i++) await flushPromises();
    expect(w.get('[data-test=workflow-11]').classes()).toContain('sel');
    w.unmount();
  });
});

// ---- the Workflows dialog alone, as TesterKofi checked it on card 272: focus over a long list ----

describe('WorkflowsDialog opened at a workflow', () => {
  beforeEach(() => { document.body.innerHTML = ''; limits.length = 0; });

  async function dialog(team: number, focus: number) {
    const w = mount(WorkflowsDialog, { props: { api: remoteApi(), team: teams.find((t) => t.id === team)!, pollMs: 60_000, focus }, attachTo: document.body });
    for (let i = 0; i < 6; i++) await flushPromises();
    return w;
  }

  it('among the newest 50: one read of 50, selected', async () => {
    const w = await dialog(3, 1100);
    expect(document.querySelector('[data-wf="1100"]')?.classList.contains('sel')).toBe(true);
    expect(limits.map((l) => l.limit)).toEqual([50]);
    w.unmount();
  });

  it('the 91st newest of 120: read again at 200, then selected and scrolled to', async () => {
    const scrolled: string[] = [];
    const orig = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = function (this: Element) { scrolled.push(this.getAttribute('data-wf') ?? ''); };
    try {
      const w = await dialog(3, 1030);
      expect(document.querySelector('[data-wf="1030"]')?.classList.contains('sel')).toBe(true);
      expect(document.querySelectorAll('[data-wf]')).toHaveLength(120);
      expect(limits.map((l) => l.limit)).toEqual([50, 200]);
      expect(scrolled).toEqual(['1030']);
      expect(w.find('[data-test=workflow-not-found]').exists()).toBe(false);
      w.unmount();
    } finally { Element.prototype.scrollIntoView = orig; }
  });

  it('past the newest 200: the newest 200 and a not-found note, nothing selected', async () => {
    const w = await dialog(4, 2030);
    expect(document.querySelectorAll('[data-wf]')).toHaveLength(200);
    expect(document.querySelector('[data-wf].sel')).toBeNull();
    expect(w.get('[data-test=workflow-not-found]').text()).toContain('#2030');
    w.unmount();
  });

  it('a short list without it: no second read, "not found in this team"', async () => {
    const w = await dialog(1, 999);
    expect(limits.map((l) => l.limit)).toEqual([50]);
    expect(w.get('[data-test=workflow-not-found]').text()).toBe('Workflow #999 was not found in this team.');
    w.unmount();
  });
});
