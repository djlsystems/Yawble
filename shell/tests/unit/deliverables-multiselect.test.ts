// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { DeliverableCounts, DeliverableRow, DeliverablesListAnswer, Goal, ItemTime, TeamSummary, YawbleApi } from '../../src/shared/api';
import DeliverablesView from '../../src/renderer/src/deliverables/DeliverablesView.vue';
import { StorageKey } from '../../src/renderer/src/deliverables/page';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';

// The Deliverables page's checkboxes against a FAKE core: ticking rows (one, a Shift range, all shown), the bar that
// counts them, and the Assign goal dialog, which sends goals.assign once per ticked deliverable. Fakes only.

const NOW = Date.parse('2026-11-19T10:00:00Z');
const iso = (minAgo: number) => new Date(NOW - minAgo * 60_000).toISOString();
const time: ItemTime = { createdAt: iso(60), startedAt: null, endedAt: null, endedHow: null, open: true, overallMs: 3_600_000, rebuilt: false,
  breakdown: [{ category: 'agent-working', ms: 3_600_000 }] as ItemTime['breakdown'] };
function row(id: number, title: string, team = 'payments', teamId = 1): DeliverableRow {
  return {
    id, status: 'idle', title, owner: 'Manager', ownerId: 1, state: 'open', undeclared: false, busy: [], ending: null, startedAt: iso(120), lastAt: iso(id),
    messages: 3, endedAt: null, expiredAt: null, expiresAt: null, question: null, time, team, teamId, waitingFor: null, working: false, item: null, members: [], goal: null,
  } as DeliverableRow;
}
const counts: DeliverableCounts = { all: 4, open: 4, needsYou: 0, done: 0, closed: 0, running: 0, waiting: 0, idle: 4, stopped: 0, completed: 0, expired: 0, completedToday: 0 };
const goals = [{ id: 1, name: 'Launch' }, { id: 2, name: 'Payments v2' }] as Goal[];

interface Fake { api: YawbleApi; calls: { method: string; params: any }[]; list: DeliverableRow[]; refuse: Set<number> }
function fake(): Fake {
  const f: Fake = { calls: [], refuse: new Set(), api: null as unknown as YawbleApi,
    list: [row(21, 'Refund model'), row(22, 'Partial refunds'), row(23, 'Refund limits'), row(24, 'Old export', 'billing', 2)] };
  f.api = {
    call: async (method: string, params: any) => {
      f.calls.push({ method, params });
      if (method === 'deliverables.list') return { deliverables: f.list, counts, truncated: false, backlog: true, now: new Date(NOW).toISOString() } satisfies DeliverablesListAnswer;
      if (method === 'goals.list') return { goals, unassigned: {}, now: new Date(NOW).toISOString() };
      if (method === 'goals.assign') {
        if (f.refuse.has(params.deliverable)) throw Object.assign(new Error('that deliverable is gone'), { code: 'no_such_deliverable' });
        const goal = params.goal === null ? null : { id: params.goal, name: goals.find((g) => g.id === params.goal)!.name };
        f.list = f.list.map((w) => (w.id === params.deliverable ? { ...w, goal } : w));
        return { deliverable: params.deliverable, goal };
      }
      return {};
    },
  } as unknown as YawbleApi;
  return f;
}
async function page(f: Fake) {
  const w = mount(DeliverablesView, { props: { teams: [{ id: 1, name: 'payments' }, { id: 2, name: 'billing' }] as TeamSummary[], api: f.api, pollMs: 600_000 }, attachTo: document.body });
  await flushPromises();
  return w;
}
const assigns = (f: Fake) => f.calls.filter((c) => c.method === 'goals.assign').map((c) => c.params);

enableAutoUnmount(afterEach);
beforeEach(() => { applyRemoteAccess('full'); try { localStorage.removeItem(StorageKey); history.replaceState(null, '', location.pathname); } catch { /* none */ } });
afterEach(() => { applyRemoteAccess('full'); });

describe('ticking deliverables', () => {
  it('no bar until a row is ticked; a tick counts the row and does not open its pane', async () => {
    const w = await page(fake());
    expect(w.find('[data-test=deliverables-actions]').exists()).toBe(false);
    const boxes = w.findAll('[data-test=deliverables-pick]');
    expect(boxes).toHaveLength(4);
    await boxes[0].trigger('click');
    await boxes[2].trigger('click');
    expect(w.get('[data-test=deliverables-picked-count]').text()).toBe('2 selected');
    expect(w.find('[data-test=deliverable-pane]').exists()).toBe(false);
    expect(w.findAll('tr.picked')).toHaveLength(2);
    await boxes[0].trigger('click');
    expect(w.get('[data-test=deliverables-picked-count]').text()).toBe('1 selected');
    await w.get('[data-test=deliverables-picked-clear]').trigger('click');
    expect(w.find('[data-test=deliverables-actions]').exists()).toBe(false);
  });

  it('Shift with a click ticks every row between the two; the header box ticks all shown, then none', async () => {
    const w = await page(fake());
    const boxes = w.findAll('[data-test=deliverables-pick]');
    await boxes[0].trigger('click');
    await boxes[2].trigger('click', { shiftKey: true });
    expect(w.get('[data-test=deliverables-picked-count]').text()).toBe('3 selected');
    await w.get('[data-test=deliverables-pick-all]').trigger('change');
    expect(w.get('[data-test=deliverables-picked-count]').text()).toBe('4 selected');
    await w.get('[data-test=deliverables-pick-all]').trigger('change');
    expect(w.find('[data-test=deliverables-actions]').exists()).toBe(false);
  });

  it('a filter that hides a ticked row unticks it', async () => {
    const w = await page(fake());
    await w.get('[data-test=deliverables-pick-all]').trigger('change');
    expect(w.get('[data-test=deliverables-picked-count]').text()).toBe('4 selected');
    await w.get('[data-test=deliverables-team-filter]').setValue('2');
    await flushPromises();
    expect(w.get('[data-test=deliverables-picked-count]').text()).toBe('1 selected');
  });

  it('a Visitor has no checkboxes', async () => {
    applyRemoteAccess('read');
    const w = await page(fake());
    expect(w.findAll('[data-test=deliverables-row]')).toHaveLength(4);
    expect(w.find('[data-test=deliverables-pick]').exists()).toBe(false);
    expect(w.find('[data-test=deliverables-pick-all]').exists()).toBe(false);
  });
});

describe('Assign goal for the ticked deliverables', () => {
  it('says how many are selected, offers every goal, and assigns the chosen one to each', async () => {
    const f = fake();
    const w = await page(f);
    const boxes = w.findAll('[data-test=deliverables-pick]');
    await boxes[0].trigger('click');
    await boxes[1].trigger('click');
    await boxes[3].trigger('click');
    await w.get('[data-test=deliverables-assign-goal]').trigger('click');
    const dialog = w.getComponent({ name: 'AssignGoalDialog' });
    expect(dialog.get('[data-test=assign-goal-count]').text()).toBe('3 deliverables selected.');
    expect(dialog.findAll('[data-test=assign-goal-select] option').map((o) => o.text())).toEqual(['Choose a goal…', 'Launch', 'Payments v2', 'Unassigned (no goal)']);
    // Nothing is sent before a goal is chosen.
    expect(dialog.get('[data-test=assign-goal-save]').attributes('disabled')).toBeDefined();
    await dialog.get('[data-test=assign-goal-select]').setValue('2');
    expect(dialog.get('[data-test=assign-goal-save]').text()).toBe('Assign to 3 deliverables');
    await dialog.get('form').trigger('submit');
    await flushPromises();
    const ids = w.findAll('[data-test=deliverables-row]').map((r) => Number(r.attributes('data-wf')));
    expect(assigns(f)).toEqual([ids[0], ids[1], ids[3]].map((deliverable) => ({ deliverable, goal: 2 })));
    // Done: the dialog closes, nothing stays ticked, and the rows show the goal.
    expect(w.findComponent({ name: 'AssignGoalDialog' }).exists()).toBe(false);
    expect(w.find('[data-test=deliverables-actions]').exists()).toBe(false);
    expect(f.list.filter((r) => r.goal?.id === 2)).toHaveLength(3);
  });

  it('Unassigned sends a null goal; Cancel sends nothing and keeps the ticks', async () => {
    const f = fake();
    const w = await page(f);
    await w.findAll('[data-test=deliverables-pick]')[0].trigger('click');
    await w.get('[data-test=deliverables-assign-goal]').trigger('click');
    await w.get('[data-test=assign-goal-cancel]').trigger('click');
    expect(assigns(f)).toEqual([]);
    expect(w.get('[data-test=deliverables-picked-count]').text()).toBe('1 selected');
    await w.get('[data-test=deliverables-assign-goal]').trigger('click');
    expect(w.get('[data-test=assign-goal-count]').text()).toBe('1 deliverable selected.');
    await w.get('[data-test=assign-goal-select]').setValue('none');
    await w.get('[data-test=assign-goal-dialog]').trigger('submit');
    await flushPromises();
    expect(assigns(f)).toHaveLength(1);
    expect(assigns(f)[0].goal).toBeNull();
  });

  it('a deliverable the core refuses is named with the reason; the others are changed and Try again sends only the refused', async () => {
    const f = fake();
    const w = await page(f);
    await w.get('[data-test=deliverables-pick-all]').trigger('change');
    const refused = Number(w.findAll('[data-test=deliverables-row]')[1].attributes('data-wf'));
    f.refuse.add(refused);
    await w.get('[data-test=deliverables-assign-goal]').trigger('click');
    await w.get('[data-test=assign-goal-select]').setValue('1');
    await w.get('[data-test=assign-goal-dialog]').trigger('submit');
    await flushPromises();
    expect(assigns(f)).toHaveLength(4);
    const failed = w.get('[data-test=assign-goal-failed]').text();
    expect(failed).toContain('1 deliverable could not be changed');
    expect(failed).toContain(`#${refused}`);
    expect(failed).toContain('that deliverable is gone');
    expect(w.get('[data-test=assign-goal-save]').text()).toBe('Try again');
    f.refuse.clear();
    await w.get('[data-test=assign-goal-dialog]').trigger('submit');
    await flushPromises();
    expect(assigns(f).slice(4)).toEqual([{ deliverable: refused, goal: 1 }]);
    expect(w.find('[data-test=assign-goal-dialog]').exists()).toBe(false);
  });
});
