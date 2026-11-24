// @vitest-environment happy-dom
// Card 430 (B086, shell): the overall time on cards and workflow rows and the Timeline dialog, against core card 429's
// wire contract (BoardCard.time, the workflow item's time, timeline.get) as the phone fake core serves it. The shell
// only formats: the open items tick on ONE shared 1 s timer. Fake timers throughout.
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import BoardCardItem from '../../src/renderer/src/board/BoardCardItem.vue';
import TimelineDialog from '../../src/renderer/src/timeline/TimelineDialog.vue';
import TeamWorkflows from '../../src/renderer/src/components/TeamWorkflows.vue';
import WorkflowsDialog from '../../src/renderer/src/components/WorkflowsDialog.vue';
import {
  BreakMinMs, buildAxis, clock, closeTimeline, formatDuration, formatShort, liveOverallMs, openTimeline, placeBreakLabels, shares, subscribeNow, tickerState,
} from '../../src/renderer/src/timeline/time';
import { createFakeCore } from '../e2e/phone/fake-core';
import type { BoardCard, ItemTime, YawbleApi } from '../../src/shared/api';

const MIN = 60_000;

function fakeApi(access: 'full' | 'read' = 'full', drop: string[] = []): YawbleApi & { asked: { method: string; params: unknown }[] } {
  const core = createFakeCore();
  core.setAccess(access);
  let id = 0;
  const asked: { method: string; params: unknown }[] = [];
  return {
    asked,
    getInfo: async () => ({}) as any,
    getAvailableBackends: async () => [],
    call: async (method: string, params?: unknown) => {
      asked.push({ method, params });
      if (drop.includes(method)) throw Object.assign(new Error(`${method} is not known`), { code: 'method_not_found' });
      const r = JSON.parse(core.answer(JSON.stringify({ id: ++id, method, params }))!);
      if (r.error) throw Object.assign(new Error(r.error.message), { code: r.error.code });
      return r.result;
    },
    onEvent: () => () => {},
  } as any;
}
const callFake = async (method: string, params?: unknown) => fakeApi().call<any>(method, params);

const openTime = (overallMs: number): ItemTime => ({ createdAt: new Date(Date.now() - overallMs).toISOString(), open: true, overallMs, rebuilt: false, breakdown: [{ category: 'agent-working', ms: overallMs }] });
const doneTime = (overallMs: number): ItemTime => ({ ...openTime(overallMs), open: false, endedAt: new Date().toISOString(), endedHow: 'done' });
const card = (id: number, time?: ItemTime | null): BoardCard => ({
  id, team: 'payments', teamId: 1, member: null, item: 'B086', title: `Card ${id}`, body: '', status: 'running', lane: 'in-progress', placed: false,
  awaitingManager: false, workflow: 10, createdAt: '', updatedAt: '', trailCount: 2, time,
});

enableAutoUnmount(afterEach);
beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(new Date('2026-11-18T18:00:00Z')); });
afterEach(() => { closeTimeline(); vi.useRealTimers(); vi.restoreAllMocks(); document.body.innerHTML = ''; });

describe('card 430: formatting', () => {
  it('says a length as "1h 12m 05s", minutes and seconds padded, a day or more without seconds', () => {
    expect(formatDuration((72 * 60 + 5) * 1000)).toBe('1h 12m 05s');
    expect(formatDuration(12 * MIN + 5000)).toBe('12m 05s');
    expect(formatDuration(45_999)).toBe('45s');
    expect(formatDuration(0)).toBe('0s');
    expect(formatDuration(-5000)).toBe('0s');
    expect(formatDuration(3 * 3600_000)).toBe('3h 00m 00s');
    expect(formatDuration((2 * 24 + 3) * 3600_000 + 12 * MIN + 9000)).toBe('2d 3h 12m');
  });
  it('says a short length with its two largest units: "53m", "1h 12m", "2d 3h"', () => {
    expect(formatShort(53 * MIN)).toBe('53m');
    expect(formatShort(72 * MIN + 30_000)).toBe('1h 12m');
    expect(formatShort(3600_000)).toBe('1h');
    expect(formatShort(51 * 3600_000)).toBe('2d 3h');
    expect(formatShort(9000)).toBe('9s');
  });
  it('gives shares in whole percents that always add up to 100', () => {
    expect(shares([1, 1, 1])).toEqual([34, 33, 33]);
    expect(shares([38, 100, 42]).reduce((a, b) => a + b)).toBe(100);
    expect(shares([0, 0])).toEqual([0, 0]);
    for (let i = 0; i < 50; i++) {
      const parts = Array.from({ length: 1 + (i % 7) }, (_, k) => ((i + 1) * (k + 3) * 7919) % 10_000);
      if (parts.some((p) => p > 0)) expect(shares(parts).reduce((a, b) => a + b)).toBe(100);
    }
  });
  it('says clock times 24 h, with the day only when it is another day', () => {
    expect(clock('2026-11-18T16:01:00')).toMatch(/^16:01$/);
    expect(clock('2026-11-18T16:54:00', '2026-11-18T09:00:00')).toMatch(/^16:54$/);
    expect(clock('2026-11-19T08:05:00', '2026-11-18T16:01:00')).toMatch(/08:05$/);
    expect(clock('2026-11-19T08:05:00', '2026-11-18T16:01:00')).not.toMatch(/^08:05$/);
    expect(clock(null)).toBe('');
  });
});

describe('card 430: the one shared 1 s timer', () => {
  it('an open item counts on from the core answer; a closed one stays as the core said it', () => {
    const o = openTime(10 * MIN), d = doneTime(10 * MIN);
    const t0 = Date.now();
    expect(liveOverallMs(o, t0)).toBe(10 * MIN);
    vi.advanceTimersByTime(5000);
    expect(liveOverallMs(o, Date.now())).toBe(10 * MIN + 5000);
    expect(liveOverallMs(d, Date.now())).toBe(10 * MIN);
  });

  it('many open cards tick together on ONE interval, which stops when the last one goes', async () => {
    const spy = vi.spyOn(globalThis, 'setInterval');
    const wrappers = [1, 2, 3, 4, 5].map((i) => mount(BoardCardItem, { props: { card: card(i, openTime((60 + i) * MIN)) } }));
    const closed = mount(BoardCardItem, { props: { card: card(9, doneTime(3 * 3600_000)) } });
    expect(tickerState()).toEqual({ users: 5, running: true });
    expect(spy).toHaveBeenCalledTimes(1);
    expect(wrappers[0].get('[data-test="overall-time-value"]').text()).toBe('1h 01m 00s');
    vi.advanceTimersByTime(3000);
    await flushPromises();
    expect(wrappers.map((w) => w.get('[data-test="overall-time-value"]').text())).toEqual(['1h 01m 03s', '1h 02m 03s', '1h 03m 03s', '1h 04m 03s', '1h 05m 03s']);
    expect(closed.get('[data-test="overall-time-value"]').text()).toBe('3h 00m 00s');
    expect(closed.get('[data-test="overall-time"]').attributes('data-open')).toBe('false');
    expect(spy).toHaveBeenCalledTimes(1);
    wrappers.forEach((w) => w.unmount());
    expect(tickerState()).toEqual({ users: 0, running: false });
    closed.unmount();
  });

  it('a card that closes leaves the timer; an unsubscribe twice counts once', async () => {
    const w = mount(BoardCardItem, { props: { card: card(1, openTime(MIN)) } });
    expect(tickerState().users).toBe(1);
    await w.setProps({ card: card(1, doneTime(2 * MIN)) });
    expect(tickerState()).toEqual({ users: 0, running: false });
    const off = subscribeNow();
    off(); off();
    expect(tickerState()).toEqual({ users: 0, running: false });
    w.unmount();
  });

  it('a card from an older core (no time) shows none; the time stays in the one-line bottom row', () => {
    expect(mount(BoardCardItem, { props: { card: card(1) } }).find('[data-test="overall-time"]').exists()).toBe(false);
    const w = mount(BoardCardItem, { props: { card: card(2, doneTime(MIN)) } });
    expect(w.get('.foot').find('[data-test="overall-time"]').exists()).toBe(true);
  });

  it('clicking the time opens the timeline and does not open the card', async () => {
    const w = mount(BoardCardItem, { props: { card: card(7, doneTime(MIN)) } });
    await w.get('[data-test="overall-time"]').trigger('click');
    expect(w.emitted('open')).toBeUndefined();
    const { timelineTarget } = await import('../../src/renderer/src/timeline/time');
    expect(timelineTarget.value).toMatchObject({ kind: 'card', id: 7 });
    await w.get('[data-test="overall-time"]').trigger('keydown', { key: 'Enter' });
    expect(w.emitted('open')).toBeUndefined();
  });
});

describe('card 430: the axis', () => {
  it('compresses a long quiet stretch behind a break and keeps the rest in order', () => {
    const s = 0, e = 10 * 3600_000;
    const edges = [10 * MIN, 30 * MIN, 9 * 3600_000];
    const a = buildAxis(s, e, edges);
    expect(a.breaks).toHaveLength(1);
    expect(a.breaks[0].ms).toBe(9 * 3600_000 - 30 * MIN);
    const xs = [s, ...edges, e].map(a.at);
    for (let i = 1; i < xs.length; i++) expect(xs[i]).toBeGreaterThan(xs[i - 1]);
    // the 8.5 h of nothing takes far less room than its share of the time
    expect(a.at(9 * 3600_000) - a.at(30 * MIN)).toBeLessThan(0.1);
    expect(a.at(s)).toBe(0); expect(a.at(e)).toBe(1);
  });
  it('compresses nothing when no stretch is both long and a big share', () => {
    expect(buildAxis(0, 3 * 3600_000, [3600_000, 2 * 3600_000]).breaks).toHaveLength(0);
    expect(buildAxis(0, 4 * BreakMinMs, []).breaks).toHaveLength(0);
  });
});

describe('card 430: the fake cores carry the contract', () => {
  it('every card and workflow #10 have a time whose breakdown adds up to overallMs exactly', async () => {
    const list = await callFake('board.list');
    for (const c of list.cards) expect(c.time.breakdown.reduce((a: number, b: any) => a + b.ms, 0)).toBe(c.time.overallMs);
    const wfs = await callFake('bus.workflows', { teamId: 1 });
    const w = wfs.find((x: any) => x.id === 10);
    expect(w.time.breakdown.reduce((a: number, b: any) => a + b.ms, 0)).toBe(w.time.overallMs);
    const tl = await callFake('timeline.get', { workflow: 10 });
    expect(tl.rows).toHaveLength(6);
    expect(tl.summary.length).toBeGreaterThan(0);
    expect(tl.time.overallMs).toBe(w.time.overallMs);
  });
});

describe('card 430: the Timeline dialog', () => {
  async function dialog(target: Parameters<typeof openTimeline>[0], api = fakeApi()) {
    const w = mount(TimelineDialog, { props: { api }, attachTo: document.body });
    openTimeline(target);
    await flushPromises();
    return { w, api };
  }

  it('a workflow: overall, open, a summary row on top and one row per card, a breakdown that adds up, legend and markers', async () => {
    const { w, api } = await dialog({ kind: 'workflow', id: 10, title: 'Partial refunds' });
    expect(api.asked.find((a) => a.method === 'timeline.get')!.params).toEqual({ workflow: 10 });
    expect(w.get('[data-test="timeline-overall-value"]').text()).toBe('2h 00m 00s');
    expect(w.find('[data-test="timeline-open"]').exists()).toBe(true);
    expect(w.get('[data-test="timeline-started"]').text()).toMatch(/^first message \d\d:\d\d · first card started \d\d:\d\d · 10m before it started$/);
    const rows = w.findAll('[data-test="timeline-row"]');
    expect(rows).toHaveLength(7);
    expect(rows[0].text()).toContain('All cards');
    expect(rows[1].text()).toContain('card 1');
    // the breakdown: its shares add up to 100 and its times to the total, which is the overall as the core said it
    const shareSum = w.findAll('[data-test="timeline-breakdown-row"]').map((r) => Number(r.findAll('td')[2].text().replace('%', ''))).reduce((a, b) => a + b, 0);
    expect(shareSum).toBe(100);
    expect(w.get('[data-test="timeline-breakdown-total"]').text()).toContain('2h 00m 00s');
    expect(w.findAll('[data-test="timeline-breakdown-row"]').map((r) => r.attributes('data-category'))[0]).toBe('agent-working');
    // Stalled is in the legend, apart from Agent working
    expect(w.get('[data-test="timeline-legend"]').text()).toContain('Stalled');
    expect(w.findAll('.band.stalled').length).toBeGreaterThan(0);
    expect(w.findAll('[data-test="timeline-marker"]').map((m) => m.attributes('data-kind'))).toEqual(expect.arrayContaining(['planned', 'started', 'watchdog-end', 'reopen', 'decision-asked', 'handback']));
    // a band is focusable with a text equivalent, and says it in the readout on focus
    const band = w.get('.band.stalled');
    expect(band.element.tagName).toBe('BUTTON');
    expect(band.attributes('aria-label')).toMatch(/^Stalled, \d\d:\d\d:\d\d to \d\d:\d\d:\d\d, 38m 00s$/);
    await band.trigger('focus');
    expect(w.get('[data-test="timeline-readout"]').text()).toMatch(/^card 3 · Developer Ada: Stalled/);
    // ticks on the shared clock while open
    vi.advanceTimersByTime(2000);
    await flushPromises();
    expect(w.get('[data-test="timeline-overall-value"]').text()).toBe('2h 00m 02s');
    w.unmount();
  });

  it('an open item is asked again every 30 s on the shared clock; a closed one is not', async () => {
    const { w, api } = await dialog({ kind: 'card', id: 3 });
    const n = () => api.asked.filter((a) => a.method === 'timeline.get').length;
    expect(n()).toBe(1);
    vi.advanceTimersByTime(30_000);
    await flushPromises();
    expect(n()).toBe(2);
    openTimeline({ kind: 'card', id: 5 });
    await flushPromises();
    const at = n();
    vi.advanceTimersByTime(60_000);
    await flushPromises();
    expect(n()).toBe(at);
    w.unmount();
  });

  it('a done card: planned · started · done and how long before it started; its own row', async () => {
    const { w, api } = await dialog({ kind: 'card', id: 5 });
    expect(api.asked.find((a) => a.method === 'timeline.get')!.params).toEqual({ card: 5 });
    expect(w.get('[data-test="timeline-started"]').text()).toMatch(/^planned \d\d:\d\d · started \d\d:\d\d · done \d\d:\d\d · 10m before it started$/);
    expect(w.get('[data-test="timeline-span"]').text()).toContain('(done)');
    expect(w.get('[data-test="timeline-overall-value"]').text()).toBe('1h 00m 00s');
    expect(w.findAll('[data-test="timeline-row"]')).toHaveLength(1);
    expect(w.find('[data-test="timeline-rebuilt"]').exists()).toBe(false);
    w.unmount();
  });

  it('a card rebuilt from history says so', async () => {
    const { w } = await dialog({ kind: 'card', id: 6 });
    expect(w.get('[data-test="timeline-rebuilt"]').text()).toContain('Rebuilt from history');
    w.unmount();
  });

  it('a long quiet stretch is drawn with a break mark (expired workflow #9: expired marker, axis to its end)', async () => {
    const core = fakeApi();
    // the expiry fixtures list #9; switch them on through the fake's own setter
    const c = createFakeCore(); c.setExpiry(true);
    const { w } = await dialog({ kind: 'workflow', id: 9 }, core);
    expect(w.get('[data-test="timeline-span"]').text()).toContain('(expired)');
    expect(w.findAll('[data-test="timeline-break"]').length).toBe(1);
    expect(w.findAll('[data-test="timeline-marker"]').map((m) => m.attributes('data-kind'))).toContain('expired');
    c.setExpiry(false);
    w.unmount();
  });

  it('a Visitor (read access) sees the same dialog', async () => {
    const { w } = await dialog({ kind: 'workflow', id: 10 }, fakeApi('read'));
    expect(w.find('[data-test="timeline-error"]').exists()).toBe(false);
    expect(w.findAll('[data-test="timeline-row"]')).toHaveLength(7);
    w.unmount();
  });

  it('an older core without timeline.get says so', async () => {
    const { w } = await dialog({ kind: 'card', id: 3 }, fakeApi('full', ['timeline.get']));
    expect(w.get('[data-test="timeline-error"]').text()).toContain('older');
    w.unmount();
  });
});

describe('card 430: the workflow rows', () => {
  it('the Teams list tile row and the Workflows dialog subtitle show the overall time; clicking it opens the timeline, not the row', async () => {
    const api = fakeApi();
    const tile = mount(TeamWorkflows, { props: { teamId: 1, api } });
    await flushPromises();
    const row = tile.get('[data-test="team-workflow"][data-id="10"]');
    expect(row.get('[data-test="overall-time-value"]').text()).toBe('2h 00m 00s');
    await row.get('[data-test="overall-time"]').trigger('click');
    expect(tile.emitted('open')).toBeUndefined();
    const { timelineTarget } = await import('../../src/renderer/src/timeline/time');
    expect(timelineTarget.value).toMatchObject({ kind: 'workflow', id: 10 });
    closeTimeline();

    const dlg = mount(WorkflowsDialog, { props: { api, team: { id: 1, name: 'payments' } as any }, attachTo: document.body });
    await flushPromises();
    const meta = dlg.get('[data-test="workflow-10"] .meta');
    expect(meta.text()).toMatch(/started .* \(\s*2h 00m 00s\s*\)/);
    vi.advanceTimersByTime(1000);
    await flushPromises();
    expect(meta.get('[data-test="overall-time-value"]').text()).toBe('2h 00m 01s');
  });
});

describe('card 433 F3: placeBreakLabels keeps a break label clear of the start / end labels', () => {
  // a 560 px axis, start label 0..40, end label 530..560 ("now"), gap 6
  it('a label centred well inside stays centred under its mark', () => {
    expect(placeBreakLabels(560, 40, 530, [{ x: 280, w: 60 }])).toEqual([250]);
  });
  it('a label near the end slides left until it clears the end label', () => {
    // centred at 96%: 537.6 - 30 = 507.6, would end at 567.6 over "now"; slid to end at 530 - 6
    expect(placeBreakLabels(560, 40, 530, [{ x: 537.6, w: 60 }])).toEqual([464]);
  });
  it('a label near the start slides right until it clears the start label', () => {
    expect(placeBreakLabels(560, 40, 530, [{ x: 10, w: 60 }])).toEqual([46]);
  });
  it('a second label that would meet the first is pushed after it, and one with no room left hides', () => {
    expect(placeBreakLabels(560, 40, 530, [{ x: 280, w: 60 }, { x: 300, w: 60 }])).toEqual([250, 316]);
    expect(placeBreakLabels(200, 40, 170, [{ x: 100, w: 60 }, { x: 120, w: 60 }])).toEqual([70, null]);
  });
  it('a label wider than the room between the start and end labels hides', () => {
    expect(placeBreakLabels(120, 40, 90, [{ x: 60, w: 60 }])).toEqual([null]);
  });
});
