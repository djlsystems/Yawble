// Card 534 (B119 Part B): Team settings > Network has "Allow localhost (macOS)", off by default, with the warning word for
// word; turning it on sends teams.localhost { teamId, on: true } and it reads back on. A Visitor sees it read-only (the
// toggle is disabled and nothing is sent). Headless Chrome against the phone-remote fake core (remote.html, tests/e2e/phone).
//
//   $env:PW_CHANNEL='chrome'; npx playwright test card534-team-localhost      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/B119 (else test-results/B119).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore, type Access } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'B119');
const WARNING = "Agents on this team can open local ports and connect to any service listening on this Mac's localhost (your dev servers, local databases). Needed for classic dotnet test (VSTest) and some other test runners. While it is on, a server an agent starts on 0.0.0.0 can be reached from other machines on your network unless the macOS firewall blocks it. Keep the macOS firewall on. Outbound traffic still goes only through the gateway.";

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

async function open(browser: Browser, access: Access = 'full', width = 1280) {
  const core = createFakeCore();
  core.setAsks(false);
  core.setAccess(access);
  const sent: { method: string; params: any }[] = [];
  const ctx = await browser.newContext({ viewport: { width, height: 800 }, colorScheme: 'light' });
  await ctx.addInitScript(() => { try { localStorage.setItem('yawble.remoteTheme', 'light'); } catch { /* none */ } });
  await ctx.route(`${ORIGIN}/**`, async (route) => {
    const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
    const file = path.resolve(builtDir, rel || 'remote.html');
    if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
    return route.fulfill({ path: file });
  });
  await ctx.routeWebSocket(/\/rpc$/, (ws) => {
    ws.onMessage((m) => {
      const msg = JSON.parse(String(m));
      sent.push({ method: msg.method, params: msg.params });
      const out = core.answer(String(m));
      if (out) ws.send(out);
    });
  });
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${ORIGIN}/remote.html`);
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await page.waitForTimeout(400);
  return { page, ctx, errors, sent };
}

async function networkTab(page: Page) {
  await page.locator('[data-test=sidebar] [data-test=nav-teams]').first().click();
  await page.locator('[data-test=team-open-payments]').click();
  await page.locator('[data-test=open-manage]').first().click();
  await page.locator('[data-test=manage-tab-network]').click();
  await expect(page.locator('[data-test=team-localhost]')).toBeVisible();
}

test('off by default with the warning; turning it on sends teams.localhost { teamId, on: true } and it stays on', async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser);
  await networkTab(page);
  const toggle = page.locator('[data-test=team-localhost-toggle]');
  await expect(page.locator('[data-test=team-localhost]')).toContainText('Allow localhost (macOS)');
  await expect(toggle).not.toBeChecked();
  await expect(page.locator('[data-test=team-localhost-warning]')).toHaveText(WARNING);
  await expect(page.locator('[data-test=team-localhost-warning] code')).toHaveText(['dotnet test', '0.0.0.0']);
  await page.locator('[data-test=team-localhost]').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'card534-team-localhost-off.png') });

  await toggle.check();
  await expect.poll(() => sent.filter((s) => s.method === 'teams.localhost' && s.params?.on !== undefined).map((s) => s.params)).toEqual([{ teamId: 1, on: true }]);
  await expect(toggle).toBeChecked();
  await page.screenshot({ path: path.join(SHOTS, 'card534-team-localhost-on.png') });

  // Closed and opened again: read back from the core.
  await page.keyboard.press('Escape');
  await page.locator('[data-test=open-manage]').first().click();
  await page.locator('[data-test=manage-tab-network]').click();
  await expect(page.locator('[data-test=team-localhost-toggle]')).toBeChecked();
  expect(errors).toEqual([]);
  await ctx.close();
});

test('a Visitor sees it read-only: disabled, the warning shown, nothing sent', async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser, 'read');
  await networkTab(page);
  const toggle = page.locator('[data-test=team-localhost-toggle]');
  await expect(toggle).toBeDisabled();
  await expect(toggle).not.toBeChecked();
  await expect(page.locator('[data-test=team-localhost-warning]')).toHaveText(WARNING);
  await page.locator('[data-test=team-localhost]').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'card534-team-localhost-visitor.png') });
  expect(sent.some((s) => s.method === 'teams.localhost' && s.params?.on !== undefined)).toBe(false);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('on a phone (375): the toggle and the whole warning fit the sheet', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 'full', 375);
  // The phone shell: the team's page, its Settings (through the ⋯ menu when not in the header), then the Network section.
  await page.goto(`${ORIGIN}/remote.html#team=1`);
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  const opener = page.locator('[data-test=open-manage]').first();
  if (!(await opener.isVisible().catch(() => false))) await page.locator('[data-test=team-more]').click();
  await opener.click();
  await page.locator('[data-test=manage-tab-network]').click();
  await expect(page.locator('[data-test=manage-section-title]')).toHaveText('Network');
  const block = page.locator('[data-test=team-localhost]');
  await expect(block).toBeVisible();
  await expect(page.locator('[data-test=team-localhost-warning]')).toHaveText(WARNING);
  const box = await block.boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(375);
  await block.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'card534-team-localhost-phone.png') });
  expect(errors).toEqual([]);
  await ctx.close();
});
