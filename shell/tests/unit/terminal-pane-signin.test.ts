// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import TerminalPane from '../../src/renderer/src/components/TerminalPane.vue';
import type { DetectedCli, YawbleApi } from '../../src/shared/api';
import { showChannelTerminal, terminalPane } from '../../src/renderer/src/terminalPane';

// B128 (card 577): Grok's Terminal pane says "Not signed in" (with the detail) while the Settings > Agents tile's sign-in
// status (agents.detect, DetectedCli.signIn) reads not signed in, and hides it when it reads signed in. A fake API only.
function fakeApi(detect: () => DetectedCli[] | Promise<DetectedCli[]>) {
  const calls: string[] = [];
  const api = {
    call: async (method: string) => {
      calls.push(method);
      if (method === 'agents.detect') return detect();
      throw new Error(`unexpected ${method}`);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  return { api, calls };
}

const base: DetectedCli = {
  cliId: 'grok', found: true, launcherPath: '/usr/bin/grok', executablePath: '/opt/grok/grok', version: '1.0.0',
  installKind: 'native', readOnlyFolders: [], configFolders: [], problems: [], installHelp: 'install it',
};
const grok = (status: string, detail: string | null): DetectedCli => ({ ...base, signIn: { status, detail } });

const stubs = { global: { stubs: { TerminalScreen: true, TerminalApp: true, TerminalView: true } } };
enableAutoUnmount(afterEach);

beforeEach(() => { terminalPane.open = false; terminalPane.target = null; });
afterEach(() => { vi.useRealTimers(); });

describe('terminal pane: Grok not signed in', () => {
  it('shows the message and the detail when Grok is not signed in', async () => {
    const { api } = fakeApi(() => [grok('not-signed-in', 'No token in ~/.grok/auth.json.')]);
    showChannelTerminal('agent-test:grok', 'Terminal · Grok Build (xAI) · global policy');
    const w = mount(TerminalPane, { props: { api }, ...stubs });
    await flushPromises();
    const line = w.get('[data-test=terminal-pane-signin]');
    expect(line.text()).toContain('Not signed in: run grok in your own terminal and sign in');
    expect(line.get('code').text()).toBe('grok');
    expect(w.get('[data-test=terminal-pane-signin-detail]').text()).toBe('No token in ~/.grok/auth.json.');
  });

  it('drops the heading and "then reopen this dialog" from the core\'s detail, keeping the full stop', async () => {
    const HEADING = 'Not signed in: run `grok` in your own terminal and sign in';
    const cases: [string, string][] = [
      [`${HEADING}. /home/alice/.grok/auth.json is missing; then reopen this dialog.`, '/home/alice/.grok/auth.json is missing.'],
      [`${HEADING}. auth.json holds no sign-in, then reopen this dialog.`, 'auth.json holds no sign-in.'],
      ["The app's last Grok request was refused.", "The app's last Grok request was refused."],
    ];
    for (const [detail, shown] of cases) {
      const { api } = fakeApi(() => [grok('not-signed-in', detail)]);
      showChannelTerminal('agent-test:grok', 'Terminal · Grok Build (xAI) · global policy');
      const w = mount(TerminalPane, { props: { api }, ...stubs });
      await flushPromises();
      expect(w.get('[data-test=terminal-pane-signin-detail]').text()).toBe(shown);
      expect(w.get('[data-test=terminal-pane-signin]').text()).not.toMatch(/reopen this dialog/);
      w.unmount();
    }
  });

  it('shows no detail line when the detail is just the heading', async () => {
    for (const detail of ['Not signed in: run `grok` in your own terminal and sign in.', 'Not signed in: run `grok` in your own terminal and sign in']) {
      const { api } = fakeApi(() => [grok('not-signed-in', detail)]);
      showChannelTerminal('agent-test:grok', 'Terminal · Grok Build (xAI) · global policy');
      const w = mount(TerminalPane, { props: { api }, ...stubs });
      await flushPromises();
      expect(w.get('[data-test=terminal-pane-signin]').text()).toContain('Not signed in: run grok in your own terminal and sign in');
      expect(w.find('[data-test=terminal-pane-signin-detail]').exists()).toBe(false);
      w.unmount();
    }
  });

  it('hides it when Grok is signed in', async () => {
    const { api } = fakeApi(() => [grok('signed-in', 'Signed in as you@example.com.')]);
    showChannelTerminal('agent-test:grok', 'Terminal · Grok Build (xAI) · global policy');
    const w = mount(TerminalPane, { props: { api }, ...stubs });
    await flushPromises();
    expect(w.find('[data-test=terminal-pane-signin]').exists()).toBe(false);
  });

  it('goes away by itself once the status reads signed in', async () => {
    vi.useFakeTimers();
    let status = 'not-signed-in';
    const { api } = fakeApi(() => [grok(status, 'Not signed in.')]);
    showChannelTerminal('agent-test:grok', 'Terminal · Grok Build (xAI) · global policy');
    const w = mount(TerminalPane, { props: { api }, ...stubs });
    await flushPromises();
    expect(w.find('[data-test=terminal-pane-signin]').exists()).toBe(true);
    status = 'signed-in';
    await vi.advanceTimersByTimeAsync(5000);
    await flushPromises();
    expect(w.find('[data-test=terminal-pane-signin]').exists()).toBe(false);
  });

  it('is never shown for another CLI, and asks nothing for it', async () => {
    const { api, calls } = fakeApi(() => [{ ...grok('not-signed-in', null), cliId: 'claude' }]);
    showChannelTerminal('agent-test:claude', 'Terminal · Claude Code · global policy');
    const w = mount(TerminalPane, { props: { api }, ...stubs });
    await flushPromises();
    expect(w.find('[data-test=terminal-pane-signin]').exists()).toBe(false);
    expect(calls).not.toContain('agents.detect');
  });

  it('a failed detect shows nothing', async () => {
    const { api } = fakeApi(() => { throw new Error('unavailable'); });
    showChannelTerminal('agent-test:grok', 'Terminal · Grok Build (xAI) · global policy');
    const w = mount(TerminalPane, { props: { api }, ...stubs });
    await flushPromises();
    expect(w.find('[data-test=terminal-pane-signin]').exists()).toBe(false);
  });
});
