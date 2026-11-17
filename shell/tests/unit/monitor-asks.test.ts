// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { NetworkAsk, ToolAsk, YawbleApi } from '../../src/shared/api';
import { MonitorModel } from '../../src/renderer/src/monitor/model';

// Card 386 (B080 fix F2): the Monitor view reads the waiting network and tool asks the way Needs you does
// (network.asks / tools.asks, their change events, a slow poll) and hands them to the live model's setAsks, so they
// count on the You node. The wall is a fake scene; the model is the real one.
vi.mock('../../src/renderer/src/monitor/scene', async (orig) => {
  const real = await orig<typeof import('../../src/renderer/src/monitor/scene')>();
  class FakeScene {
    model: unknown; onHover?: unknown; onPick?: unknown; onOpenWorkflow?: unknown; effects: unknown[] = [];
    setPinned() {} setLayout() {} setTeams() {} setPicked() {} destroy() {} fit() {} clearFiles() {}
  }
  return { ...real, MonitorScene: FakeScene };
});
const { default: MonitorView } = await import('../../src/renderer/src/monitor/MonitorView.vue');

const net = (o: Partial<NetworkAsk>): NetworkAsk => ({
  id: 1, at: '2026-11-17T10:00:00Z', teamId: 1, team: 'payments', member: 'payments/Ana', memberName: 'Ana', host: 'api.nuget.org',
  reason: null, method: null, pathSample: null, count: 1, lastSeen: '2026-11-17T10:00:00Z', hint: null, workflow: null,
  state: 'pending', scope: null, decidedBy: null, decidedAt: null, plainHttp: false, choices: ['agent', 'team', 'deny'], ...o,
} as NetworkAsk);
const tool = (o: Partial<ToolAsk>): ToolAsk => ({
  id: 7, at: '2026-11-17T10:00:00Z', teamId: 2, team: 'alpha', member: 'alpha/Bo', memberName: 'Bo', tool: 'dotnet',
  displayName: '.NET SDK', publisher: null, wingetId: null, scope: null, reason: null, count: 1, lastSeen: '2026-11-17T10:00:00Z',
  workflow: null, state: 'pending', attempts: 0, error: null, decidedBy: null, decidedVia: null, decidedAt: null,
  canInstall: true, command: null, choices: [], ...o,
} as ToolAsk);

let netAsks: NetworkAsk[] | Error = [];
let toolAsks: ToolAsk[] | Error = [];
let calls: string[] = [];
let listeners: ((e: { event: string }) => void)[] = [];
let unsubscribed = 0;
function api(): YawbleApi {
  return {
    call: async (method: string) => {
      calls.push(method);
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams: [
          { id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 },
          { id: 2, name: 'alpha', repoPath: null, members: 1, running: 0, queued: 0 },
        ] } as any;
        case 'monitor.messages': return { end: 0, messages: [] } as any;
        case 'monitor.steps': return { end: 0, pending: false, steps: [] } as any;
        case 'network.asks': if (netAsks instanceof Error) throw netAsks; return netAsks as any;
        case 'tools.asks': if (toolAsks instanceof Error) throw toolAsks; return toolAsks as any;
        default: return [] as any;
      }
    },
    onEvent: (fn: (e: { event: string }) => void) => { listeners.push(fn); return () => { unsubscribed++; listeners = listeners.filter((l) => l !== fn); }; },
  } as unknown as YawbleApi;
}
const settle = async (n = 8) => { for (let i = 0; i < n; i++) await flushPromises(); };
const emit = (event: string) => { for (const l of [...listeners]) l({ event }); };
const mounted: VueWrapper[] = [];
let spy: ReturnType<typeof vi.spyOn<MonitorModel, 'setAsks'>>;
async function view() {
  const w = mount(MonitorView, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
  mounted.push(w);
  await settle();
  return w;
}
const lastAsks = () => spy.mock.calls.at(-1)![0].map((a) => ({ ...a }));
const theModel = () => spy.mock.contexts.at(-1) as MonitorModel;
const youFx = (m: MonitorModel) => m.effects.filter((e) => e.kind === 'you');

beforeEach(() => {
  localStorage.clear(); document.body.innerHTML = '';
  netAsks = []; toolAsks = []; calls = []; listeners = []; unsubscribed = 0;
  HTMLCanvasElement.prototype.getContext = (() => ({})) as any;
  spy = vi.spyOn(MonitorModel.prototype, 'setAsks');
});
afterEach(() => { for (const w of mounted.splice(0)) w.unmount(); spy.mockRestore(); vi.restoreAllMocks(); });

describe('the You node gets the network and tool asks (card 386)', () => {
  it('on load: setAsks with the pending network asks (one item per member and domain) and the pending tool asks', async () => {
    netAsks = [
      net({ id: 1, host: 'crl3.digicert.com', domain: 'digicert.com' }),
      net({ id: 2, host: 'ocsp.digicert.com', domain: 'digicert.com' }),
      net({ id: 3, host: 'api.nuget.org' }),
      net({ id: 4, host: 'old.example.com', state: 'denied' }),
    ];
    toolAsks = [tool({ id: 7 }), tool({ id: 8, tool: 'python', displayName: null, state: 'installing' }), tool({ id: 9, state: 'dismissed' })];
    await view();
    expect(calls).toContain('network.asks');
    expect(calls).toContain('tools.asks');
    expect(spy).toHaveBeenCalledTimes(1);
    expect(lastAsks()).toEqual([
      { key: expect.stringMatching(/^n:/), kind: 'network', team: 'payments', member: 'Ana', label: 'crl3.digicert.com, ocsp.digicert.com (2 hosts)' },
      { key: expect.stringMatching(/^n:/), kind: 'network', team: 'payments', member: 'Ana', label: 'api.nuget.org' },
      { key: 't:7', kind: 'tool', team: 'alpha', member: 'Bo', label: '.NET SDK' },
      { key: 't:8', kind: 'tool', team: 'alpha', member: 'Bo', label: 'python' },
    ]);
    const m = theModel();
    expect(m.needYouItems().length).toBe(4);
    // Those waiting when the Monitor opens are counted, not flown in.
    expect(youFx(m)).toHaveLength(0);
  });

  it('network.asksChanged reads again: a new ask is passed on and flies to the You node', async () => {
    await view();
    expect(lastAsks()).toEqual([]);
    netAsks = [net({ id: 5, host: 'registry.npmjs.org' })];
    emit('network.asksChanged');
    await settle();
    expect(spy).toHaveBeenCalledTimes(2);
    expect(lastAsks()).toEqual([{ key: expect.stringMatching(/^n:/), kind: 'network', team: 'payments', member: 'Ana', label: 'registry.npmjs.org' }]);
    const m = theModel();
    expect(m.needYouItems().length).toBe(1);
    expect(youFx(m)).toHaveLength(1);
  });

  it('tools.asksChanged reads again: an answered tool ask leaves the count', async () => {
    toolAsks = [tool({ id: 7 }), tool({ id: 8, tool: 'git' })];
    await view();
    expect(theModel().needYouItems().length).toBe(2);
    toolAsks = [tool({ id: 8, tool: 'git' })];
    emit('tools.asksChanged');
    await settle();
    expect(lastAsks().map((a) => a.key)).toEqual(['t:8']);
    expect(theModel().needYouItems().length).toBe(1);
  });

  it('other events do not read the asks', async () => {
    await view();
    const n = calls.filter((c) => c === 'network.asks').length;
    emit('bus.message');
    await settle();
    expect(calls.filter((c) => c === 'network.asks').length).toBe(n);
    expect(spy).toHaveBeenCalledTimes(1);
  });

  it('a refused read (older core, a remote view that may not read it) counts as none of that kind; the Monitor loads', async () => {
    netAsks = new Error('not allowed');
    toolAsks = [tool({ id: 7 })];
    const w = await view();
    expect(lastAsks().map((a) => a.key)).toEqual(['t:7']);
    toolAsks = new Error('unknown method');
    emit('tools.asksChanged');
    await settle();
    expect(lastAsks()).toEqual([]);
    expect(w.find('[data-test=monitor]').exists()).toBe(true);
  });

  it('polls the asks slowly (a repeated refusal changes without an event)', async () => {
    await view();
    expect(spy).toHaveBeenCalledTimes(1);
    const real = Date.now;
    vi.spyOn(Date, 'now').mockImplementation(() => real() + 6000);
    await new Promise((r) => setTimeout(r, 1100));
    await settle();
    expect(spy.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it('unmounting stops listening', async () => {
    const w = await view();
    const before = unsubscribed;
    w.unmount(); mounted.length = 0;
    expect(unsubscribed).toBeGreaterThan(before);
    expect(listeners).toHaveLength(0);
  });
});
