// @vitest-environment happy-dom
// The Deliverables page's pane when access turns read-only (Visitor) while one of its confirms is open: the question
// closes as Cancel and nothing is sent, as the Deliverables dialog did. Nudge, Extend, Stop and Close alike; the same when
// the change arrives through the app's remote access (applyRemoteAccess), and an answer given after it is refused.
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import type { DeliverableDetail, DeliverableRow, TeamSummary, YawbleApi } from '../../src/shared/api';
import DeliverablePane from '../../src/renderer/src/deliverables/DeliverablePane.vue';
import DeliverablesView from '../../src/renderer/src/deliverables/DeliverablesView.vue';
import { confirmState } from '../../src/renderer/src/confirm';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';

const real = <T = any>(name: string): T => JSON.parse(readFileSync(path.join(__dirname, 'fixtures', 'deliverables', `${name}.json`), 'utf8'));
const RUNNING = real<DeliverableDetail>('get-running');
/** The running one (Stop, Close) and the same one quiet with an expiry clock (Nudge, Extend, Close). */
const busyRow = (): DeliverableRow => structuredClone(RUNNING.deliverable);
const quietRow = (): DeliverableRow => ({ ...busyRow(), working: false, members: [], expiresAt: new Date(Date.now() + 20 * 3600_000).toISOString() });

const ACTIONS = new Set(['bus.nudgeDeliverable', 'bus.deliverableExtend', 'bus.stopDeliverable', 'bus.closeDeliverable']);
function fake(row: DeliverableRow) {
  const calls: string[] = [];
  const api = {
    call: async (method: string) => {
      calls.push(method);
      if (method === 'deliverables.get') return { ...structuredClone(RUNNING), deliverable: structuredClone(row) };
      if (method === 'deliverables.list') return { deliverables: [structuredClone(row)], counts: { all: 1, open: 1 }, truncated: false, backlog: true, now: new Date().toISOString() };
      return {};
    },
  } as unknown as YawbleApi;
  return { api, acted: () => calls.filter((m) => ACTIONS.has(m)) };
}

enableAutoUnmount(afterEach);
afterEach(() => { confirmState.value?.resolve(false); applyRemoteAccess('full'); document.body.innerHTML = ''; });

describe('a pending confirm when access turns read-only', () => {
  for (const [action, row] of [['nudge', quietRow], ['extend', quietRow], ['stop', busyRow], ['close-wf', busyRow]] as const) {
    it(`${action}: the question closes as Cancel, the actions go and nothing is sent`, async () => {
      const r = row();
      const f = fake(r);
      const w = mount(DeliverablePane, { props: { row: r, api: f.api, pollMs: 600_000 }, attachTo: document.body });
      await flushPromises();
      await w.find(`[data-test=deliverable-pane-${action}]`).trigger('click');
      expect(confirmState.value).not.toBeNull();
      await w.setProps({ readOnly: true });
      await flushPromises();
      expect(confirmState.value).toBeNull();
      expect(w.find('[data-test=deliverable-pane-actions]').exists()).toBe(false);
      expect(f.acted()).toEqual([]);
    });
  }

  it('an OK given after access turned read-only is refused (the question was not the pane\'s to close)', async () => {
    const r = busyRow();
    const f = fake(r);
    const w = mount(DeliverablePane, { props: { row: r, api: f.api, pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=deliverable-pane-close-wf]').trigger('click');
    const q = confirmState.value!;
    // The OK arrives in the same tick as the change, before the pane could close its question: act checks again.
    const ro = w.setProps({ readOnly: true });
    q.resolve(true);
    await ro;
    await flushPromises();
    expect(f.acted()).toEqual([]);
  });

  it('through the page: applyRemoteAccess(\'read\') closes the open Stop question and nothing is sent', async () => {
    const r = busyRow();
    const f = fake(r);
    const w = mount(DeliverablesView, { props: { teams: [{ id: r.teamId, name: r.team }] as TeamSummary[], api: f.api, pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    await w.find(`[data-test=deliverables-row][data-wf="${r.id}"]`).trigger('click');
    await flushPromises();
    await w.find('[data-test=deliverable-pane-stop]').trigger('click');
    expect(confirmState.value?.okLabel).toBe('Stop');
    applyRemoteAccess('read');
    await flushPromises();
    expect(confirmState.value).toBeNull();
    expect(w.find('[data-test=deliverable-pane-actions]').exists()).toBe(false);
    expect(f.acted()).toEqual([]);
  });

  it('full access: OK still acts (Close sends bus.closeDeliverable once)', async () => {
    const r = busyRow();
    const f = fake(r);
    const w = mount(DeliverablePane, { props: { row: r, api: f.api, pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=deliverable-pane-close-wf]').trigger('click');
    confirmState.value!.resolve(true);
    await flushPromises();
    expect(f.acted()).toEqual(['bus.closeDeliverable']);
  });
});
