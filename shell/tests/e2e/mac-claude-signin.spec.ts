// Settings > Agents on macOS, where Claude Code keeps its sign-in in the login Keychain item
// "Claude Code-credentials" and the app's gateway reads it there. The Claude tile shows the core's sign-in line:
//  - signed in (the Keychain item exists): it says so and that agents use it through the gateway;
//  - not signed in (no item): it says how to sign in ("run 'claude' in your own terminal and sign in (/login)").
//  - Never the old "Claude members cannot run on macOS" text.
// Headless Chrome against the phone-remote fake core (remote.html, tests/e2e/phone); agents.detect answers with the
// core's own macOS texts (Agents/CliSignIn.cs, SignInCatalog.Claude). 1280x800 and 375x812, light and dark.
//
//   $env:PW_CHANNEL='chrome'; npx playwright test mac-claude-signin --workers=1   (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/mac-claude-signin (else test-results/mac-claude-signin).
import { expect, test, type Browser } from './phone/fixtures';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { answers, createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';
import { settled } from './settle';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(120_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'mac-claude-signin');

// The core's words (SignInCatalog.Claude on macOS).
const SIGNED_IN = 'Signed in on this computer (the login Keychain item "Claude Code-credentials"). Agents use this sign-in through the app\'s gateway, '
  + 'which reads it from the Keychain and adds it to each model request: agents never see it, and your own claude renews it.';
const NOT_SIGNED_IN = 'Not signed in: the login Keychain item "Claude Code-credentials" is missing. To sign in, '
  + "run 'claude' in your own terminal and sign in (/login), then reopen this dialog.";

const macClaude = (signedIn: boolean) => ({
  cliId: 'claude', found: true, launcherPath: '/Users/alice/.local/bin/claude', executablePath: '/Users/alice/.local/share/claude/versions/2.1.325',
  version: '2.1.325', installKind: 'native', readOnlyFolders: ['/Users/alice/.local/share/claude/versions'], configFolders: ['/Users/alice/.claude'],
  problems: [], installHelp: '',
  signIn: signedIn
    ? { status: 'signed-in', detail: SIGNED_IN, files: [{ path: 'Claude Code-credentials', access: 'hostStore', present: true }], environment: {} }
    : { status: 'not-signed-in', detail: NOT_SIGNED_IN, files: [{ path: 'Claude Code-credentials', access: 'hostStore', present: false }], environment: {} },
});
const macGrok = {
  cliId: 'grok', found: true, launcherPath: '/Users/alice/.grok/bin/grok', executablePath: '/Users/alice/.grok/bin/grok', version: '1.0.0',
  installKind: 'native', readOnlyFolders: ['/Users/alice/.grok/bin'], configFolders: ['/Users/alice/.grok'], problems: [], installHelp: '',
  signIn: { status: 'signed-in', detail: 'Signed in on this computer (/Users/alice/.grok/auth.json).', files: [], environment: {} },
};

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

async function open(browser: Browser, size: { width: number; height: number }, scheme: 'light' | 'dark', signedIn: boolean) {
  answers['agents.detect'] = () => [macClaude(signedIn), macGrok];
  const core = createFakeCore();
  core.setAsks(false);
  const ctx = await browser.newContext({ viewport: size, colorScheme: scheme, isMobile: size.width < 500, hasTouch: size.width < 500 });
  await ctx.addInitScript((t) => { try { localStorage.setItem('yawble.remoteTheme', t); } catch { /* none */ } }, scheme);
  await ctx.route(`${ORIGIN}/**`, async (route) => {
    const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
    const file = path.resolve(builtDir, rel || 'remote.html');
    if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
    return route.fulfill({ path: file });
  });
  await ctx.routeWebSocket(/\/rpc$/, (ws) => {
    ws.onMessage((m) => { const out = core.answer(String(m)); if (out) ws.send(out); });
  });
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${ORIGIN}/remote.html`);
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await settled(page); // the start-up reads in
  // Settings (the gear) > Agents.
  await page.getByTestId('settings-gear').click();
  await expect(page.locator('[data-test=settings]')).toBeVisible();
  await page.locator('[data-test=settings] [data-test=open-agents]').first().click();
  await expect(page.locator('[data-test=agent-tiles]')).toBeVisible({ timeout: 15_000 });
  return { page, ctx, errors };
}

const SIZES = [
  { label: 'desktop-1280', size: { width: 1280, height: 800 } },
  { label: 'phone-375', size: { width: 375, height: 812 } },
] as const;

const detect = answers['agents.detect'];
test.afterEach(() => { answers['agents.detect'] = detect; });

for (const { label, size } of SIZES) {
  for (const scheme of ['light', 'dark'] as const) {
    test(`macOS Claude signed in through the Keychain: the tile says so (${label}, ${scheme})`, async ({ browser }) => {
      const { page, ctx, errors } = await open(browser, size, scheme, true);
      const tile = page.locator('[data-test=agent-tiles] article').filter({ has: page.locator('h3', { hasText: /^Claude Code$/ }) }).first();
      const line = tile.locator('[data-test=cli-signin]');
      await expect(line).toHaveText(SIGNED_IN);
      await expect(line).toHaveAttribute('data-status', 'signed-in');
      await expect(line).toHaveClass(/muted/);
      await expect(tile.locator('[data-test=cli-found]')).toHaveText('Found');
      await expect(tile.locator('[data-test=cli-path]')).toHaveText('/Users/alice/.local/share/claude/versions/2.1.325');
      const all = await page.locator('[data-test=agent-tiles]').innerText();
      expect(all).not.toMatch(/cannot run on macOS|Claude members cannot|cannot run here yet/);
      await tile.scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(SHOTS, `agents-mac-claude-signed-in-${label}-${scheme}.png`), fullPage: true });
      expect(errors).toEqual([]);
      await ctx.close();
    });

    test(`macOS Claude without the Keychain item: the tile says how to sign in (${label}, ${scheme})`, async ({ browser }) => {
      const { page, ctx, errors } = await open(browser, size, scheme, false);
      const tile = page.locator('[data-test=agent-tiles] article').filter({ has: page.locator('h3', { hasText: /^Claude Code$/ }) }).first();
      const line = tile.locator('[data-test=cli-signin]');
      await expect(line).toHaveText(NOT_SIGNED_IN);
      await expect(line).toContainText("run 'claude' in your own terminal and sign in (/login)");
      await expect(line).toHaveAttribute('data-status', 'not-signed-in');
      await expect(line).toHaveClass(/warn/);
      // Grok's own line is unchanged by this.
      const grok = page.locator('[data-test=agent-tiles] article').filter({ has: page.locator('h3', { hasText: /^Grok/ }) }).first();
      await expect(grok.locator('[data-test=cli-signin]')).toHaveAttribute('data-status', 'signed-in');
      const all = await page.locator('[data-test=agent-tiles]').innerText();
      expect(all).not.toMatch(/cannot run on macOS|Claude members cannot/);
      await tile.scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(SHOTS, `agents-mac-claude-not-signed-in-${label}-${scheme}.png`), fullPage: true });
      expect(errors).toEqual([]);
      await ctx.close();
    });
  }
}
