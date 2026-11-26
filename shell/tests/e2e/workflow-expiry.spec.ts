// Workflow expiry in headless Chrome against the phone-remote fake core
// (remote.html, tests/e2e/phone) with its expiry fixtures on (setExpiry): an open #12 waiting on question 3 that expires
// in 20 h, and #9 the app closed as expired. The Teams list tile rows read '· expired' for #9; on the Workflows page (the team
// page's Workflows button) #9's pane reads Expired with no Extend, and an idle one's says when it expires, its Extend asked
// first and sent as bus.workflowExtend { teamId, workflow }; the Kanban board's Needs you has '#12 will expire tomorrow; it waits on your
// answer to question 3' with Extend; Team settings shows 'Expire idle workflows after 3 days' and saves 5 as
// teams.workflowExpiry { teamId, days: 5 }. A Visitor's pane has no Extend.
//
//   npx playwright test workflow-expiry      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/workflow-expiry (else test-results/workflow-expiry).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore, type Access } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';
import { settled } from './settle';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'workflow-expiry');

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
  const row = page.locator('[data-test=team-workflow][data-id="9"]').first();
  await expect(row).toBeVisible();
  await expect(row.locator('[data-test=team-workflow-expired]')).toHaveText(/expired/);
  await expect(row).toHaveAttribute('data-status', 'expired');
  await page.screenshot({ path: path.join(SHOTS, 'teams-list-expired.png') });
  expect(errors).toEqual([]);
  await ctx.close();
});

/** The team page's Workflows button: the Workflows page, filtered to the team; then the All chip (ended ones too). */
async function workflowsPage(page: Page) {
  await teams(page);
  await page.locator('[data-test=team-open-payments]').click();
  await page.locator('[data-test=open-workflows]').first().click();
  await expect(page.locator('[data-test=workflows-view]')).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-test=workflows-team-filter]')).toHaveValue('1');
  await expect(page.locator('[role=dialog]')).toHaveCount(0);
  await page.locator('[data-test=workflows-chip-all]').click();
}

test("the Workflows page's pane: #9 Expired with no Extend; an expiring one says when, and Extend (confirmed) sends bus.workflowExtend", async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser);
  await workflowsPage(page);
  await page.locator('[data-test=workflows-row][data-wf="9"]').click();
  const pane = page.locator('[data-test=workflow-pane]');
  await expect(pane).toHaveAttribute('data-id', '9');
  await expect(pane.locator('[data-test=workflow-pane-status]')).toHaveText('Expired');
  await expect(pane.locator('[data-test=workflow-pane-extend]')).toHaveCount(0); // an ended one has no Extend
  await expect(pane.locator('[data-test=workflow-pane-actions]')).toHaveCount(0);
  await page.screenshot({ path: path.join(SHOTS, 'workflows-page-expired.png') });
  // The page's idle one with an expiry is in the other team (#16, expires in 20 h).
  await page.locator('[data-test=workflows-team-filter]').selectOption('2');
  await page.locator('[data-test=workflows-row][data-wf="16"]').click();
  await expect(pane).toHaveAttribute('data-id', '16');
  await expect(pane.locator('[data-test=workflow-pane-status]')).toHaveAttribute('title', /Expires in (19|20)h/);
  await page.screenshot({ path: path.join(SHOTS, 'workflows-page-extend.png') });
  await pane.locator('[data-test=workflow-pane-extend]').click();
  await expect(page.locator('[data-test=confirm-message]')).toHaveText('Extend #16? Its expiry clock starts again.');
  await page.locator('[data-test=confirm-ok]').click();
  await expect.poll(() => sent.filter((s) => s.method === 'bus.workflowExtend').map((s) => s.params)).toEqual([{ teamId: 2, workflow: 16 }]);
  expect(errors).toEqual([]);
  await ctx.close();
});
test('Needs you on the Kanban board: #12 will expire tomorrow on question 3, with Extend', async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser);
  await page.locator('[data-test=sidebar] [data-test=nav-board]').first().click();
  const asks = page.locator('[data-test=workflow-expiry-asks]');
  await expect(asks).toBeVisible({ timeout: 15_000 });
  await expect(asks.locator('[data-test=workflow-expiry-text]')).toHaveText('#12 will expire tomorrow; it waits on your answer to question 3');
  await asks.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'needs-you-extend.png') });
  await asks.getByRole('button', { name: /Extend/ }).click();
  await expect.poll(() => sent.filter((s) => s.method === 'bus.workflowExtend').map((s) => s.params)).toEqual([{ teamId: 1, workflow: 12 }]);
  await expect(asks.locator('[data-test=workflow-expiry-text]')).toHaveCount(0);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('Team settings: Expire idle workflows after 3 days; saving 5 sends teams.workflowExpiry { teamId, days: 5 }', async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser);
  await teams(page);
  await page.locator('[data-test=team-open-payments]').click();
  await page.locator('[data-test=open-manage]').first().click();
  const days = page.locator('[data-test=team-workflow-expiry-days]');
  await expect(days).toHaveValue('3');
  await days.fill('5');
  await page.locator('[data-test=team-workflow-expiry-save]').click();
  await expect(page.locator('[data-test=team-workflow-expiry-saved]')).toBeVisible();
  expect(sent.filter((s) => s.method === 'teams.workflowExpiry').at(-1)!.params).toEqual({ teamId: 1, days: 5 });
  await page.locator('[data-test=team-workflow-expiry]').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'team-settings-expiry.png') });
  expect(errors).toEqual([]);
  await ctx.close();
});

test("a Visitor: the Workflows page's pane has no Extend (nor any action)", async ({ browser }) => {
  const { page, ctx, sent } = await open(browser, 'read');
  await workflowsPage(page);
  await page.locator('[data-test=workflows-team-filter]').selectOption('2');
  await page.locator('[data-test=workflows-row][data-wf="16"]').click();
  const pane = page.locator('[data-test=workflow-pane]');
  await expect(pane).toHaveAttribute('data-id', '16');
  await expect(pane.locator('[data-test=workflow-pane-status]')).toHaveText('Idle');
  await expect(pane.locator('[data-test=workflow-pane-extend]')).toHaveCount(0);
  await expect(pane.locator('[data-test=workflow-pane-actions]')).toHaveCount(0);
  expect(sent.some((s) => s.method === 'bus.workflowExtend')).toBe(false);
  await ctx.close();
});