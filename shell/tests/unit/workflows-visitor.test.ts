// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import WorkflowsView from '../../src/renderer/src/workflows/WorkflowsView.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { mountPane, paneCore, paneRow } from './workflow-pane-fake';
import { stopConfirmStubs, stubConfirm } from './confirm-stub';

// In a read-only (Visitor) view the Workflows page's pane has no change buttons - no Nudge, Extend, Stop or Close -
// and calls nothing; with full access each is asked first in the app's confirm, then sent, and while one is in flight
// they are all held. Fakes only.

const running = paneRow({ id: 10, title: 'Partial refunds', status: 'running', busy: ['DeveloperAda is running'], members: [{ id: 2, name: 'DeveloperAda', state: 'running' }] });
const idle = paneRow({ id: 11, title: 'Refund limits', status: 'idle', expiresAt: new Date(Date.now() + 5 * 3_600_000).toISOString() });
const ActionIds = ['workflow-pane-nudge', 'workflow-pane-extend', 'workflow-pane-stop', 'workflow-pane-close-wf'];
const shownActions = (w: { findAll: (s: string) => { attributes: (n: string) => string | undefined }[] }) =>
  w.findAll('[data-test=workflow-pane-actions] button').map((b) => b.attributes('data-test'));
const disabled = (w: { get: (s: string) => { element: Element } }, t: string) => (w.get(`[data-test=${t}]`).element as HTMLButtonElement).disabled;

afterEach(() => { applyRemoteAccess('full'); stopConfirmStubs(); localStorage.clear(); history.replaceState(null, '', '/'); document.body.innerHTML = ''; });

describe('the Workflows page for a Visitor', () => {
  it('the pane opened read-only shows no Nudge, Extend, Stop or Close, and calls nothing', async () => {
    const core = paneCore(() => [running, idle]);
    for (const row of [running, idle]) {
      const w = await mountPane(row, core.api, { readOnly: true });
      expect(w.find('[data-test=workflow-pane-actions]').exists(), `#${row.id}`).toBe(false);
      for (const t of ActionIds) expect(w.find(`[data-test=${t}]`).exists(), t).toBe(false);
      w.unmount();
    }
    expect(core.methods()).toEqual([]);
  });

  it('the page in a read-only view opens the pane read-only: no actions, nothing called', async () => {
    applyRemoteAccess('read');
    const core = paneCore(() => [running, idle]);
    const w = mount(WorkflowsView, { props: { teams: [{ id: 1, name: 'payments' }] as never, api: core.api, pollMs: 600_000, request: { teamId: 1, workflow: 11, seq: 1 } }, attachTo: document.body });
    await vi.waitFor(() => expect(w.find('[data-test=workflow-pane]').exists()).toBe(true));
    await flushPromises();
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('11');
    expect(w.find('[data-test=workflow-pane-actions]').exists()).toBe(false);
    expect(core.methods()).toEqual([]);
    w.unmount();
  });

  // The dialog's confirm was disabled when access turned read while it was open; the pane asks in the app's confirm
  // dialog, which does not look at access, and the pane checks readOnly only before asking.
  it.todo('the confirm asked before access turned read does not act once it is read');
});

describe('the Workflows page with full access', () => {
  it('an idle one offers Nudge, Extend and Close (no Stop); Nudge asked and confirmed calls bus.nudgeWorkflow', async () => {
    const asked = stubConfirm(() => true);
    const core = paneCore(() => [idle]);
    const w = await mountPane(idle, core.api);
    expect(shownActions(w)).toEqual(['workflow-pane-nudge', 'workflow-pane-extend', 'workflow-pane-close-wf']);
    for (const t of shownActions(w)) expect(disabled(w, t!), t).toBe(false);
    await w.get('[data-test=workflow-pane-nudge]').trigger('click');
    await flushPromises();
    expect(asked).toHaveBeenCalledWith('Nudge #11? Its Manager is told to pick it up.');
    expect(core.calls.find((c) => c.method === 'bus.nudgeWorkflow')!.params).toEqual({ teamId: 1, workflow: 11 });
    w.unmount();
  });

  it('a running one offers Stop and Close (no Nudge); Stop and Close each asked, then sent; a declined one sends nothing', async () => {
    const asked = stubConfirm((m) => !m.startsWith('Close'));
    const core = paneCore(() => [running]);
    const w = await mountPane(running, core.api);
    expect(shownActions(w)).toEqual(['workflow-pane-stop', 'workflow-pane-close-wf']);
    await w.get('[data-test=workflow-pane-stop]').trigger('click');
    await flushPromises();
    await w.get('[data-test=workflow-pane-close-wf]').trigger('click');
    await flushPromises();
    expect(asked.mock.calls.map((c) => c[0])).toEqual(['Stop what runs in #10 now? The workflow stays open.', 'Close #10 without declaring it complete?']);
    expect(core.methods()).toEqual(['bus.stopWorkflow']);
    w.unmount();
  });

  it('busy still holds them: while Stop is in flight, Stop and Close are disabled, and free again after', async () => {
    stubConfirm(() => true);
    let release!: () => void;
    const hold = new Promise<void>((ok) => { release = ok; });
    const core = paneCore(() => [running], { hold });
    const w = await mountPane(running, core.api);
    await w.get('[data-test=workflow-pane-stop]').trigger('click');
    await flushPromises();
    for (const t of ['workflow-pane-stop', 'workflow-pane-close-wf']) expect(disabled(w, t), t).toBe(true);
    expect(w.get('[data-test=workflow-pane-stop]').text()).toContain('Stopping');
    release();
    await flushPromises();
    for (const t of ['workflow-pane-stop', 'workflow-pane-close-wf']) expect(disabled(w, t), t).toBe(false);
    w.unmount();
  });
});
