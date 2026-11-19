// @vitest-environment happy-dom
// Card 478 (B104 verify, TesterKofi): the Workflows page fed the REAL core's answers.
// tests/unit/fixtures/card478/*.json were written by core Card478B104VerifyTests with CARD478_FIXTURE_OUT set: a started
// engine with a scripted fake runner (never a real agent CLI) where Web's Manager runs holding the heavy-work lease and
// has told Ben (whose run waits for the lease), Cy waits for the lease in his own workflow, Cloud's Dee asked a question
// with her run still on, Eve waits for a run slot; plus a dispatched item (completed, with its spec), an archived item
// from the root payload (closed) and an expired workflow. The fake API answers workflows.list / workflows.get with
// those answers (state open: the core's open answer; anything else: the core's all answer), nothing invented.
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkflowDetail, WorkflowsListAnswer, YawbleApi } from '../../src/shared/api';
import WorkflowsView from '../../src/renderer/src/workflows/WorkflowsView.vue';
import { backlogRequest, sortRows, statusTip, timeBar, workflowsRequest } from '../../src/renderer/src/workflows/page';
import { closeTimeline, timelineTarget } from '../../src/renderer/src/timeline/time';
import { confirmState } from '../../src/renderer/src/confirm';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';

const real = <T = any>(name: string): T => JSON.parse(readFileSync(path.join(__dirname, 'fixtures', 'card478', `${name}.json`), 'utf8'));
const ID = real<Record<string, number | string>>('ids');
const ALL = real<WorkflowsListAnswer>('list-all');
const OPEN = real<WorkflowsListAnswer>('list-open');
const GETS: WorkflowDetail[] = ['get-running', 'get-dispatched', 'get-needs-you', 'get-closed-archived'].map((n) => real<WorkflowDetail>(n));
const VISITOR_LIST = real<WorkflowsListAnswer>('visitor-list');
const VISITOR_GET = real<WorkflowDetail>('visitor-get');
const n = (k: string) => Number(ID[k]);

interface Fake { api: YawbleApi; calls: { method: string; params: any }[]; shape: (a: any) => any }
function fake(list = { all: ALL, open: OPEN }, gets = GETS): Fake {
  const f: Fake = { calls: [], shape: (a) => a, api: null as unknown as YawbleApi };
  f.api = {
    call: async (method: string, params: any) => {
      f.calls.push({ method, params });
      // A fresh copy per answer, as the wire gives (the page stamps each answer's time objects when they arrive).
      if (method === 'workflows.list') return f.shape(structuredClone(params?.state === 'open' ? list.open : list.all));
      if (method === 'workflows.get') {
        const g = gets.find((d) => d.workflow.id === params.workflow);
        if (!g) throw Object.assign(new Error('no such workflow'), { code: 'no_such_workflow' });
        return f.shape(structuredClone(g));
      }
      return {};
    },
  } as unknown as YawbleApi;
  return f;
}
async function page(f = fake(), props: Record<string, unknown> = {}) {
  lastFake = f;
  const w = mount(WorkflowsView, { props: { teams: [{ id: n('web'), name: 'Web' }, { id: n('cloud'), name: 'Cloud' }], api: f.api, pollMs: 600_000, ...props }, attachTo: document.body });
  await flushPromises();
  return w;
}
type W = Awaited<ReturnType<typeof page>>;
const ids = (w: W) => w.findAll('[data-test=workflows-row]').map((r) => Number(r.attributes('data-wf')));
let lastFake: Fake | null = null;
const f0 = (_w: W) => (lastFake?.calls ?? []).filter((c) => c.method === 'workflows.list').map((c) => c.params ?? {});
const rowOf = (w: W, id: number) => w.find(`[data-test=workflows-row][data-wf="${id}"]`);

enableAutoUnmount(afterEach);
beforeEach(() => { localStorage.clear(); history.replaceState(null, '', '/'); });
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

describe('the real core answers, as the page shows them', () => {
  it('Open (the default): needs you first, then running, then the waiting ones; chips and summary from the core counts', async () => {
    const w = await page();
    expect(ids(w)[0]).toBe(n('dee'));
    expect(ids(w)[1]).toBe(n('lead'));
    expect(ids(w).slice(2).sort()).toEqual([n('cy'), n('eve')].sort());
    expect(w.find('[data-test=workflows-chip-open] [data-test=workflows-chip-count]').text()).toBe('4');
    expect(w.find('[data-test=workflows-chip-needs-you] [data-test=workflows-chip-count]').text()).toBe('1');
    expect(w.find('[data-test=workflows-chip-closed] [data-test=workflows-chip-count]').text()).toBe('2');
    expect(w.find('[data-test=workflows-summary]').text()).toContain('4 open · 1 needs you · 1 completed today');
    // The page asks only the contract's parameters.
    for (const c of f0(w)) expect(Object.keys(c).every((k) => ['teamId', 'q', 'state', 'limit'].includes(k))).toBe(true);
  });

  it('the Status words and tooltips for the core\'s live states (lease, slot, needs-you with a run on it)', async () => {
    const w = await page();
    const word = (id: number) => rowOf(w, id).find('[data-test=workflows-status]').text();
    expect([word(n('lead')), word(n('cy')), word(n('dee')), word(n('eve'))]).toEqual(['Running', 'Waiting', 'Needs you', 'Waiting']);
    expect(rowOf(w, n('cy')).find('td.st').attributes('title')).toBe('Waiting for the heavy-work lease');
    expect(rowOf(w, n('eve')).find('td.st').attributes('title')).toBe('Waiting for a run slot');
    expect(rowOf(w, n('dee')).find('td.st').attributes('title')).toBe('Waits on your answer (a run is on it too)');
    // working: true for the three with an active run (the lease wait too), false for the slot wait.
    expect(['lead', 'cy', 'dee', 'eve'].map((k) => rowOf(w, n(k)).classes('working'))).toEqual([true, true, true, false]);
  });

  it('Who: the members working it now in the core\'s order (running, lease), the owner not shown twice', async () => {
    const w = await page();
    const faces = rowOf(w, n('lead')).findAll('[data-test=workflows-who] > .face').map((f) => f.attributes('title'));
    expect(faces).toEqual([expect.stringMatching(/Manager: running$/), expect.stringMatching(/Ben: waiting for the heavy-work lease$/)]);
    const eve = rowOf(w, n('eve')).findAll('[data-test=workflows-who] > .face').map((f) => f.attributes('title'));
    expect(eve[0]).toMatch(/Eve: queued for a run slot$/);
  });

  it('All: the ended rows after the open ones; Item links open the Backlog at the item (the archived one in the archive)', async () => {
    const w = await page();
    await w.find('[data-test=workflows-chip-all]').trigger('click');
    await flushPromises();
    expect(ids(w)).toHaveLength(7);
    expect(ids(w).slice(4).sort()).toEqual([n('dispatched'), n('archived'), n('expired')].sort());
    const link = rowOf(w, n('dispatched')).find('[data-test=workflow-item-link]');
    expect(link.text()).toBe(String(ID.spec));
    expect(rowOf(w, n('dispatched')).find('[data-test=workflows-title]').text()).toBe('Workflows page'); // never the dispatch sentence
    await link.trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: ID.spec, archived: false });
    await rowOf(w, n('archived')).find('[data-test=workflow-item-link]').trigger('click');
    expect(backlogRequest.value).toMatchObject({ label: ID.old, archived: true });
    expect(rowOf(w, n('expired')).find('[data-test=workflow-item-none]').exists()).toBe(true);
  });

  it('the time bar\'s segments are the core breakdown (same categories, same order, grow by ms)', async () => {
    const w = await page();
    for (const row of OPEN.workflows) {
      const segs = rowOf(w, row.id).findAll('[data-test=workflow-time-seg]');
      expect(segs.map((s) => s.attributes('data-category'))).toEqual(row.time!.breakdown.filter((b) => b.ms > 0).map((b) => b.category));
      expect(segs.map((s) => s.attributes('style'))).toEqual(row.time!.breakdown.filter((b) => b.ms > 0).map((b) => expect.stringContaining(`flex-grow: ${b.ms}`)));
      expect(timeBar(row.time).reduce((a, s) => a + s.pct, 0)).toBe(100);
    }
  });

  it('the pane on the real get: the running one\'s cards by lane, activity newest first, time breakdown; no spec without an item', async () => {
    const w = await page();
    await rowOf(w, n('lead')).trigger('click');
    await flushPromises();
    const pane = w.find('[data-test=workflow-pane]');
    expect(pane.find('[data-test=workflow-pane-title]').text()).toBe('lead it');
    expect(pane.find('[data-test=workflow-pane-spec]').exists()).toBe(false);
    expect(pane.findAll('.lane').map((l) => l.attributes('data-lane'))).toEqual(['todo', 'in-progress']);
    expect(pane.findAll('[data-test=workflow-pane-message]').map((m) => m.attributes('data-kind'))).toEqual(['progress', 'instruction', 'instruction']);
    expect(pane.findAll('[data-test=workflow-pane-message]')[1].text()).toContain('Ben');
    expect(pane.findAll('[data-test=workflow-pane-breakdown-row]')).toHaveLength(GETS[0].workflow.time!.breakdown.length);
    // Actions for full access: Stop (a run is on it) and Close; no Nudge (someone works it); no Extend (no expiry).
    expect(['nudge', 'extend', 'stop', 'close-wf'].map((a) => pane.find(`[data-test=workflow-pane-${a}]`).exists())).toEqual([false, false, true, true]);
  });

  it('the pane on the dispatched one: its spec rendered (core\'s markdown), the item link, no actions once ended', async () => {
    const w = await page();
    await w.find('[data-test=workflows-chip-all]').trigger('click');
    await flushPromises();
    await rowOf(w, n('dispatched')).trigger('click');
    await flushPromises();
    const pane = w.find('[data-test=workflow-pane]');
    expect(pane.find('[data-test=workflow-pane-spec]').html()).toContain('<strong>Why:</strong>');
    expect(pane.find('[data-test=workflow-pane-spec] h2').text()).toBe('Table');
    expect(pane.find('[data-test=workflow-item-link]').text()).toBe(String(ID.spec));
    expect(pane.find('[data-test=workflow-pane-actions]').exists()).toBe(false);
    expect(pane.findAll('[data-test=workflow-pane-message]')[0].attributes('data-kind')).toBe('completed');
  });

  it('a Visitor on the core\'s Cloud Link answers: the item links and spec stay (backlog true), no action anywhere', async () => {
    applyRemoteAccess('read');
    const f = fake({ all: VISITOR_LIST, open: VISITOR_LIST }, [VISITOR_GET]);
    const w = await page(f);
    const id = VISITOR_LIST.workflows[0].id;
    expect(rowOf(w, id).find('[data-test=workflow-item-link]').exists()).toBe(true);
    await rowOf(w, id).trigger('click');
    await flushPromises();
    const pane = w.find('[data-test=workflow-pane]');
    expect(pane.find('[data-test=workflow-pane-spec]').text()).toContain('the spec');
    expect(pane.find('[data-test=workflow-pane-actions]').exists()).toBe(false);
    expect(f.calls.map((c) => c.method).filter((m) => !m.startsWith('workflows.'))).toEqual([]);
  });

  it('backlog: false (a viewer that may not read the backlog): item labels are plain text and no spec section', async () => {
    const f = fake();
    f.shape = (a) => { a.backlog = false; if ('spec' in a) a.spec = null; return a; };
    const w = await page(f);
    await w.find('[data-test=workflows-chip-all]').trigger('click');
    await flushPromises();
    expect(rowOf(w, n('dispatched')).find('[data-test=workflow-item-link]').exists()).toBe(false);
    expect(rowOf(w, n('dispatched')).find('[data-test=workflow-item-text]').text()).toBe(String(ID.spec));
    await rowOf(w, n('dispatched')).trigger('click');
    await flushPromises();
    expect(w.find('[data-test=workflow-pane-spec]').exists()).toBe(false);
    expect(w.find('[data-test=workflow-pane] [data-test=workflow-item-link]').exists()).toBe(false);
  });
});

describe('time (spec: elapsed ticks live while open on the shared 1 s timer; Ines: it counts from when each answer arrived, not `now`)', () => {
  it('an open row ticks from its overallMs as answered; an ended one does not; the core\'s `now` changes nothing', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout', 'Date'] });
    vi.setSystemTime(Date.parse('2026-11-19T12:00:00Z'));
    const f = fake();
    // A core clock an hour off (another computer through the Cloud Link): the page must not jump by it.
    f.shape = (a) => { if ('now' in a) a.now = '2026-11-19T11:00:00Z'; return a; };
    const w = await page(f);
    await w.find('[data-test=workflows-chip-all]').trigger('click');
    await flushPromises();
    const value = (id: number) => rowOf(w, id).find('[data-test=overall-time-value]').text();
    const lead0 = value(n('lead')), done0 = value(n('dispatched'));
    await vi.advanceTimersByTimeAsync(5_000);
    const lead5 = value(n('lead'));
    expect(lead5).not.toBe(lead0);
    expect(value(n('dispatched'))).toBe(done0);
    // 0.4 s as answered + 5 s since it arrived: "5s" or "6s" (never an hour more, never the core's clock).
    expect(lead5).toMatch(/^0?[56]s$|^0m 0?[56]s$|^00:0[56]$/);
  });

  it('the Timeline link and the time both open the Timeline dialog for that workflow', async () => {
    const w = await page();
    await rowOf(w, n('lead')).find('[data-test=workflows-timeline]').trigger('click');
    expect(timelineTarget.value).toMatchObject({ kind: 'workflow', id: n('lead'), team: 'Web' });
    closeTimeline();
    await rowOf(w, n('cy')).find('[data-test=overall-time]').trigger('click');
    expect(timelineTarget.value).toMatchObject({ kind: 'workflow', id: n('cy') });
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false); // the click did not also select the row
  });
});

describe('the text filter on the real rows (spec: "over id, title, item label, team and member names ... the matches are highlighted")', () => {
  it('typing a member name keeps the workflow that member works now; typing the team highlights it', async () => {
    const w = await page();
    await w.find('[data-test=workflows-search]').setValue('ben');
    expect(ids(w)).toEqual([n('lead')]);
    await w.find('[data-test=workflows-search]').setValue('cloud');
    expect(ids(w).sort()).toEqual([n('dee'), n('eve')].sort());
    expect(rowOf(w, n('dee')).find('td.tm mark').text()).toBe('Cloud');
  });

  it('typing an item label filters to it and highlights the title, team or id matches', async () => {
    const w = await page();
    await w.find('[data-test=workflows-chip-all]').trigger('click');
    await flushPromises();
    await w.find('[data-test=workflows-search]').setValue(String(ID.spec).toLowerCase());
    expect(ids(w)).toEqual([n('dispatched')]);
  });

  // F1 (shell, minor): the item label is one of the filter's fields, but its match is not highlighted (the Item cell is
  // ItemLink, which has no highlight). Pinned with .fails: this test starts passing (and so failing here) once it is.
  it.fails('F1: the matched item label is highlighted in the Item column', async () => {
    const w = await page();
    await w.find('[data-test=workflows-chip-all]').trigger('click');
    await flushPromises();
    await w.find('[data-test=workflows-search]').setValue(String(ID.spec));
    expect(rowOf(w, n('dispatched')).find('td.itm mark').exists()).toBe(true);
  });
});

describe('the pure rules on the real rows', () => {
  it('the default order puts needs-you, then running, then the other open ones, then the ended ones by end time', () => {
    const order = sortRows(ALL.workflows, { key: 'default', desc: false }).map((w) => w.status);
    expect(order.slice(0, 2)).toEqual(['needs-you', 'running']);
    expect(order.slice(2, 4)).toEqual(['waiting', 'waiting']);
    expect(order.slice(4).every((s) => ['completed', 'closed', 'expired'].includes(s))).toBe(true);
    const ended = sortRows(ALL.workflows, { key: 'default', desc: false }).slice(4).map((w) => Date.parse(w.endedAt!));
    expect([...ended].sort((a, b) => b - a)).toEqual(ended);
  });

  it('statusTip for every row has words (no raw status leaks)', () => {
    for (const r of ALL.workflows) expect(statusTip(r)).toMatch(/^[A-Z]/);
  });
});
