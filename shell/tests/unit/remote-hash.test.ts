// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
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
];
const workflowsOf: Record<number, number[]> = { 1: [10, 11], 2: [413, 20] };

function remoteApi(): YawbleApi {
  return {
    remote: true,
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, p?: any) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams } as any;
        case 'bus.workflows': return (workflowsOf[p.teamId] ?? []).map(wf) as any;
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
      w.unmount();
    }
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
