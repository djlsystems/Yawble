// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import TerminalPane from '../../src/renderer/src/components/TerminalPane.vue';
import type { DetectedCli, YawbleApi } from '../../src/shared/api';
import { showChannelTerminal, terminalPane } from '../../src/renderer/src/terminalPane';

// Grok's Terminal pane says "Not signed in" (with the detail) while the Settings > Agents tile's sign-in
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

// The detail strings the core sends (SignInCatalog.Grok / GrokRefused in core/src/Yawble.Core/Agents/CliSignIn.cs) begin
// with the heading's own words: the pane shows the fix text once and the rest of the detail as the reason.
describe('Grok banner with the core\'s own detail', () => {
  const FIX = 'Not signed in: run grok in your own terminal and sign in';
  const CORE_DETAILS = [
    'Not signed in: run `grok` in your own terminal and sign in. /home/alice/.grok/auth.json holds no sign-in (grok clears it when xAI refuses to renew it); then reopen this dialog.',
    'Not signed in: run `grok` in your own terminal and sign in. /home/alice/.grok/auth.json is missing; then reopen this dialog.',
    "Not signed in: run `grok` in your own terminal and sign in. The app's last Grok request (2026-11-22 15:00) was refused: the Grok sign-in on this computer was refused by xAI.",
    "Not signed in: run `grok` in your own terminal and sign in. The app's last Grok request (2026-11-22 15:00) was refused: the Grok sign-in on this computer was refused or has expired: run 'grok' in your own terminal to renew it.",
  ];
  const REASONS = [
    '/home/alice/.grok/auth.json holds no sign-in (grok clears it when xAI refuses to renew it).',
    '/home/alice/.grok/auth.json is missing.',
    "The app's last Grok request (2026-11-22 15:00) was refused: the Grok sign-in on this computer was refused by xAI.",
    "The app's last Grok request (2026-11-22 15:00) was refused: the Grok sign-in on this computer was refused or has expired: run 'grok' in your own terminal to renew it.",
  ];
  let lastDetail: string | null = null;
  async function banner(detail: string) {
    const { api } = fakeApi(() => [grok('not-signed-in', detail)]);
    showChannelTerminal('agent-test:grok', 'Terminal · Grok Build (xAI) · global policy');
    const w = mount(TerminalPane, { props: { api }, ...stubs });
    await flushPromises();
    lastDetail = w.find('[data-test=terminal-pane-signin-detail]').exists() ? w.get('[data-test=terminal-pane-signin-detail]').text() : null;
    return w.get('[data-test=terminal-pane-signin]').text().replace(/`/g, '');
  }

  it('shows the banner for each detail', async () => {
    for (const d of CORE_DETAILS) expect(await banner(d)).toContain(FIX);
  });

  it('shows the fix text once', async () => {
    for (const d of CORE_DETAILS) expect((await banner(d)).split(FIX).length - 1).toBe(1);
  });

  it('shows the reason as the detail line, without "reopen this dialog"', async () => {
    for (const [i, d] of CORE_DETAILS.entries()) {
      const text = await banner(d);
      expect(text).toContain(REASONS[i]);
      expect(text).not.toMatch(/reopen this dialog/i);
      expect(lastDetail).toBe(REASONS[i]);
    }
  });
});
