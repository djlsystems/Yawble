// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import TerminalPane from '../../src/renderer/src/components/TerminalPane.vue';
import type { DetectedCli, YawbleApi } from '../../src/shared/api';
import { showChannelTerminal, terminalPane } from '../../src/renderer/src/terminalPane';

// Card 578 (B128 verify): the Grok Terminal banner fed the detail strings the core really sends
// (SignInCatalog.Grok / GrokRefused in core/src/Yawble.Core/Agents/CliSignIn.cs). Those details already begin with
// SignInCatalog.GrokNotSignedIn, so the heading plus the detail show the fix text twice. That is the fault reported on
// card 578. The repeat is pinned with it.fails: the test fails today, so it.fails passes. Once the fault is fixed the test
// passes, it.fails then fails, and `.fails` must be removed.
const FIX = 'Not signed in: run grok in your own terminal and sign in';
const CORE_DETAILS = [
  'Not signed in: run `grok` in your own terminal and sign in. /home/alice/.grok/auth.json holds no sign-in (grok clears it when xAI refuses to renew it); then reopen this dialog.',
  'Not signed in: run `grok` in your own terminal and sign in. /home/alice/.grok/auth.json is missing; then reopen this dialog.',
  "Not signed in: run `grok` in your own terminal and sign in. The app's last Grok request (2026-11-22 15:00) was refused: the Grok sign-in on this computer was refused by xAI.",
  "Not signed in: run `grok` in your own terminal and sign in. The app's last Grok request (2026-11-22 15:00) was refused: the Grok sign-in on this computer was refused or has expired: run 'grok' in your own terminal to renew it.",
];
// Re-check (card 578 after card 577's fix): the reason each detail carries, which the pane must still show.
const REASONS = [
  '/home/alice/.grok/auth.json holds no sign-in (grok clears it when xAI refuses to renew it).',
  '/home/alice/.grok/auth.json is missing.',
  "The app's last Grok request (2026-11-22 15:00) was refused: the Grok sign-in on this computer was refused by xAI.",
  "The app's last Grok request (2026-11-22 15:00) was refused: the Grok sign-in on this computer was refused or has expired: run 'grok' in your own terminal to renew it.",
];

const base: DetectedCli = {
  cliId: 'grok', found: true, launcherPath: '/usr/bin/grok', executablePath: '/opt/grok/grok', version: '1.0.0',
  installKind: 'native', readOnlyFolders: [], configFolders: [], problems: [], installHelp: 'install it',
};
const stubs = { global: { stubs: { TerminalScreen: true, TerminalApp: true, TerminalView: true } } };
enableAutoUnmount(afterEach);
beforeEach(() => { terminalPane.open = false; terminalPane.target = null; });

let lastDetail: string | null = null;
async function banner(detail: string) {
  const api = { call: async () => [{ ...base, signIn: { status: 'not-signed-in', detail } }], onEvent: () => () => {} } as unknown as YawbleApi;
  showChannelTerminal('agent-test:grok', 'Terminal · Grok Build (xAI) · global policy');
  const w = mount(TerminalPane, { props: { api }, ...stubs });
  await flushPromises();
  lastDetail = w.find('[data-test=terminal-pane-signin-detail]').exists() ? w.get('[data-test=terminal-pane-signin-detail]').text() : null;
  return w.get('[data-test=terminal-pane-signin]').text().replace(/`/g, '');
}

describe('B128 verify: Grok banner with the core\'s real detail', () => {
  it('shows the banner for each real detail', async () => {
    for (const d of CORE_DETAILS) expect(await banner(d)).toContain(FIX);
  });

  // Was the fault on card 578 (the fix text twice); fixed by card 577 (TerminalPane drops the heading from the detail).
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
