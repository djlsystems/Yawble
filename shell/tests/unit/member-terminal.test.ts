// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { YawbleApi } from '../../src/shared/api';

// Every entry that opens a MEMBER's terminal opens the team page with that member's pane on Terminal
// (focusMemberTerminal(team, member), memberPaneView.ts), never the right-hand terminal pane, which keeps the channel
// terminals (Settings > Agents sessions). A fake focusMemberTerminal and a scripted core only.

// The Monitor's wall is a canvas scene: a fake one hands the test its pick callback.
const scenes: { onPick?: (k: string | null) => void }[] = [];
vi.mock('../../src/renderer/src/monitor/scene', async (orig) => {
  const real = await orig<typeof import('../../src/renderer/src/monitor/scene')>();
  class FakeScene {
    model: unknown; onHover?: unknown; onPick?: (k: string | null) => void; onOpenDeliverable?: unknown; effects: unknown[] = [];
    constructor() { scenes.push(this); }
    setPinned() {} setLayout() {} setTeams() {} setPicked() {} destroy() {} fit() {}
  }
  return { ...real, MonitorScene: FakeScene };
});

const { default: App } = await import('../../src/renderer/src/App.vue');
const { default: MonitorView } = await import('../../src/renderer/src/monitor/MonitorView.vue');
const { default: TerminalPane } = await import('../../src/renderer/src/components/TerminalPane.vue');
const mt = await import('../../src/renderer/src/memberTerminal');
const tp = await import('../../src/renderer/src/terminalPane');
const { createRemoteApi } = await import('../../src/renderer/src/remoteApi');

const teams = [
  { id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 },
  { id: 2, name: 'alpha', repoPath: null, members: 2, running: 0, queued: 0 },
];
const card = (id: number, name: string, team: string) => ({
  member: { id, name, role: 'Developer', cli: 'claude', team, state: 'idle' }, queuedInstructions: 0, statusLine: null,
});
const cardsOf: Record<number, unknown[]> = { 1: [card(3, 'Ben', 'payments')], 2: [card(4, 'Ines', 'alpha'), card(5, 'Kofi', 'alpha')] };

function api(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, p?: any) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'teams.agents': return (cardsOf[p.teamId] ?? []) as any;
        case 'monitor.messages': return { end: 0, messages: [] } as any;
        case 'monitor.steps': return { end: 0, pending: false, steps: [] } as any;
        // The real member pane on Terminal (the memberPaneView test) attaches its terminal and reads its banner:
        // TerminalScreen atob()s the replay (empty is valid base64) and TerminalApp reads banner.loosened.length.
        // Without these the default [] reply raises 2 unhandled rejections.
        case 'terminal.attach': return { channel: p.channel, replay: '', end: 0, session: null } as any;
        case 'terminal.banner': return {
          memberId: p.memberId, channel: `member:${p.memberId}`, title: `m${p.memberId}`, state: 'idle', runId: null, queuePosition: null, queueLength: 0,
          lastRunState: null, lastRunReason: null, details: [], loosened: [], interactive: false,
        } as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

const focused: [string, string][] = [];
const settle = async (n = 6) => { for (let i = 0; i < n; i++) await flushPromises(); };

beforeEach(() => {
  localStorage.clear(); sessionStorage.clear(); document.body.innerHTML = '';
  focused.length = 0; scenes.length = 0;
  mt.setMemberTerminalFocus((team, member) => { focused.push([team, member]); });
  tp.terminalPane.open = false; tp.terminalPane.target = null;
});
afterEach(() => { mt.setMemberTerminalFocus(null); });

async function app() {
  const w = mount(App, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
  await settle();
  return w;
}

describe('member terminals open on the team page', () => {
  it("the Monitor: a picked member's Terminal opens its team page at that member, the right-hand pane stays shut", async () => {
    HTMLCanvasElement.prototype.getContext = (() => ({})) as any;
    const w = await app();
    await w.get('[data-test=nav-monitor]').trigger('click');
    await settle();
    expect(scenes).toHaveLength(1);
    scenes[0]!.onPick!('m:alpha/ines');
    await settle(2);
    const picks = w.findComponent(MonitorView);
    expect(picks.find('[data-test=monitor-open-terminal]').exists()).toBe(true);
    await picks.get('[data-test=monitor-open-terminal]').trigger('click');
    await settle();
    expect(w.find('[data-test=monitor-activity]').exists()).toBe(false); // left the Monitor
    expect(w.findComponent({ name: 'TeamView' }).props('team')).toMatchObject({ id: 2, name: 'alpha' });
    expect(focused.at(-1)).toEqual(['alpha', 'Ines']);
    expect(tp.terminalPane.open).toBe(false);
    expect(w.find('[data-test=terminal-pane]').exists()).toBe(false);
    w.unmount();
  });

  it('the Monitor offers Terminal for a picked member only', async () => {
    HTMLCanvasElement.prototype.getContext = (() => ({})) as any;
    const w = mount(MonitorView, { props: { api: api(), pollMs: 60_000 } });
    await settle();
    scenes.at(-1)!.onPick!('t:alpha');
    await settle(2);
    expect(w.find('[data-test=monitor-open-team]').exists()).toBe(true);
    expect(w.find('[data-test=monitor-open-terminal]').exists()).toBe(false);
    scenes.at(-1)!.onPick!('m:payments/ben');
    await settle(2);
    await w.get('[data-test=monitor-open-terminal]').trigger('click');
    expect(w.emitted('openTerminal')).toEqual([[1, 3]]);
    w.unmount();
  });

  it('there is no View > Terminal and Ctrl+` does nothing: a member terminal opens from its pane, the Monitor or >_', async () => {
    const w = await app();
    await mt.openMemberTerminal(2, 5);
    await settle();
    await w.get('[data-test=nav-monitor]').trigger('click');
    focused.length = 0;
    window.dispatchEvent(new KeyboardEvent('keydown', { key: '`', ctrlKey: true }));
    await settle();
    expect(focused).toEqual([]);
    expect(tp.terminalPane.open).toBe(false);
    w.unmount();
  });

  it("without a fake, memberPaneView's focusMemberTerminal gets the names: that member's pane is on Terminal", async () => {
    const pv = await import('../../src/renderer/src/memberPaneView');
    pv.resetMemberViews();
    mt.setMemberTerminalFocus(null);
    const w = await app();
    expect(await mt.openMemberTerminal(2, 5)).toBe(true);
    await settle();
    expect(pv.getMemberView('alpha', 'Kofi')).toBe('terminal');
    expect(pv.getMemberView('2', '5')).toBe('report'); // not keyed by ids
    expect(focused).toEqual([]);
    w.unmount();
    pv.resetMemberViews();
  });

  it('an older showMemberTerminal call (an agent card\'s >_) looks the member\'s team up and opens it there', async () => {
    const w = await app();
    tp.showMemberTerminal(3, 'Ben · payments');
    await settle();
    expect(focused.at(-1)).toEqual(['payments', 'Ben']);
    expect(w.findComponent({ name: 'TeamView' }).props('team')).toMatchObject({ id: 1 });
    expect(tp.terminalPane.open).toBe(false);
    expect(tp.terminalPane.target).toBeNull();
    w.unmount();
  });

  it('a member on no team: nothing opens, no error', async () => {
    const w = await app();
    expect(await mt.openMemberTerminalById(99)).toBe(false);
    expect(await mt.openMemberTerminal(2, 3)).toBe(false); // Ben is on payments, not alpha
    await settle();
    expect(focused).toEqual([]);
    expect(tp.terminalPane.open).toBe(false);
    w.unmount();
  });

  it("the remote view's openTerminal opens the team page in the same page, no browser window", async () => {
    class FakeSocket { static OPEN = 1; readyState = 0; onopen?: () => void; onclose?: () => void; onmessage?: () => void; send() {} close() {} constructor(_u: string) {} }
    vi.stubGlobal('WebSocket', FakeSocket);
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    const w = await app();
    const remote = createRemoteApi('ws://example.invalid/rpc');
    await remote.openTerminal(4);
    await settle();
    expect(open).not.toHaveBeenCalled();
    expect(focused.at(-1)).toEqual(['alpha', 'Ines']);
    expect(w.findComponent({ name: 'TeamView' }).props('team')).toMatchObject({ id: 2 });
    open.mockRestore();
    vi.unstubAllGlobals();
    w.unmount();
  });
});

describe('the right-hand pane is for channel terminals only', () => {
  it('an Agents test session opens there; the empty pane points to the team page for an agent', async () => {
    tp.showChannelTerminal('agent-test:claude', 'Test · Claude Code · global policy');
    expect(tp.terminalPane.open).toBe(true);
    expect(tp.terminalPane.target).toMatchObject({ kind: 'channel', channel: 'agent-test:claude' });
    tp.terminalPane.target = null;
    const w = mount(TerminalPane, { global: { stubs: { TerminalScreen: true } } });
    expect(w.get('[data-test=terminal-pane-empty]').text()).toContain('team page');
    w.unmount();
  });

  it('the pane has no toggle: it opens when a channel is shown and closeTerminalPane hides it', () => {
    expect('toggleTerminalPane' in tp).toBe(false);
    tp.showChannelTerminal('agent-test:grok', 'Terminal · Grok · global policy');
    expect(tp.terminalPane.open).toBe(true);
    tp.closeTerminalPane();
    expect(tp.terminalPane.open).toBe(false);
  });
});
