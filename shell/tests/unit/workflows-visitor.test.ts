// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';
import WorkflowsDialog from '../../src/renderer/src/components/WorkflowsDialog.vue';
import { applyRemoteAccess, READ_ONLY_TITLE } from '../../src/renderer/src/remoteAccess';

// In a read-only (Visitor) view the workflows dialog's change buttons - Nudge, Stop runs, Close and
// the confirm (Nudge / Close it) - are disabled with the read-only title (v-change); with full access they work as
// before, and their own busy-disabled state still applies. Fakes only.

const team = { id: 1, name: 'payments' } as TeamSummary;
const wf = { id: 10, title: 'Partial refunds', owner: 'Manager', state: 'open', undeclared: false, busy: ['DeveloperAda is running'], ending: null, startedAt: '2026-11-16T10:00:00Z', lastAt: '2026-11-16T11:00:00Z', messages: 3 };

function fakeApi(calls: string[], hold?: Promise<void>) {
  return {
    call: async (method: string) => {
      calls.push(method);
      if (method === 'bus.workflows') return [wf];
      if (hold) await hold;
      return {};
    },
  } as unknown as YawbleApi;
}

async function open(calls: string[] = [], hold?: Promise<void>) {
  const w = mount(WorkflowsDialog, { props: { team, api: fakeApi(calls, hold), pollMs: 60_000 }, attachTo: document.body });
  await flushPromises();
  return w;
}
const btn = (w: Awaited<ReturnType<typeof open>>, t: string) => w.get(`[data-test=${t}]`).element as HTMLButtonElement;

afterEach(() => { applyRemoteAccess('full'); document.body.innerHTML = ''; });

describe('workflows dialog for a Visitor', () => {
  it('Nudge, Stop runs and Close are disabled with the read-only title; a click calls nothing', async () => {
    applyRemoteAccess('read');
    const calls: string[] = [];
    const w = await open(calls);
    for (const t of ['workflow-nudge', 'workflow-stop', 'workflow-close']) {
      expect(btn(w, t).disabled, t).toBe(true);
      expect(btn(w, t).title, t).toBe(READ_ONLY_TITLE);
    }
    await w.get('[data-test=workflow-stop]').trigger('click');
    await w.get('[data-test=workflow-nudge]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=workflow-confirm]').exists()).toBe(false);
    expect(calls.filter((c) => c !== 'bus.workflows' && c !== 'workflows.list')).toEqual([]);
    w.unmount();
  });

  it('the confirm button is disabled when access turns read while it is open, and enabled again on full', async () => {
    const calls: string[] = [];
    const w = await open(calls);
    await w.get('[data-test=workflow-close]').trigger('click');
    expect(btn(w, 'workflow-confirm').disabled).toBe(false);
    applyRemoteAccess('read');
    await flushPromises();
    expect(btn(w, 'workflow-confirm').disabled).toBe(true);
    expect(btn(w, 'workflow-confirm').title).toBe(READ_ONLY_TITLE);
    await w.get('[data-test=workflow-confirm]').trigger('click');
    await flushPromises();
    expect(calls).not.toContain('bus.closeWorkflow');
    applyRemoteAccess('full');
    await flushPromises();
    expect(btn(w, 'workflow-confirm').disabled).toBe(false);
    w.unmount();
  });
});

describe('workflows dialog with full access (unchanged)', () => {
  it('Nudge, Stop runs and Close are enabled; Nudge then confirm calls bus.nudgeWorkflow', async () => {
    const calls: string[] = [];
    const w = await open(calls);
    for (const t of ['workflow-nudge', 'workflow-stop', 'workflow-close']) expect(btn(w, t).disabled, t).toBe(false);
    await w.get('[data-test=workflow-nudge]').trigger('click');
    expect(btn(w, 'workflow-confirm').disabled).toBe(false);
    await w.get('[data-test=workflow-confirm]').trigger('click');
    await flushPromises();
    expect(calls).toContain('bus.nudgeWorkflow');
    w.unmount();
  });

  it('busy still disables them: while Stop runs is in flight, Nudge, Stop and Close are disabled', async () => {
    let release!: () => void;
    const hold = new Promise<void>((ok) => { release = ok; });
    const calls: string[] = [];
    const w = await open(calls, hold);
    await w.get('[data-test=workflow-stop]').trigger('click');
    await flushPromises();
    for (const t of ['workflow-nudge', 'workflow-stop', 'workflow-close']) expect(btn(w, t).disabled, t).toBe(true);
    release();
    await flushPromises();
    for (const t of ['workflow-nudge', 'workflow-stop', 'workflow-close']) expect(btn(w, t).disabled, t).toBe(false);
    w.unmount();
  });
});
