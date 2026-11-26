// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { YawbleApi } from '../../src/shared/api';
import {
  memberKey, MonitorModel, NeedYouForgetMs, needYouSummary, type Effect, type FeedRow, type TeamSnapshot,
} from '../../src/renderer/src/monitor/model';
import { MonitorSound, SoundGapMs, type SoundKind } from '../../src/renderer/src/monitor/sound';

/**
 * One "need you" list (questions, network asks, tool asks, blocked and failed members, each once) that
 * the Monitor's header and its You node both count; the You node's breakdown by kind; and the needs-you sound and pulse
 * firing once per new item, keyed by its id.
 */

type State = TeamSnapshot['members'][number]['state'];
const member = (name: string, state: State = 'idle', statusLine: string | null = null, id = 1) =>
  ({ id, name, role: 'member' as const, state, queued: 0, statusLine, cli: 'claude' });
const team = (name: string, members: ReturnType<typeof member>[], id = 1): TeamSnapshot => ({ id, name, members });
const row = (seq: number, kind: FeedRow['kind'], memberName: string, text: string | null = null, teamName = 'payments'): FeedRow =>
  ({ seq, kind, team: teamName, member: memberName, from: memberName, text, deliverable: 1, at: new Date().toISOString() });
const youFx = (m: MonitorModel) => m.effects.filter((e): e is Extract<Effect, { kind: 'you' }> => e.kind === 'you');
/** The header's count and the You node's count: the same list's length. */
const both = (m: MonitorModel) => ({ header: m.counts().needYou, you: m.needYouItems().length });

describe('needYouItems: one list for the header and the You node', () => {
  it('only questions: each member asking is one item; the header says the same', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'decision'), member('Bo', 'decision'), member('Cy', 'running')])], 0);
    m.apply(row(1, 'decision', 'Ana', 'MIT or Apache?'), 1);
    const items = m.needYouItems();
    expect(items.map((i) => [i.kind, i.member, i.id])).toEqual([
      ['question', 'Ana', 'q:payments/ana'], ['question', 'Bo', 'q:payments/bo'],
    ]);
    expect(items[0]!.label).toBe('MIT or Apache?');
    expect(both(m)).toEqual({ header: 2, you: 2 });
    expect(needYouSummary(items)).toBe('2 questions');
  });

  it('only asks: network and tool asks count in the header', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'running')])], 0);
    m.setAsks([
      { key: 'n:payments/ana/nuget.org', kind: 'network', team: 'payments', member: 'Ana', label: 'api.nuget.org' },
      { key: 't:7', kind: 'tool', team: 'alpha', member: 'Bo', label: '.NET SDK' },
      { key: 't:8', team: 'alpha', member: null }, // no kind: read from the key
    ], 0);
    expect(m.needYouItems().map((i) => [i.kind, i.team, i.member])).toEqual([
      ['network', 'payments', 'Ana'], ['tool', 'alpha', 'Bo'], ['tool', 'alpha', null],
    ]);
    expect(both(m)).toEqual({ header: 3, you: 3 });
    expect(needYouSummary(m.needYouItems())).toBe('1 network ask, 2 tool asks');
  });

  it('only blocked and failed: they count on the You node too', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'blocked', 'no host'), member('Bo', 'failed'), member('Cy', 'idle')])], 0);
    expect(m.needYouItems().map((i) => [i.kind, i.member, i.id, i.label])).toEqual([
      ['blocked', 'Ana', 'b:payments/ana', 'no host'], ['failed', 'Bo', 'f:payments/bo', null],
    ]);
    expect(both(m)).toEqual({ header: 2, you: 2 });
    expect(needYouSummary(m.needYouItems())).toBe('1 blocked, 1 failed');
  });

  it('a mix: a member asking and also asking for a host is two items; listed by kind', () => {
    const m = new MonitorModel();
    m.reconcile([
      team('payments', [member('Ana', 'decision'), member('Bo', 'failed')], 1),
      team('alpha', [member('Cy', 'decision'), member('Di', 'blocked')], 2),
    ], 0);
    m.setAsks([
      { key: 'n:1', kind: 'network', team: 'payments', member: 'Ana', label: 'registry.npmjs.org' },
      { key: 't:2', kind: 'tool', team: 'alpha', member: 'Di', label: 'python' },
    ], 0);
    const items = m.needYouItems();
    expect(items.map((i) => i.kind)).toEqual(['question', 'question', 'network', 'tool', 'blocked', 'failed']);
    expect(items.filter((i) => i.member === 'Ana').map((i) => i.kind)).toEqual(['question', 'network']);
    expect(new Set(items.map((i) => i.id)).size).toBe(items.length); // each once
    expect(both(m)).toEqual({ header: 6, you: 6 });
    expect(needYouSummary(items)).toBe('2 questions, 1 network ask, 1 tool ask, 1 blocked, 1 failed');
    expect(needYouSummary([])).toBe('Nothing needs you');
  });

  it('items resolving: the count goes down on both, to nothing', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'decision'), member('Bo', 'blocked')])], 0);
    m.setAsks([{ key: 't:7', kind: 'tool', team: 'payments', member: 'Bo' }], 0);
    expect(both(m)).toEqual({ header: 3, you: 3 });
    m.reconcile([team('payments', [member('Ana', 'running'), member('Bo', 'blocked')])], 10); // answered
    expect(both(m)).toEqual({ header: 2, you: 2 });
    m.setAsks([], 20); // the tool is installed
    expect(both(m)).toEqual({ header: 1, you: 1 });
    m.reconcile([team('payments', [member('Ana', 'idle'), member('Bo', 'running')])], 30);
    expect(both(m)).toEqual({ header: 0, you: 0 });
    expect(m.counts()).toMatchObject({ running: 1, members: 2 });
  });
});

describe('the needs-you pulse and sound fire once per new item, by id', () => {
  it('a new item flies once; the next poll and a change event for the same list fire nothing', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'idle')])], 0);
    const asks = [{ key: 'n:1', kind: 'network' as const, team: 'payments', member: 'Ana', label: 'api.nuget.org' }];
    m.setAsks(asks, 10); // the change event
    m.setAsks(asks, 15); // the 5 s poll reads the same
    m.syncNeedYou(20); // the tick
    m.setAsks(asks, 5010);
    expect(youFx(m)).toEqual([{ kind: 'you', from: memberKey('payments', 'Ana'), color: 'decision', label: 'api.nuget.org', at: 10, id: 'a:n:1' }]);
  });

  it('blocked and failed fire too, in their own colours, once each across ticks', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'blocked'), member('Bo', 'failed')])], 0);
    m.syncNeedYou(1);
    m.syncNeedYou(2);
    m.reconcile([team('payments', [member('Ana', 'blocked'), member('Bo', 'failed')])], 3);
    m.syncNeedYou(4);
    expect(youFx(m).map((e) => [e.id, e.color, e.label, e.at])).toEqual([
      ['b:payments/ana', 'blocked', 'blocked', 1], ['f:payments/bo', 'fail', 'failed', 1],
    ]);
  });

  it('a question flies with its row, and the snapshot showing it does not fly it again', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'running')])], 0);
    m.apply(row(1, 'decision', 'Ana', 'Ship it?'), 5); // the feed is ahead of the snapshot
    m.syncNeedYou(6);
    m.reconcile([team('payments', [member('Ana', 'decision')])], 10);
    m.syncNeedYou(11);
    m.apply(row(2, 'decision', 'Ana', 'Ship it now?'), 12); // the same member still asking: the same item
    m.syncNeedYou(13);
    expect(youFx(m).map((e) => [e.id, e.label, e.at])).toEqual([['q:payments/ana', 'Ship it?', 5]]);
    // A snapshot that shows the question first: it flies from the list, once, with the row's words when they came.
    const n = new MonitorModel();
    n.reconcile([team('payments', [member('Bo', 'decision')])], 0);
    n.syncNeedYou(1);
    n.apply(row(3, 'decision', 'Bo', 'Which port?'), 2);
    n.syncNeedYou(3);
    expect(youFx(n).map((e) => e.id)).toEqual(['q:payments/bo']);
    expect(n.needYouItems()[0]!.label).toBe('Which port?');
  });

  it('an item that resolves and comes back is new and fires again; a row the snapshot never showed is forgotten', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'blocked')])], 0);
    m.syncNeedYou(1);
    m.reconcile([team('payments', [member('Ana', 'running')])], 2);
    m.syncNeedYou(3);
    m.reconcile([team('payments', [member('Ana', 'blocked')])], 4);
    m.syncNeedYou(5);
    expect(youFx(m).map((e) => [e.id, e.at])).toEqual([['b:payments/ana', 1], ['b:payments/ana', 5]]);
    // A question answered before any snapshot showed it: after NeedYouForgetMs the member's next question flies.
    const n = new MonitorModel();
    n.reconcile([team('payments', [member('Bo', 'running')])], 0);
    n.apply(row(1, 'decision', 'Bo', 'One?'), 10);
    n.syncNeedYou(20);
    n.apply(row(2, 'decision', 'Bo', 'Two?'), 30); // too soon: taken as the same question
    n.syncNeedYou(10 + NeedYouForgetMs + 1);
    n.apply(row(3, 'decision', 'Bo', 'Three?'), 10 + NeedYouForgetMs + 2);
    expect(youFx(n).map((e) => e.label)).toEqual(['One?', 'Three?']);
  });

  it('play false (the Monitor opening): what is there is counted, not flown, and never flies later', () => {
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'decision'), member('Bo', 'failed')])], 0);
    expect(m.syncNeedYou(0, false)).toHaveLength(2);
    expect(youFx(m)).toHaveLength(0);
    m.reconcile([team('payments', [member('Ana', 'decision'), member('Bo', 'failed'), member('Cy', 'blocked')])], 10);
    m.syncNeedYou(11);
    expect(youFx(m).map((e) => e.id)).toEqual(['b:payments/cy']);
  });

  it('the sound: one needs-you tone per new item (blocked and failed included), none for the same item again', () => {
    const played: SoundKind[] = [];
    const s = new MonitorSound((k) => played.push(k));
    s.on = true;
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'idle')])], 0);
    const drain = (now: number) => s.hear(m.effects.splice(0), now);
    m.setAsks([{ key: 't:7', kind: 'tool', team: 'payments', member: 'Ana' }], 0);
    expect(drain(0)).toEqual(['needsYou']);
    // The same ask again by the poll and the event, well after the gap: silent.
    m.setAsks([{ key: 't:7', kind: 'tool', team: 'payments', member: 'Ana' }], SoundGapMs * 2);
    m.syncNeedYou(SoundGapMs * 2);
    expect(drain(SoundGapMs * 2)).toEqual([]);
    // Ana is blocked: a new item, it sounds.
    m.reconcile([team('payments', [member('Ana', 'blocked')])], SoundGapMs * 3);
    m.syncNeedYou(SoundGapMs * 3);
    expect(drain(SoundGapMs * 3)).toEqual(['needsYou']);
    m.syncNeedYou(SoundGapMs * 5);
    expect(drain(SoundGapMs * 5)).toEqual([]);
    expect(played).toEqual(['needsYou', 'needsYou']);
  });

  it('a question\'s own burst on its member does not sound needs you (its flyer does, once)', () => {
    const played: SoundKind[] = [];
    const s = new MonitorSound((k) => played.push(k));
    s.on = true;
    const m = new MonitorModel();
    m.reconcile([team('payments', [member('Ana', 'decision')])], 0);
    m.apply(row(1, 'decision', 'Ana', 'Q?'), 0);
    m.syncNeedYou(0);
    s.hear(m.effects.splice(0), 0);
    m.apply(row(2, 'decision', 'Ana', 'Q again?'), SoundGapMs * 2); // its burst again, the same item
    m.syncNeedYou(SoundGapMs * 2);
    s.hear(m.effects.splice(0), SoundGapMs * 2);
    expect(played).toEqual(['needsYou']);
  });
});

// The view: the header and the You node's breakdown read the same list.
type Fake = { onHover?: (h: unknown) => void; model: MonitorModel; you: { x: number; y: number; r: number } | null };
const scenes: Fake[] = [];
vi.mock('../../src/renderer/src/monitor/scene', async (orig) => {
  const real = await orig<typeof import('../../src/renderer/src/monitor/scene')>();
  class FakeScene {
    model: unknown; onHover?: unknown; onPick?: unknown; onOpenDeliverable?: unknown; effects: unknown[] = []; you: { x: number; y: number; r: number } | null = null;
    constructor() { scenes.push(this as unknown as Fake); }
    hitYouNode(x: number, y: number) { return !!this.you && Math.hypot(x - this.you.x, y - this.you.y) <= this.you.r; }
    setPinned() {} setLayout() {} setTeams() {} setPicked() {} destroy() {} fit() {} clearFiles() {}
  }
  return { ...real, MonitorScene: FakeScene };
});
const { default: MonitorView } = await import('../../src/renderer/src/monitor/MonitorView.vue');

const card = (id: number, name: string, attention: string | null, state = 'idle') =>
  ({ member: { id, teamId: 1, team: 'payments', name, role: 'member', cli: 'claude' }, state, attention, queuedInstructions: 0, statusLine: null });
function api(): YawbleApi {
  return {
    call: async (method: string) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 3, running: 0, queued: 0 }] } as any;
        case 'teams.agents': return [card(1, 'DeveloperAna', 'decision'), card(2, 'TesterBo', 'failed'), card(3, 'DeveloperCy', null, 'running')] as any;
        case 'monitor.messages': return { end: 0, messages: [] } as any;
        case 'monitor.steps': return { end: 0, pending: false, steps: [] } as any;
        case 'network.asks': return [{ id: 1, at: '', teamId: 1, team: 'payments', member: 'payments/DeveloperAna', memberName: 'DeveloperAna', host: 'api.nuget.org', count: 1, lastSeen: '', state: 'pending', choices: [] }] as any;
        case 'tools.asks': return [] as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
}
const settle = async (n = 8) => { for (let i = 0; i < n; i++) await flushPromises(); };
const mounted: VueWrapper[] = [];
let narrow = false;
async function view() {
  const w = mount(MonitorView, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
  mounted.push(w);
  await settle();
  return { w, scene: scenes.at(-1)!, cv: w.get('[data-test=monitor-canvas]').element as HTMLCanvasElement };
}
function tap(cv: HTMLCanvasElement, x: number, y: number) {
  cv.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, clientX: x, clientY: y, pointerId: 1 }));
  cv.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, clientX: x, clientY: y, pointerId: 1 }));
}

describe('the Monitor view: the header and the You node agree, and the You node says what is in it', () => {
  beforeEach(() => {
    localStorage.clear(); document.body.innerHTML = ''; scenes.length = 0; narrow = false;
    HTMLCanvasElement.prototype.getContext = (() => ({})) as any;
    vi.stubGlobal('matchMedia', () => ({ matches: narrow, addEventListener() {}, removeEventListener() {} }));
  });
  afterEach(() => { for (const w of mounted.splice(0)) w.unmount(); vi.unstubAllGlobals(); });

  it('the header counts the question, the network ask and the failed run: 3, the You node\'s list', async () => {
    const { w, scene } = await view();
    expect(w.get('[data-test=monitor-counts]').text()).toContain('3 need you');
    expect(scene.model.needYouItems()).toHaveLength(3);
    // Hovering the You node: the breakdown by kind, then one line per item with team / member.
    expect(w.find('[data-test=monitor-you-tip]').exists()).toBe(false);
    scene.onHover!({ key: 'you', kind: 'you', team: '', name: 'You', x: 900, y: 300 });
    await settle(2);
    expect(w.get('[data-test=monitor-you-summary]').text()).toBe('1 question, 1 network ask, 1 failed');
    expect(w.findAll('[data-test=monitor-you-item]').map((l) => [l.attributes('data-kind'), l.text().replace(/\s+/g, ' ').trim()])).toEqual([
      ['question', 'question payments / Developer Ana'],
      ['network', 'network ask payments / Developer Ana · api.nuget.org'],
      ['failed', 'failed payments / Tester Bo'],
    ]);
    expect(w.find('[data-test=monitor-tip]').exists()).toBe(false); // not the plain tip
    scene.onHover!(null);
    await settle(2);
    expect(w.find('[data-test=monitor-you-tip]').exists()).toBe(false);
  });

  it('those waiting when the Monitor opens are counted, not flown in', async () => {
    const { scene } = await view();
    expect(scene.model.effects.filter((e) => e.kind === 'you')).toHaveLength(0);
  });

  it('on a phone a tap on the You node shows the breakdown; its button opens Needs you; a tap elsewhere hides it', async () => {
    narrow = true;
    const { w, scene, cv } = await view();
    scene.you = { x: 200, y: 150, r: 20 };
    tap(cv, 200, 150);
    await settle(2);
    expect(w.emitted('openNeedsYou')).toBeUndefined();
    expect(w.get('[data-test=monitor-you-summary]').text()).toBe('1 question, 1 network ask, 1 failed');
    await w.get('[data-test=monitor-you-open]').trigger('click');
    expect(w.emitted('openNeedsYou')).toHaveLength(1);
    expect(w.find('[data-test=monitor-you-tip]').exists()).toBe(false);
    tap(cv, 200, 150);
    await settle(2);
    expect(w.find('[data-test=monitor-you-tip]').exists()).toBe(true);
    tap(cv, 20, 20);
    await settle(2);
    expect(w.find('[data-test=monitor-you-tip]').exists()).toBe(false);
  });

  it('on a desktop a click on the You node opens Needs you straight away', async () => {
    const { w, scene, cv } = await view();
    scene.you = { x: 200, y: 150, r: 20 };
    tap(cv, 200, 150);
    await settle(2);
    expect(w.emitted('openNeedsYou')).toHaveLength(1);
    expect(w.find('[data-test=monitor-you-tip]').exists()).toBe(false);
  });
});
