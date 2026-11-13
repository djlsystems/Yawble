import { _electron as electron, expect, test, type ElectronApplication, type Page } from '@playwright/test';
import { companionHome, removeCompanionHomes } from './companion-home';
import { chmodSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// The first-run journey: with no config files, a person connects a
// Claude subscription and an Anthropic API key, signs in to Grok, adds an xAI key, flips both switches, creates
// two teams (one mixing Claude Code and Grok), changes one agent's policy, opens its terminal during a run and
// sees live output with the effective-policy banner, closes and reopens the window and gets the replay.
//
// Credentials are the real credential service, here its 0600 file fallback in the test's own
// data folder. The host panes run the CLIs the app detects (first on PATH), which here are STUB claude and grok
// scripts in a temp folder put first on PATH: the stub `claude setup-token` prints a made-up token, the stub
// `grok login` writes a made-up session into GROK_HOME. Runs use the core's fake-pty runner
// (YAWBLE_RUNNER=fake-pty). Every credential below is a made-up string; no real CLI or credential is used or read.
const shellDir = path.resolve(__dirname, '..', '..');
const shots = process.env.YAWBLE_E2E_SHOTS ?? path.join(shellDir, 'test-results', 'first-run');

// Windows are created hidden and shown on 'ready-to-show'. Under Xvfb (no window manager) the DOM can be asserted
// before the window is painted, and Page.captureScreenshot then fails with "Unable to capture screenshot" (as in
// window.spec.ts). Retry for a few paint intervals; still fails if none succeeds.
async function shot(page: Page, name: string) {
  await expect(async () => {
    await page.screenshot({ path: path.join(shots, `${name}.png`), timeout: 5_000 });
  }).toPass({ intervals: [250, 500, 1_000], timeout: 15_000 });
}

/** Waits for a save's confirmation; if the dialog shows its error line instead, fails with that text. */
async function expectSaved(page: Page, okSel: string, errSel: string, text?: string) {
  const ok = page.locator(okSel);
  const err = page.locator(errSel);
  await expect(ok.or(err)).toBeVisible({ timeout: 15_000 });
  if (await err.isVisible()) throw new Error(`save refused: ${await err.textContent()}`);
  if (text) await expect(ok).toContainText(text);
}

/** A folder of stub claude and grok "installed on the host" (put first on PATH, where the detector looks first). */
function stubClis(root: string): string {
  const stubs: Record<string, string> = {
    claude: [
      '#!/bin/sh',
      '[ "$1" = "setup-token" ] || { echo "stub claude: only setup-token" >&2; exit 2; }',
      'echo "STUB claude setup-token (e2e): no browser, no Anthropic account"',
      'echo "Your OAuth token (valid for one year):"',
      'echo "fake-oauth-token-e2e-0001"',
      'echo "Store this token securely."',
    ].join('\n'),
    grok: [
      '#!/bin/sh',
      '[ "$1" = "login" ] || exit 0',
      'echo "STUB grok login (e2e): GROK_HOME=$GROK_HOME"',
      'exp=$(( $(date +%s) + 21600 ))',
      'printf \'{"https://auth.x.ai::00000000-0000-0000-0000-000000000000":{"access_token":"fake-grok-session-e2e-0001","refresh_token":"fake-refresh-e2e","auth_mode":"oauth","expires_at":%s}}\' "$exp" > "$GROK_HOME/auth.json"',
      'echo "Signed in to Grok."',
    ].join('\n'),
  };
  const dir = path.join(root, 'host-clis');
  for (const [cli, body] of Object.entries(stubs)) {
    const file = path.join(dir, cli);
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, body + '\n');
    chmodSync(file, 0o755);
  }
  return dir;
}

/**
 * Opt-in (YAWBLE_E2E_GUARDCHECK_DIR = the yawble-guardcheck build output, e.g. bridge/src/Yawble.GuardCheck/bin/Debug/
 * net10.0): installs the probe into the e2e's install folder as a dotnet wrapper, so the window's guardrail check
 * runs for real in the sandbox. Only where the sandbox's proxy mode works (Linux with bwrap, slirp4netns and /dev/net/tun).
 */
function installProbe(root: string): boolean {
  const from = process.env.YAWBLE_E2E_GUARDCHECK_DIR;
  if (!from || process.platform === 'win32') return false;
  const bin = path.join(root, 'bin');
  const dir = path.join(bin, 'yawble-guardcheck.d');
  mkdirSync(dir, { recursive: true });
  for (const f of readdirSync(from)) copyFileSync(path.join(from, f), path.join(dir, f));
  const dotnet = existsSync('/usr/bin/dotnet') ? '/usr/bin/dotnet' : 'dotnet';
  writeFileSync(path.join(bin, 'yawble-guardcheck'), `#!/bin/sh\nexec ${dotnet} "$(dirname "$0")/yawble-guardcheck.d/yawble-guardcheck.dll" "$@"\n`);
  chmodSync(path.join(bin, 'yawble-guardcheck'), 0o755);
  mkdirSync(path.join(root, 'roles'), { recursive: true });
  mkdirSync(path.join(root, 'home'), { recursive: true });
  return true;
}

/** The agent's terminal, in the main window's terminal pane (one at a time; no window of its own). */
async function terminalFor(_app: ElectronApplication, main: Page, agent: string) {
  await main.locator(`[data-test=agent-${agent}] [data-test=open-terminal]`).click();
  const term = main.locator('[data-test=terminal-pane]');
  await term.waitFor();
  return term;
}

test('first run: credentials, two teams, one agent\'s policy, a live terminal and its replay', async () => {
  test.skip(process.platform === 'win32', 'the stub claude and grok are shell scripts');
  test.setTimeout(180_000);
  mkdirSync(shots, { recursive: true });
  const root = mkdtempSync(path.join(os.tmpdir(), 'yawble-e2e5-'));
  const dataDir = path.join(root, 'data');
  // The install folder (bin/, roles/) is outside the app data root, as a real install is: the policy screens dry-run
  // the request the launcher would compile, and the compiler refuses grants inside the app data root.
  const install = mkdtempSync(path.join(os.tmpdir(), 'yawble-e2e5-install-'));
  const clis = stubClis(install);
  const realCheck = installProbe(install);
  const extra = process.env.YAWBLE_E2E_NO_SANDBOX === '1' ? ['--no-sandbox'] : [];
  const app = await electron.launch({
    args: [...extra, shellDir],
    env: { ...process.env, ...companionHome(), YAWBLE_DATA_DIR: dataDir, YAWBLE_INSTALL_DIR: install, PATH: `${clis}${path.delimiter}${process.env.PATH}`, YAWBLE_RUNNER: 'fake-pty', YAWBLE_CREDENTIAL_STORE: 'file', YAWBLE_FAKE_RUN_TICKS: '0',
      // The real check probes the person's home (~/.ssh, ~/.claude, ~/.grok); point it at a scratch one, never a real login.
      ...(realCheck ? { HOME: path.join(install, 'home') } : {}) } as Record<string, string>,
  });
  try {
    const main = await app.firstWindow();
    main.on('dialog', (d) => void d.accept());
    await main.setViewportSize({ width: 1200, height: 800 });
    await expect(main.locator('[data-test=slots]')).toHaveText('Run slots: 0 / 4 in use · 0 queued', { timeout: 60_000 });
    await main.locator('[data-test=nav-teams]').click();
    await expect(main.locator('[data-test=no-teams]')).toBeVisible();
    await shot(main, '01-first-start');

    // ---- Agents: a live agents.detect against this computer's real PATH (found or not depends on the machine).
    await main.locator('[data-test=open-app-settings]').click();
    await main.locator('[data-test=open-agents]').click();
    for (const cli of ['claude', 'grok']) {
      const row = main.locator(`[data-test=agent-cli-${cli}]`);
      await expect(row.locator('[data-test=cli-found]')).toHaveText(/^(Found|Not found)$/, { timeout: 30_000 });
      if ((await row.locator('[data-test=cli-found]').textContent()) === 'Found') await expect(row.locator(`[data-test=test-${cli}]`)).toBeVisible();
      else await expect(row.locator('[data-test=cli-install-help]')).toContainText('https://');
    }
    await shot(main, '01b-agents-detected');
    await main.locator('[data-test=nav-teams]').click();

    // ---- Settings → Model credentials
    await main.locator('[data-test=open-credentials]').click();
    // No Secret Service on the test host: the owner-only file fallback, with its one-time warning.
    await expect(main.locator('[data-test=cred-storage-warning]')).toContainText('only your account can read');
    await expect(main.locator('[data-test=cred-fake-note]')).toHaveCount(0);
    await main.locator('[data-test=connect-claude]').click();
    await expect(main.locator('[data-test=claude-section] [data-test=terminal-mirror]')).toContainText('fake-oauth-token-e2e-0001');
    await shot(main, '02a-claude-setup-token-host-pane');
    await main.locator('[data-test=setup-token-input]').fill('fake-oauth-token-e2e-0001');
    await main.locator('[data-test=save-setup-token]').click();
    await expect(main.locator('[data-test=claude-sub-status]')).toContainText('Connected');
    await expect(main.locator('[data-test=claude-sub-status]')).toContainText('expires');
    await main.locator('[data-test=anthropic-key-input]').fill('fake-anthropic-key-e2e-0001');
    await main.locator('[data-test=save-anthropic-key]').click();
    await expect(main.locator('[data-test=anthropic-key-status]')).toContainText('Saved');

    await main.locator('[data-test=grok-sign-in]').click();
    await expect(main.locator('[data-test=grok-section] [data-test=terminal-mirror]')).toContainText('Signed in to Grok.');
    await expect(main.locator('[data-test=grok-login-status]')).toContainText('Signed in');
    await expect(main.locator('[data-test=grok-login-status]')).toContainText('last refresh');
    await main.locator('[data-test=xai-key-input]').fill('fake-xai-key-e2e-0001');
    await main.locator('[data-test=save-xai-key]').click();
    await expect(main.locator('[data-test=xai-key-status]')).toContainText('Saved');

    // Flip both switches, and back again for Claude.
    await main.locator('[data-test=claude-mode-apiKey]').check();
    await expect(main.locator('[data-test=claude-mode]')).toContainText('the API key');
    await main.locator('[data-test=grok-mode-apiKey]').check();
    await expect(main.locator('[data-test=grok-mode]')).toContainText('the xAI API key');
    await shot(main, '02-credentials-both-switched-to-api-keys');
    await main.locator('[data-test=claude-mode-subscription]').check();
    await expect(main.locator('[data-test=claude-mode]')).toContainText('the subscription');
    await main.locator('[data-test=grok-mode-login]').check();
    await expect(main.locator('[data-test=grok-mode]')).toContainText('your Grok login');
    await shot(main, '03-credentials-subscription-and-grok-login');
    // The pane is gone once the token is saved; no value ever comes back from the core.
    await expect(main.locator('body')).not.toContainText('fake-oauth-token-e2e-0001');
    await expect(main.locator('body')).not.toContainText('fake-anthropic-key-e2e-0001');
    await expect(main.locator('body')).not.toContainText('fake-xai-key-e2e-0001');
    // They reached the real credential service: the owner-only file and the host Grok home, never the database.
    const credFile = path.join(dataDir, 'credentials', 'credentials.json');
    expect(statSync(credFile).mode & 0o777).toBe(0o600);
    const stored = readFileSync(credFile, 'utf8');
    for (const v of ['fake-oauth-token-e2e-0001', 'fake-anthropic-key-e2e-0001', 'fake-xai-key-e2e-0001']) expect(stored).toContain(v);
    expect(readFileSync(path.join(root, 'grok-home', 'auth.json'), 'utf8')).toContain('fake-grok-session-e2e-0001');
    expect(readFileSync(path.join(dataDir, 'yawble.db')).includes('fake-oauth-token-e2e-0001')).toBe(false);
    await main.locator('[data-test=modal-close]').click();

    // ---- Two teams: one all Claude Code, one mixed
    await main.locator('[data-test=new-team]').first().click();
    await main.locator('[data-test=team-name]').fill('docs-site');
    await main.locator('[data-test=team-repo]').fill(path.join(root, 'repos', 'docs-site'));
    await main.locator('[data-test=member-name-0]').fill('Cleo');
    await main.locator('[data-test=create-team]').click();
    await expect(main.locator('[data-test=team-title]')).toContainText('Team docs-site · 2 agents');

    await main.locator('[data-test=new-team]').first().click();
    await main.locator('[data-test=team-name]').fill('payments');
    await main.locator('[data-test=team-repo]').fill(path.join(root, 'repos', 'payments'));
    await main.locator('[data-test=member-name-0]').fill('Ada');
    await main.locator('[data-test=add-member-row]').click();
    await main.locator('[data-test=member-name-1]').fill('Ben');
    await main.locator('[data-test=member-cli-1]').selectOption('grok');
    await shot(main, '04-new-mixed-team');
    await main.locator('[data-test=create-team]').click();
    await expect(main.locator('[data-test=team-title]')).toContainText('Team payments · 3 agents');
    await expect(main.locator('[data-test=agent-Ben] [data-test=agent-cli]')).toHaveText('grok');
    await expect(main.locator('[data-test=agent-Ada] [data-test=agent-cli]')).toHaveText('claude');
    await expect(main.locator('[data-test=team-item]')).toHaveCount(2);

    // Add and remove an agent.
    await main.locator('[data-test=add-agent-name]').fill('Temp');
    await main.locator('[data-test=add-agent]').click();
    await expect(main.locator('[data-test=agent-Temp]')).toBeVisible();
    await main.locator('[data-test=agent-Temp] [data-test=remove-agent]').click();
    await expect(main.locator('[data-test=agent-Temp]')).toHaveCount(0);

    // ---- Change one agent's policy: Ben (Grok) gets a 15-minute timeout, and a confirmed loosening.
    await main.locator('[data-test=agent-Ben] [data-test=open-settings]').click();
    await main.locator('[data-test=tab-policy]').click();
    await main.locator('[data-test=inherit-compute-wallTimeoutSec]').uncheck();
    await main.locator('[data-test=override-compute-wallTimeoutSec]').fill('900');
    await main.locator('[data-test=save-override]').click();
    await expectSaved(main, '[data-test=settings-saved]', '[data-test=settings-error]', 'Override saved');
    await expect(main.locator('[data-test=effective-compute-wallTimeoutSec]')).toHaveText('900');
    // Widening through the override is refused.
    await main.locator('[data-test=inherit-network-allowHosts]').uncheck();
    await main.locator('[data-test=override-network-allowHosts]').fill('registry.npmjs.org');
    await main.locator('[data-test=save-override]').click();
    await expect(main.locator('[data-test=settings-error]')).toContainText('only the loosen block can add a host');
    await main.locator('[data-test=inherit-network-allowHosts]').check();
    // The loosen block, with its preview and confirmation.
    await main.locator('[data-test=loosen-network-allowHosts]').fill('registry.npmjs.org');
    await main.locator('[data-test=preview-loosen]').click();
    await expect(main.locator('[data-test=loosen-preview-item]')).toHaveText(['network.allowHosts + registry.npmjs.org']);
    await main.locator('[data-test=confirm-loosen]').scrollIntoViewIfNeeded();
    await shot(main, '05-agent-policy-loosen-preview');
    await main.locator('[data-test=confirm-loosen]').click();
    await expect(main.locator('[data-test=loosen-current]')).toContainText('registry.npmjs.org');
    await expect(main.locator('[data-test=effective-network-allowHosts]')).toHaveText('registry.npmjs.org');
    await main.locator('[data-test=policy-table]').scrollIntoViewIfNeeded();
    await shot(main, '06-agent-policy-saved');
    await main.locator('[data-test=modal-close]').click();
    await expect(main.locator('[data-test=agent-Ben] [data-test=agent-timeout]')).toHaveText('15 min');
    await expect(main.locator('[data-test=agent-Ben] [data-test=loosened-chip]')).toContainText('registry.npmjs.org');
    await expect(main.locator('[data-test=agent-Ada] [data-test=agent-timeout]')).toHaveText('2 h');

    // ---- The card's guardrail button opens the agent's Guardrails tab, through Electron main's method
    // allow-list to the core. By default this e2e installs no yawble-guardcheck probe (and runs no real sandbox), so the core
    // answers why the check is off and Run is disabled. With YAWBLE_E2E_GUARDCHECK_DIR (see installProbe) the window
    // runs the real check in the sandbox and it must pass. The check itself is covered by RealSandboxGuardrailTests.
    await main.locator('[data-test=agent-Ben] [data-test=open-guardrail]').click();
    await expect(main.locator('[data-test=tab-guardrails]')).toHaveClass(/on/);
    await expect(main.locator('[data-test=guardrail-panel]')).toContainText("Ben's effective policy");
    if (!realCheck) {
      await expect(main.locator('[data-test=guardrail-unavailable]')).toContainText(/the sandbox launcher is off|the probe is not installed/);
      await expect(main.locator('[data-test=guardrail-run]')).toBeDisabled();
      await shot(main, '06b-agent-guardrail-check-unavailable');
    } else {
      await expect(main.locator('[data-test=guardrail-none]')).toBeVisible();
      await main.locator('[data-test=guardrail-run]').click();
      await expect(main.locator('[data-test=guardrail-verdict]')).toHaveText(/PASSED|FAILED|STOPPED/, { timeout: 120_000 });
      const verdict = await main.locator('[data-test=guardrail-verdict]').textContent();
      if (verdict !== 'PASSED') throw new Error(`the guardrail check ${verdict}:\n${await main.locator('[data-test=guardrail-panel]').innerText()}`);
      await expect(main.locator('[data-test=guardrail-summary]')).toContainText('0 unexpected');
      await expect(main.locator('[data-test=guardrail-result-gateway\\.connect-allowed-host]')).toContainText('ALLOWED');
      await shot(main, '06b-agent-guardrail-check-real-sandbox');
    }
    await main.locator('[data-test=modal-close]').click();

    // ---- The global run limit
    await main.locator('[data-test=open-app-settings]').click();
    await main.locator('[data-test=open-global-policy]').click();
    await main.locator('[data-test=global-maxConcurrentAgents]').fill('3');
    await main.locator('[data-test=save-global]').click();
    await expectSaved(main, '[data-test=global-saved]', '[data-test=global-error]');
    await shot(main, '07-global-policy');
    await main.locator('[data-test=nav-teams]').click();
    await expect(main.locator('[data-test=slots]')).toContainText('/ 3 in use');

    // ---- A run: tell the Manager; it splits the work to Ada and Ben, who run until stopped.
    await main.locator('[data-test=tell-text]').fill('Add a refunds endpoint and its test');
    await main.locator('[data-test=tell-send]').click();
    await expect(main.locator('[data-test=agent-Ben] [data-test=agent-state]')).toContainText('Running', { timeout: 30_000 });
    await expect(main.locator('[data-test=slots]')).toContainText('in use');
    await shot(main, '08-team-running');

    // ---- Ben's terminal during the run: banner and live output.
    let term = await terminalFor(app, main, 'Ben');
    await expect(term.locator('[data-test=banner-title]')).toHaveText(/^payments \/ Ben · sandbox · \w+ · grok · net: model \+ bus \+ registry\.npmjs\.org$/);
    await expect(term.locator('[data-test=terminal-pane-title]')).toContainText('Ben · payments');
    await expect(term.locator('[data-test=banner-details]')).toContainText('timeout 15 min');
    await expect(term.locator('[data-test=banner-loosened]')).toContainText('network.allowHosts + registry.npmjs.org');
    await expect(term.locator('[data-test=terminal-state]')).toContainText('input off during headless run');
    const mirror = term.locator('[data-test=terminal-mirror]');
    await expect(mirror).toContainText('$ grok -p (batch of 1');
    await expect(mirror).toContainText('● step 2');
    await expect(mirror).toContainText('● step 4'); // still growing: live
    await shot(main, '09-terminal-live');
    const before = (await mirror.textContent()) ?? '';
    await term.locator('[data-test=terminal-pane-close]').click();

    // ---- Reopen: the replay comes back (everything shown before, from the start of the run), then live again.
    await main.waitForTimeout(1500);
    term = await terminalFor(app, main, 'Ben');
    const mirror2 = term.locator('[data-test=terminal-mirror]');
    await expect(mirror2).toContainText('$ grok -p (batch of 1');
    await expect.poll(async () => ((await mirror2.textContent()) ?? '').startsWith(before)).toBe(true);
    const stepsBefore = (before.match(/● step \d+/g) ?? []).length;
    await expect.poll(async () => (((await mirror2.textContent()) ?? '').match(/● step \d+/g) ?? []).length).toBeGreaterThan(stepsBefore + 1);
    await shot(main, '10-terminal-reopened-replay');

    // ---- Stop, then an interactive session (the fake-pty runner's).
    await term.locator('[data-test=terminal-stop]').click();
    await expect(mirror2).toContainText('stopped by the person');
    await expect(term.locator('[data-test=terminal-state]')).toContainText('last run failed: stopped: by the person');
    await term.locator('[data-test=open-interactive]').click();
    await expect(term.locator('[data-test=terminal-state]')).toContainText('input on');
    await term.locator('[data-test=xterm]').click();
    await main.keyboard.type('hello');
    await main.keyboard.press('Enter');
    await expect(mirror2).toContainText('you said "hello"');
    await shot(main, '11-terminal-interactive');
    await term.locator('[data-test=terminal-stop]').click();
    await term.locator('[data-test=terminal-pane-close]').click();

    await expect(main.locator('[data-test=agent-Ben] [data-test=agent-state]')).toContainText('stopped: by the person');
    await shot(main, '12-team-after-stop');
  } finally {
    await app.close();
    removeCompanionHomes();
    rmSync(root, { recursive: true, force: true });
    rmSync(install, { recursive: true, force: true });
  }
});
