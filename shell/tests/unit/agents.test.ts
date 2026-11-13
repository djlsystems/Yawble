// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import AgentsDialog from '../../src/renderer/src/components/AgentsDialog.vue';
import type { DetectedCli, YawbleApi } from '../../src/shared/api';
import { isUiMethod } from '../../src/shared/api';
import { terminalPane } from '../../src/renderer/src/terminalPane';

// Settings > Agents, the detection side, against a scripted core (a fake API: no core, no CLI, no network port): a
// live agents.detect on open, one tile per CLI, install help only when not found, and Test opening the CLI in the sandbox
// terminal pane. The agentProfiles side is in agent-settings.test.ts.
function fakeApi(answers: Record<string, (params: any) => unknown>, remote = false) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...(remote ? { remote: true } : {}),
  } as YawbleApi;
  return { api, calls };
}

const claude: DetectedCli = {
  cliId: 'claude', found: true, launcherPath: '/home/u/.local/bin/claude', executablePath: '/home/u/.local/share/claude/versions/2.1.318',
  version: '2.1.318', installKind: 'native', readOnlyFolders: ['/home/u/.local/share/claude/versions'],
  configFolders: ['/home/u/.claude', '/home/u/.claude.json'], problems: [], installHelp: 'curl -fsSL https://claude.ai/install.sh | bash',
};
const grokMissing: DetectedCli = {
  cliId: 'grok', found: false, launcherPath: null, executablePath: null, version: null, installKind: 'other', readOnlyFolders: [],
  configFolders: ['/home/u/.grok'], problems: [], installHelp: 'Install xAI\'s Grok Build CLI: curl -fsSL https://x.ai/cli/install.sh | bash',
};

// The agents side of the scripted core, with nothing custom: enough for the tiles to load quietly.
const quiet = {
  'agentProfiles.list': () => [],
};

describe('agents screen: detection', () => {
  it('detects live on open and shows found / not found, path, version, kind and problems in each tile', async () => {
    const { api, calls } = fakeApi({
      ...quiet,
      'agents.detect': () => [{ ...claude, problems: ['Found at /home/u/.local/bin/claude, but its folder is not on your PATH.'] }, grokMissing],
    });
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();

    expect(calls.map((c) => c.method).filter((m) => m === 'agents.detect')).toEqual(['agents.detect']);
    const c = w.get('[data-test=agent-cli-claude]');
    expect(c.get('[data-test=cli-found]').text()).toBe('Found');
    expect(c.get('[data-test=cli-path]').text()).toBe('/home/u/.local/share/claude/versions/2.1.318');
    expect(c.get('[data-test=cli-version]').text()).toBe('2.1.318');
    expect(c.get('[data-test=cli-kind]').text()).toBe('official installer');
    expect(c.get('[data-test=cli-problems]').text()).toContain('not on your PATH');
    expect(c.find('[data-test=cli-install-help]').exists()).toBe(false);
    expect(c.find('[data-test=test-claude]').exists()).toBe(true);

    // Not found: only the install instructions, no Test.
    const g = w.get('[data-test=agent-cli-grok]');
    expect(g.get('[data-test=cli-found]').text()).toBe('Not found');
    expect(g.get('[data-test=cli-install-help]').text()).toContain('https://x.ai/cli/install.sh');
    expect(g.find('[data-test=test-grok]').exists()).toBe(false);
    expect(g.find('[data-test=cli-path]').exists()).toBe(false);
  });

  it('the Command line dialog shows what the sandbox gets for the CLI', async () => {
    const { api } = fakeApi({
      ...quiet,
      'agents.detect': () => [claude],
      'agents.commandLine': () => ({ cli: 'claude', model: { model: '', source: 'cli', label: "The CLI's default" },
        headless: { argv: ['claude', '-p', '<prompt>'], env: ['CLAUDE_CONFIG_DIR'] }, interactive: { argv: ['claude'], env: [] } }),
    });
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    await w.get('[data-test=agent-cli-claude] [data-test=tile-cmd]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=cli-readonly]').text()).toBe('/home/u/.local/share/claude/versions');
    expect(w.get('[data-test=cli-config]').text()).toContain('/home/u/.claude');
  });

  it('detects again on demand (no cache) and shows a failed detect as it is', async () => {
    let n = 0;
    const { api, calls } = fakeApi({
      ...quiet,
      'agents.detect': () => {
        n++;
        if (n === 2) throw new Error("Error invoking remote method 'core:call': Error: the core is restarting");
        return n === 1 ? [grokMissing] : [{ ...grokMissing, found: true, executablePath: '/home/u/.grok/downloads/grok-1.0.77', version: '1.0.77', installKind: 'native' }];
      },
    });
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    await w.get('[data-test=agents-redetect]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=agents-error]').text()).toBe('the core is restarting');
    await w.get('[data-test=agents-redetect]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'agents.detect')).toHaveLength(3);
    expect(w.get('[data-test=agent-cli-grok] [data-test=cli-version]').text()).toBe('1.0.77');
  });

  it('Terminal shows Starting… with a spinner (both buttons disabled) until the session has started', async () => {
    let release!: (v: unknown) => void;
    const { api } = fakeApi({
      ...quiet,
      'agents.detect': () => [claude, grokMissing],
      'agents.test': () => new Promise((r) => { release = r; }),
    });
    terminalPane.open = false;
    terminalPane.target = null;
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    await w.get('[data-test=terminal-claude]').trigger('click');
    await flushPromises();
    const term = w.get('[data-test=terminal-claude]');
    expect(term.text()).toBe('Starting…');
    expect(term.find('.btn-spinner').exists()).toBe(true);
    expect(term.attributes('disabled')).toBeDefined();
    expect(w.get('[data-test=test-claude]').attributes('disabled')).toBeDefined();
    expect(terminalPane.open).toBe(false);
    release({ channel: 'agent-test:claude', diagnostic: false, runId: 'r1' });
    await flushPromises();
    expect(w.get('[data-test=terminal-claude]').text()).toBe('Terminal');
    expect(w.get('[data-test=terminal-claude]').attributes('disabled')).toBeUndefined();
    expect(terminalPane.open).toBe(true);
    w.unmount();
  });

  it('Test always sends the diagnostic and Stop ends it; there is no diagnostic checkbox', async () => {
    const { api, calls } = fakeApi({
      ...quiet,
      'agents.detect': () => [claude, grokMissing],
      'agents.test': (p) => ({ channel: `agent-test:${p.cli}`, diagnostic: p.diagnostic }),
      'agents.stopTest': () => ({ stopped: true }),
    });
    terminalPane.open = false;
    terminalPane.target = null;
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=agents-diagnostic]').exists()).toBe(false);
    expect(w.text()).not.toContain('Test sends a diagnostic prompt');
    expect(w.find('[data-test=agents-test-note]').exists()).toBe(false);

    await w.get('[data-test=test-claude]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'agents.test')!.params).toEqual({ cli: 'claude', diagnostic: true });
    expect(terminalPane.open).toBe(true);
    expect(terminalPane.target).toMatchObject({ kind: 'channel', channel: 'agent-test:claude', title: 'Test · Claude Code · global policy' });
    expect(w.find('[data-test=agents-test-note]').exists()).toBe(true);

    const target = terminalPane.target as { stop?: () => Promise<void> };
    await target.stop!();
    expect(calls.at(-1)).toEqual({ method: 'agents.stopTest', params: { cli: 'claude' } });

    await w.get('[data-test=test-claude]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'agents.test').at(-1)!.params).toEqual({ cli: 'claude', diagnostic: true });
  });

  it('Terminal opens the built-in with no prompt, in the terminal pane titled for it; only on a found CLI', async () => {
    const { api, calls } = fakeApi({
      ...quiet,
      'agents.detect': () => [claude, grokMissing],
      'agents.test': (p) => ({ channel: `agent-test:${p.cli}`, diagnostic: p.diagnostic }),
      'agents.stopTest': () => ({ stopped: true }),
    });
    terminalPane.open = false;
    terminalPane.target = null;
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=agent-cli-grok] [data-test=terminal-grok]').exists()).toBe(false);

    await w.get('[data-test=agent-cli-claude] [data-test=terminal-claude]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'agents.test')!.params).toEqual({ cli: 'claude', diagnostic: false });
    expect(terminalPane.open).toBe(true);
    expect(terminalPane.target).toMatchObject({ kind: 'channel', channel: 'agent-test:claude', title: 'Terminal · Claude Code · global policy' });
  });

  it('a custom agent has Terminal and Test, each sending its name as agent; a not-found base has neither', async () => {
    const mine = { id: 7, name: 'Claude fast', baseCli: 'claude', custom: true, defaultModel: 'haiku', argsBefore: ['--x'], argsAfter: [], removedArgs: [] };
    const grokMine = { id: 8, name: 'Grok quiet', baseCli: 'grok', custom: true, defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] };
    const builtin = (name: string) => ({ id: 0, name, baseCli: name, custom: false, defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] });
    const { api, calls } = fakeApi({
      'agents.detect': () => [claude, grokMissing],
      'agentProfiles.list': () => [builtin('claude'), builtin('grok'), mine, grokMine],
      'agents.versionStatus': (p) => ({ cli: p.cli, detected: null, lastRun: null, changed: false }),
      'agents.test': (p) => ({ channel: `agent-test:${p.cli}`, diagnostic: p.diagnostic }),
      'agents.stopTest': () => ({ stopped: true }),
    });
    terminalPane.open = false;
    terminalPane.target = null;
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    const t = w.get('[data-test="agent-custom-Claude fast"]');
    const g = w.get('[data-test="agent-custom-Grok quiet"]');
    expect(g.find('[data-test=terminal-grok]').exists()).toBe(false);
    expect(g.find('[data-test=test-grok]').exists()).toBe(false);

    await t.get('[data-test=terminal-claude]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'agents.test').at(-1)!.params).toEqual({ cli: 'claude', agent: 'Claude fast', diagnostic: false });
    expect(terminalPane.target).toMatchObject({ channel: 'agent-test:claude', title: 'Terminal · Claude fast · global policy' });

    // One session per base CLI: starting the Test stops the running Terminal first.
    await t.get('[data-test=test-claude]').trigger('click');
    await flushPromises();
    const methods = calls.map((c) => c.method);
    expect(methods.slice(-2)).toEqual(['agents.stopTest', 'agents.test']);
    expect(calls.at(-1)!.params).toEqual({ cli: 'claude', agent: 'Claude fast', diagnostic: true });
    expect(terminalPane.target).toMatchObject({ title: 'Test · Claude fast · global policy' });
  });

  it('shows the core refusing a Test (no launcher on this machine)', async () => {
    const { api } = fakeApi({
      ...quiet,
      'agents.detect': () => [claude],
      'agents.test': () => { throw new Error('RpcError: bubblewrap is not installed'); },
    });
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    await w.get('[data-test=test-claude]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=agents-error]').text()).toBe('bubblewrap is not installed');
    expect(w.find('[data-test=agents-test-note]').exists()).toBe(false);
  });

  it('a cloud viewer sees detection only: no agentProfiles calls, no Model / Edit / Duplicate / Delete / Update / Command line', async () => {
    const { api, calls } = fakeApi({ 'agents.detect': () => [claude, grokMissing] }, true);
    const w = mount(AgentsDialog, { props: { api } });
    await flushPromises();
    expect(calls.map((c) => c.method)).toEqual(['agents.detect']);
    expect(w.get('[data-test=agent-cli-claude] [data-test=cli-found]').text()).toBe('Found');
    // Terminal and Test are sessions on this computer: the window's only (agents.test is refused over the Cloud Link).
    for (const b of ['terminal-claude', 'test-claude', 'tile-cmd', 'tile-model', 'tile-update', 'tile-duplicate', 'tile-edit', 'tile-delete'])
      expect(w.find(`[data-test=${b}]`).exists()).toBe(false);
    expect(w.get('[data-test=agents-remote-note]').text()).toContain('set on the computer itself');
  });

  it('the renderer may call agents.* through the bridge', () => {
    expect(isUiMethod('agents.detect')).toBe(true);
    expect(isUiMethod('agents.test')).toBe(true);
  });
});

describe('agents screen: after Install', () => {
  it('updates by itself until the CLI is found signed in, and again when the window comes back', async () => {
    const { vi } = await import('vitest');
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let stage: 'missing' | 'found' | 'signed' = 'missing';
      const grok = () => (stage === 'missing' ? grokMissing
        : { ...grokMissing, found: true, executablePath: '/home/u/.grok/bin/grok', version: '1.0.0', signIn: { status: stage === 'signed' ? 'signed-in' : 'not-signed-in' } }) as DetectedCli;
      const { api, calls } = fakeApi({ ...quiet, 'agents.detect': () => [claude, grok()], 'agents.install': () => ({ opened: true }) });
      const w = mount(AgentsDialog, { props: { api } });
      await flushPromises();
      await w.get('[data-test=agent-cli-grok] [data-test=tile-install-btn]').trigger('click');
      await flushPromises();
      expect(w.text()).toContain('this list updates by itself');

      stage = 'found';
      await vi.advanceTimersByTimeAsync(4100);
      await flushPromises();
      expect(w.get('[data-test=agent-cli-grok] [data-test=cli-found]').text()).toBe('Found');
      expect(w.text()).toContain('Sign in to it in the terminal');

      stage = 'signed';
      await vi.advanceTimersByTimeAsync(4100);
      await flushPromises();
      expect(w.text()).toContain('is installed and signed in');
      const detects = calls.filter((c) => c.method === 'agents.detect').length;
      await vi.advanceTimersByTimeAsync(20_000);
      expect(calls.filter((c) => c.method === 'agents.detect').length).toBe(detects); // stopped once signed in

      window.dispatchEvent(new Event('focus'));
      await flushPromises();
      expect(calls.filter((c) => c.method === 'agents.detect').length).toBe(detects + 1);
      w.unmount();
    } finally {
      vi.useRealTimers();
    }
  });
});
