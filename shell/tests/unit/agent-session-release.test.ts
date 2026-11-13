// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import AgentsDialog from '../../src/renderer/src/components/AgentsDialog.vue';
import TerminalPane from '../../src/renderer/src/components/TerminalPane.vue';
import type { AgentTestSession, DetectedCli, YawbleApi } from '../../src/shared/api';
import { showChannelTerminal, showMemberTerminal, terminalPane, toggleTerminalPane } from '../../src/renderer/src/terminalPane';

// An agent's Terminal / Test session is released when the person is done:
// ✕ on the pane stops it, its tile shows it while it runs (Open / Stop), driven by agents.testSessions and the
// agents.testChanged push. A scripted core only (a fake API: no core, no CLI, no network port).
type Listener = (e: { event: string; data: unknown }) => void;

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const listeners: Listener[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: (l: Listener) => { listeners.push(l); return () => { listeners.splice(listeners.indexOf(l), 1); }; },
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
  const push = (event: string, data: unknown) => { for (const l of [...listeners]) l({ event, data }); };
  return { api, calls, push, listeners };
}

const claude: DetectedCli = {
  cliId: 'claude', found: true, launcherPath: '/home/u/.local/bin/claude', executablePath: '/home/u/.local/share/claude/versions/2.1.318',
  version: '2.1.318', installKind: 'native', readOnlyFolders: [], configFolders: ['/home/u/.claude'], problems: [], installHelp: 'install it',
};
const grok: DetectedCli = { ...claude, cliId: 'grok', launcherPath: '/usr/bin/grok', executablePath: '/opt/grok/grok', version: '1.0.0' };
const builtin = (name: string) => ({ id: 0, name, baseCli: name, custom: false, defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] });
const mine = { id: 7, name: 'Claude fast', baseCli: 'claude', custom: true, defaultModel: 'haiku', argsBefore: [], argsAfter: [], removedArgs: [] };

const stubs = { global: { stubs: { TerminalScreen: true, TerminalApp: true, TerminalView: true } } };
enableAutoUnmount(afterEach);

beforeEach(() => {
  terminalPane.open = false;
  terminalPane.target = null;
});

describe('terminal pane: ✕ releases an agent session', () => {
  it('✕ on an agent-test target calls its stop, then closes the pane', async () => {
    const stop = vi.fn(async () => {});
    showChannelTerminal('agent-test:claude', 'Terminal · Claude Code · global policy', stop);
    const w = mount(TerminalPane, stubs);
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stop).toHaveBeenCalledTimes(1);
    expect(terminalPane.open).toBe(false);
  });

  it('✕ still closes the pane when the stop fails', async () => {
    const stop = vi.fn(async () => { throw new Error('gone'); });
    showChannelTerminal('agent-test:grok', 'Test · Grok · global policy', stop);
    const w = mount(TerminalPane, stubs);
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stop).toHaveBeenCalledTimes(1);
    expect(terminalPane.open).toBe(false);
  });

  it('✕ on a member terminal, or another channel, does not stop anything: it only hides the pane', async () => {
    showMemberTerminal(4, 'ines');
    const w = mount(TerminalPane, stubs);
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(terminalPane.open).toBe(false);

    const stop = vi.fn();
    showChannelTerminal('host:shell', 'Shell', stop);
    await flushPromises();
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stop).not.toHaveBeenCalled();
    expect(terminalPane.open).toBe(false);
  });

  it('Ctrl+` (toggle) only hides an agent session, and replacing the target does not stop it', () => {
    const stop = vi.fn();
    showChannelTerminal('agent-test:claude', 'Terminal · Claude Code · global policy', stop);
    toggleTerminalPane();
    expect(terminalPane.open).toBe(false);
    toggleTerminalPane();
    showChannelTerminal('agent-test:grok', 'Terminal · Grok · global policy', vi.fn());
    expect(stop).not.toHaveBeenCalled();
    expect(terminalPane.target).toMatchObject({ channel: 'agent-test:grok' });
  });
});

describe('agents screen: a running session on its tile', () => {
  function rig(initial: AgentTestSession[]) {
    let live = initial;
    const fake = fakeApi({
      'agents.detect': () => [claude, grok],
      'agentProfiles.list': () => [builtin('claude'), builtin('grok'), mine],
      'agents.versionStatus': (p) => ({ cli: p.cli, detected: null, lastRun: null, changed: false }),
      'agents.testSessions': () => live,
      'agents.test': (p) => ({ channel: `agent-test:${p.cli}`, diagnostic: p.diagnostic }),
      'agents.stopTest': () => ({ stopped: true }),
    });
    return { ...fake, set: (s: AgentTestSession[]) => { live = s; } };
  }
  const session = (cli: string, agent: string, kind: 'terminal' | 'test'): AgentTestSession => ({ cli, agent, kind, startedAt: '2026-11-12T09:00:00Z' });

  it('shows Running · Terminal|Test with Open and Stop from agents.testSessions, on the base tile and the matching custom tile', async () => {
    const { api } = rig([session('claude', 'Claude fast', 'terminal'), session('grok', 'grok', 'test')]);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    const base = w.get('[data-test=agent-cli-claude]');
    const custom = w.get('[data-test="agent-custom-Claude fast"]');
    const g = w.get('[data-test=agent-cli-grok]');
    expect(base.get('[data-test=tile-running-chip]').text()).toBe('Running · Terminal');
    expect(custom.get('[data-test=tile-running-chip]').text()).toBe('Running · Terminal');
    expect(g.get('[data-test=tile-running-chip]').text()).toBe('Running · Test');
    expect(base.find('[data-test=tile-running-open]').exists()).toBe(true);
    expect(base.find('[data-test=tile-running-stop]').exists()).toBe(true);
  });

  it('a session started as the built-in shows on the built-in tile only, not on a custom agent of that CLI', async () => {
    const { api } = rig([session('claude', 'claude', 'test')]);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=agent-cli-claude] [data-test=tile-running]').exists()).toBe(true);
    expect(w.find('[data-test="agent-custom-Claude fast"] [data-test=tile-running]').exists()).toBe(false);
  });

  it('updates on agents.testChanged: the chip appears when one starts and goes when it ends', async () => {
    const { api, push, set } = rig([]);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=tile-running]').exists()).toBe(false);

    set([session('claude', 'claude', 'terminal')]);
    push('agents.testChanged', { cli: 'claude', agent: 'claude', kind: 'terminal', live: true });
    await flushPromises();
    expect(w.get('[data-test=agent-cli-claude] [data-test=tile-running-chip]').text()).toBe('Running · Terminal');

    set([]);
    push('agents.testChanged', { cli: 'claude', agent: 'claude', kind: 'terminal', live: false });
    await flushPromises();
    expect(w.find('[data-test=tile-running]').exists()).toBe(false);
  });

  it('Open re-shows the pane on that channel with Stop wired; Stop on the tile ends it', async () => {
    const { api, calls } = rig([session('claude', 'Claude fast', 'test')]);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    await w.get('[data-test="agent-custom-Claude fast"] [data-test=tile-running-open]').trigger('click');
    expect(terminalPane.open).toBe(true);
    expect(terminalPane.target).toMatchObject({ kind: 'channel', channel: 'agent-test:claude', title: 'Test · Claude fast · global policy' });
    await (terminalPane.target as { stop?: () => Promise<void> }).stop!();
    expect(calls.at(-1)).toEqual({ method: 'agents.stopTest', params: { cli: 'claude' } });

    calls.length = 0;
    await w.get('[data-test=agent-cli-claude] [data-test=tile-running-stop]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'agents.stopTest', params: { cli: 'claude' } }]);
  });

  it('starting Terminal/Test while one runs for that CLI stops it first (even one started before the dialog opened)', async () => {
    const { api, calls } = rig([session('claude', 'claude', 'terminal')]);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    calls.length = 0;
    await w.get('[data-test=agent-cli-claude] [data-test=test-claude]').trigger('click');
    await flushPromises();
    expect(calls.map((c) => c.method)).toEqual(['agents.stopTest', 'agents.test']);

    // Another CLI's session keeps running.
    calls.length = 0;
    await w.get('[data-test=agent-cli-grok] [data-test=terminal-grok]').trigger('click');
    await flushPromises();
    expect(calls.map((c) => c.method)).toEqual(['agents.test']);
  });

  it('stops listening when the screen closes', async () => {
    const { api, listeners } = rig([]);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    expect(listeners).toHaveLength(1);
    w.unmount();
    expect(listeners).toHaveLength(0);
  });
});

describe('review fixes: a stop names its run, ended sessions are left alone, replies are sequenced', () => {
  const live = (over: Partial<Record<string, unknown>> = {}) =>
    ({ kind: 'interactive', runId: null, startedAt: '2026-11-12T09:00:00Z', inputEnabled: true, command: null, ended: false, exitCode: null, endReason: null, ...over });

  it('✕ on an agent session that ended after the pane saw it live does not call stop; it only closes', async () => {
    const stop = vi.fn(async () => {});
    showChannelTerminal('agent-test:claude', 'Terminal · Claude Code · global policy', stop);
    const w = mount(TerminalPane, stubs);
    const screen = w.findComponent({ name: 'TerminalScreen' });
    screen.vm.$emit('session', live());
    screen.vm.$emit('session', live({ ended: true, inputEnabled: false, endReason: 'ended' }));
    await flushPromises();
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stop).not.toHaveBeenCalled();
    expect(terminalPane.open).toBe(false);
  });

  it('✕ while the sandbox is still starting (only the previous, ended run shown) still sends the stop', async () => {
    const stop = vi.fn(async () => {});
    showChannelTerminal('agent-test:claude', 'Test · Claude Code · global policy', stop);
    const w = mount(TerminalPane, stubs);
    w.findComponent({ name: 'TerminalScreen' }).vm.$emit('session', live({ ended: true })); // the last run, never seen live here
    await flushPromises();
    expect(w.get('[data-test=terminal-pane-stop]').attributes('disabled')).toBeUndefined(); // Stop works during start-up too
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stop).toHaveBeenCalledTimes(1);
  });

  function rig(sessions: () => AgentTestSession[]) {
    return fakeApi({
      'agents.detect': () => [claude, grok],
      'agentProfiles.list': () => [builtin('claude'), builtin('grok')],
      'agents.versionStatus': (p) => ({ cli: p.cli, detected: null, lastRun: null, changed: false }),
      'agents.testSessions': () => sessions(),
      'agents.test': (p) => ({ channel: `agent-test:${p.cli}`, diagnostic: p.diagnostic, runId: `run-${p.cli}-1` }),
      'agents.stopTest': () => ({ stopped: true }),
    });
  }

  it('the pane opened by Terminal/Test and by Open stops only its own run (agents.stopTest with its runId)', async () => {
    const { api, calls } = rig(() => [{ cli: 'grok', agent: 'grok', kind: 'test', startedAt: '2026-11-12T09:00:00Z', runId: 'run-grok-0' }]);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    await w.get('[data-test=agent-cli-claude] [data-test=terminal-claude]').trigger('click');
    await flushPromises();
    await (terminalPane.target as { stop?: () => Promise<void> }).stop!();
    expect(calls.at(-1)).toEqual({ method: 'agents.stopTest', params: { cli: 'claude', runId: 'run-claude-1' } });

    await w.get('[data-test=agent-cli-grok] [data-test=tile-running-open]').trigger('click');
    await (terminalPane.target as { stop?: () => Promise<void> }).stop!();
    expect(calls.at(-1)).toEqual({ method: 'agents.stopTest', params: { cli: 'grok', runId: 'run-grok-0' } });
    await w.get('[data-test=agent-cli-grok] [data-test=tile-running-stop]').trigger('click');
    await flushPromises();
    expect(calls.at(-1)).toEqual({ method: 'agents.stopTest', params: { cli: 'grok', runId: 'run-grok-0' } });
  });

  it('a stale agents.testSessions reply (answered after a newer one) is ignored', async () => {
    const replies: ((v: AgentTestSession[]) => void)[] = [];
    const { api, push } = fakeApi({
      'agents.detect': () => [claude],
      'agentProfiles.list': () => [builtin('claude')],
      'agents.versionStatus': (p) => ({ cli: p.cli, detected: null, lastRun: null, changed: false }),
      'agents.testSessions': () => new Promise<AgentTestSession[]>((r) => replies.push(r)),
    });
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    push('agents.testChanged', { cli: 'claude', live: false });
    await flushPromises();
    expect(replies).toHaveLength(2);
    replies[1]!([]); // the newer answer: nothing runs
    await flushPromises();
    replies[0]!([{ cli: 'claude', agent: 'claude', kind: 'terminal', startedAt: '2026-11-12T09:00:00Z', runId: 'old' }]); // the older one, late
    await flushPromises();
    expect(w.find('[data-test=tile-running]').exists()).toBe(false);
  });

  it('a session that ends on its own clears the "runs in the terminal pane" note', async () => {
    let running: AgentTestSession[] = [];
    const { api, push } = rig(() => running);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    await w.get('[data-test=agent-cli-claude] [data-test=test-claude]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=agents-test-note]').exists()).toBe(true);

    // Another CLI's end, or an older run's, leaves it.
    push('agents.testChanged', { cli: 'grok', runId: 'x', live: false });
    push('agents.testChanged', { cli: 'claude', runId: 'run-claude-0', live: false });
    await flushPromises();
    expect(w.find('[data-test=agents-test-note]').exists()).toBe(true);

    running = [];
    push('agents.testChanged', { cli: 'claude', agent: 'claude', kind: 'test', runId: 'run-claude-1', live: false });
    await flushPromises();
    expect(w.find('[data-test=agents-test-note]').exists()).toBe(false);
  });
});

describe('terminal pane: ✕ releases a member\'s interactive session', () => {
  const banner = (over: Record<string, unknown> = {}) =>
    ({ memberId: 4, channel: 'member:4', title: 't', details: [], loosened: [], state: 'running', queuePosition: null, queueLength: 0, runId: null,
      interactive: true, lastRunState: null, lastRunReason: null, ...over });
  function rig(initial: Record<string, unknown>, stopImpl: () => unknown = () => ({ stopped: true })) {
    let current = initial;
    const fake = fakeApi({ 'terminal.banner': () => current, 'terminal.stop': stopImpl });
    const w = mount(TerminalPane, { props: { api: fake.api }, ...stubs });
    const seen = (b: Record<string, unknown>) => { current = b; w.findComponent({ name: 'TerminalApp' }).vm.$emit('banner', b); };
    return { ...fake, w, seen, set: (b: Record<string, unknown>) => { current = b; } };
  }
  const stops = (calls: { method: string }[]) => calls.filter((c) => c.method === 'terminal.stop');

  it('✕ on a live interactive session stops that member, then closes; the tooltip says so', async () => {
    showMemberTerminal(4, 'ines · alpha');
    const { w, calls, seen } = rig(banner());
    seen(banner());
    await flushPromises();
    expect(w.get('[data-test=terminal-pane-close]').attributes('title')).toBe('End the session and close — queued work for ines resumes');
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stops(calls)).toEqual([{ method: 'terminal.stop', params: { memberId: 4 } }]);
    expect(terminalPane.open).toBe(false);
  });

  it('✕ on a session still waiting for a slot withdraws it (same stop)', async () => {
    showMemberTerminal(4, 'ines · alpha');
    const waiting = banner({ state: 'queued', queuePosition: 2, queueLength: 3 });
    const { w, calls, seen } = rig(waiting);
    seen(waiting);
    await flushPromises();
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stops(calls)).toHaveLength(1);
    expect(terminalPane.open).toBe(false);
  });

  it('✕ while watching a headless run does not stop it, and says Close', async () => {
    showMemberTerminal(4, 'ines · alpha');
    const headless = banner({ interactive: false, runId: 9 });
    const { w, calls, seen } = rig(headless);
    seen(headless);
    await flushPromises();
    expect(w.get('[data-test=terminal-pane-close]').attributes('title')).toBe('Close');
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stops(calls)).toHaveLength(0);
    expect(terminalPane.open).toBe(false);
  });

  it('a stale interactive banner never stops a headless run that began since (fresh check)', async () => {
    showMemberTerminal(4, 'ines · alpha');
    const { w, calls, seen, set } = rig(banner());
    seen(banner());
    set(banner({ interactive: false, runId: 9 }));
    await w.get('[data-test=terminal-pane-close]').trigger('click');
    await flushPromises();
    expect(stops(calls)).toHaveLength(0);
    expect(terminalPane.open).toBe(false);
  });

  it('is held while stopping (no double stop); a failed stop is reported and the pane still closes', async () => {
    showMemberTerminal(4, 'ines · alpha');
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const { w, calls, seen } = rig(banner(), () => gate.then(() => { throw new Error('boom'); }));
    seen(banner());
    await flushPromises();
    const close = w.get('[data-test=terminal-pane-close]');
    await close.trigger('click');
    await flushPromises();
    expect(close.attributes('aria-busy')).toBe('true');
    expect(close.attributes('disabled')).toBeDefined();
    await close.trigger('click');
    release();
    await flushPromises();
    expect(stops(calls)).toHaveLength(1);
    expect(terminalPane.open).toBe(false);
    expect(terminalPane.stopError).toBe('Could not end the session: boom');
    terminalPane.stopError = null;
  });

  it('Ctrl+` / toggle never stops, and opening another terminal does not end the session', async () => {
    showMemberTerminal(4, 'ines · alpha');
    const { calls, seen } = rig(banner());
    seen(banner());
    toggleTerminalPane();
    toggleTerminalPane();
    showMemberTerminal(5, 'ben · alpha');
    showChannelTerminal('host:shell', 'Shell');
    await flushPromises();
    expect(stops(calls)).toHaveLength(0);
  });
});
