// Deliverable expiry in headless Chrome against the phone-remote fake core
// (remote.html, tests/e2e/phone) with its expiry fixtures on (setExpiry): an open #12 waiting on question 3 that expires
// in 20 h, and #9 the app closed as expired. The Teams list tile rows read '· expired' for #9; on the Deliverables page (the team
// page's Deliverables button) #9's pane reads Expired with no Extend, and an idle one's says when it expires, its Extend asked
// first and sent as bus.deliverableExtend { teamId, deliverable }; the Kanban board's Needs you has '#12 will expire tomorrow; it waits on your
// answer to question 3' with Extend; Team settings shows 'Expire idle deliverables after 3 days' and saves 5 as
// teams.deliverableExpiry { teamId, days: 5 }. A Visitor's pane has no Extend.
//
//   npx playwright test deliverable-expiry      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/deliverable-expiry (else test-results/deliverable-expiry).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore, type Access } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';
import { settled } from './settle';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'deliverable-expiry');

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

async function open(browser: Browser, access: Access = 'full') {
  const core = createFakeCore();
  core.setAsks(false);
  core.setExpiry(true);
  core.setAccess(access);
  const sent: { method: string; params: any }[] = [];
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, colorScheme: 'light' });
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
  await settled(page);
  return { page, ctx, errors, sent, core };
}

async function teams(page: Page) {
  await page.locator('[data-test=sidebar] [data-test=nav-teams]').first().click();
  await expect(page.locator('[data-test=team-open-payments]')).toBeVisible();
}

test('the Teams list tile rows read expired for #9, with its last activity as the time', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser);
  await teams(page);
  const row = page.locator('[data-test=team-deliverable][data-id="9"]').first();
  await expect(row).toBeVisible();
  await expect(row.locator('[data-test=team-deliverable-expired]')).toHaveText(/expired/);
  await expect(row).toHaveAttribute('data-status', 'expired');
  await page.screenshot({ path: path.join(SHOTS, 'teams-list-expired.png') });
  expect(errors).toEqual([]);
  await ctx.close();
});

/** The team page's Deliverables button: the Deliverables page, filtered to the team; then the All chip (ended ones too). */
async function deliverablesPage(page: Page) {
  await teams(page);
  await page.locator('[data-test=team-open-payments]').click();
  await page.locator('[data-test=open-deliverables]').first().click();
  await expect(page.locator('[data-test=deliverables-view]')).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-test=deliverables-team-filter]')).toHaveValue('1');
  await expect(page.locator('[role=dialog]')).toHaveCount(0);
  await page.locator('[data-test=deliverables-chip-all]').click();
}

test("the Deliverables page's pane: #9 Expired with no Extend; an expiring one says when, and Extend (confirmed) sends bus.deliverableExtend", async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser);
  await deliverablesPage(page);
  await page.locator('[data-test=deliverables-row][data-wf="9"]').click();
  const pane = page.locator('[data-test=deliverable-pane]');
  await expect(pane).toHaveAttribute('data-id', '9');
  await expect(pane.locator('[data-test=deliverable-pane-status]')).toHaveText('Expired');
  await expect(pane.locator('[data-test=deliverable-pane-extend]')).toHaveCount(0); // an ended one has no Extend
  await expect(pane.locator('[data-test=deliverable-pane-actions]')).toHaveCount(0);
  await page.screenshot({ path: path.join(SHOTS, 'deliverables-page-expired.png') });
  // The page's idle one with an expiry is in the other team (#16, expires in 20 h).
  await page.locator('[data-test=deliverables-team-filter]').selectOption('2');
  await page.locator('[data-test=deliverables-row][data-wf="16"]').click();
  await expect(pane).toHaveAttribute('data-id', '16');
  await expect(pane.locator('[data-test=deliverable-pane-status]')).toHaveAttribute('title', /Expires in (19|20)h/);
  await page.screenshot({ path: path.join(SHOTS, 'deliverables-page-extend.png') });
  await pane.locator('[data-test=deliverable-pane-extend]').click();
  await expect(page.locator('[data-test=confirm-message]')).toHaveText('Extend #16? Its expiry clock starts again.');
  await page.locator('[data-test=confirm-ok]').click();
  await expect.poll(() => sent.filter((s) => s.method === 'bus.deliverableExtend').map((s) => s.params)).toEqual([{ teamId: 2, deliverable: 16 }]);
  expect(errors).toEqual([]);
  await ctx.close();
});
test('Needs you on the Kanban board: #12 will expire tomorrow on question 3, with Extend', async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser);
  await page.locator('[data-test=sidebar] [data-test=nav-board]').first().click();
  const asks = page.locator('[data-test=deliverable-expiry-asks]');
  await expect(asks).toBeVisible({ timeout: 15_000 });
  await expect(asks.locator('[data-test=deliverable-expiry-text]')).toHaveText('#12 will expire tomorrow; it waits on your answer to question 3');
  await asks.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'needs-you-extend.png') });
  await asks.getByRole('button', { name: /Extend/ }).click();
  await expect.poll(() => sent.filter((s) => s.method === 'bus.deliverableExtend').map((s) => s.params)).toEqual([{ teamId: 1, deliverable: 12 }]);
  await expect(asks.locator('[data-test=deliverable-expiry-text]')).toHaveCount(0);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('Team settings: Expire idle deliverables after 3 days; saving 5 sends teams.deliverableExpiry { teamId, days: 5 }', async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser);
  await teams(page);
  await page.locator('[data-test=team-open-payments]').click();
  await page.locator('[data-test=open-manage]').first().click();
  const days = page.locator('[data-test=team-deliverable-expiry-days]');
  await expect(days).toHaveValue('3');
  await days.fill('5');
  await page.locator('[data-test=team-deliverable-expiry-save]').click();
  await expect(page.locator('[data-test=team-deliverable-expiry-saved]')).toBeVisible();
  expect(sent.filter((s) => s.method === 'teams.deliverableExpiry').at(-1)!.params).toEqual({ teamId: 1, days: 5 });
  await page.locator('[data-test=team-deliverable-expiry]').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'team-settings-expiry.png') });
  expect(errors).toEqual([]);
  await ctx.close();
});

test("a Visitor: the Deliverables page's pane has no Extend (nor any action)", async ({ browser }) => {
  const { page, ctx, sent } = await open(browser, 'read');
  await deliverablesPage(page);
  await page.locator('[data-test=deliverables-team-filter]').selectOption('2');
  await page.locator('[data-test=deliverables-row][data-wf="16"]').click();
  const pane = page.locator('[data-test=deliverable-pane]');
  await expect(pane).toHaveAttribute('data-id', '16');
  await expect(pane.locator('[data-test=deliverable-pane-status]')).toHaveText('Idle');
  await expect(pane.locator('[data-test=deliverable-pane-extend]')).toHaveCount(0);
  await expect(pane.locator('[data-test=deliverable-pane-actions]')).toHaveCount(0);
  expect(sent.some((s) => s.method === 'bus.deliverableExtend')).toBe(false);
  await ctx.close();
});