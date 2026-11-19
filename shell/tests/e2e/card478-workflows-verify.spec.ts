// Card 478 (B104 verify, TesterKofi): the Workflows page in a real browser (headless Chrome) on remote.html, the remote
// view, with the phone-remote FAKE core whose workflows.list / workflows.get answer the REAL core's answers
// (tests/unit/fixtures/card478, written by core Card478B104VerifyTests: live running / lease / slot / needs-you states,
// a dispatched item with its spec, an archived item, an expired workflow). At 1280, 1440 and 375 px, light and dark,
// with full access and as a Visitor (read-only). Screenshots go to $YAWBLE_TEAM_DOCS/B104/verify.
//
//   npx playwright test card478-workflows-verify      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser, type Page } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { answers, type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);
test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const fx = (name: string) => JSON.parse(readFileSync(path.join(__dirname, '..', 'unit', 'fixtures', 'card478', `${name}.json`), 'utf8'));
const ID = fx('ids') as Record<string, number | string>;
const LIST = { all: fx('list-all'), open: fx('list-open') };
const GETS = ['get-running', 'get-dispatched', 'get-needs-you', 'get-closed-archived'].map(fx);
const n = (k: string) => Number(ID[k]);

// The real core's answers in place of the fake's canned ones (this worker only).
answers['workflows.list'] = (p) => structuredClone(String(p?.state ?? 'all').toLowerCase() === 'open' ? LIST.open : LIST.all);
answers['workflows.get'] = (p) => {
  const g = GETS.find((d) => d.workflow.id === Number(p?.workflow));
  if (!g) throw Object.assign(new Error('no such workflow'), { code: 'no_such_workflow' });
  return structuredClone(g);
};

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'B104', 'verify') : null;
if (shots) mkdirSync(shots, { recursive: true });

async function open(browser: Browser, access: Access, w: number, h: number, theme: 'light' | 'dark', hash = '#workflows') {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  s.core.setAccess(access);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: theme, reducedMotion: 'reduce' });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}${hash}`);
  await expect(page.locator('[data-test=workflows-view]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test=workflows-row]').first()).toBeVisible();
  return { s, page, ctx, errors };
}
const noSideScroll = (page: Page) => page.evaluate(() => document.scrollingElement!.scrollWidth <= window.innerWidth + 1);
const row = (page: Page, id: number) => page.locator(`[data-test=workflows-row][data-wf="${id}"]`);

const sizes = [[1280, 800], [1440, 900], [375, 740]] as const;
for (const access of ['full', 'read'] as const) {
  for (const [w, h] of sizes) {
    for (const theme of ['light', 'dark'] as const) {
      const who = access === 'read' ? 'visitor' : 'full';
      test(`${who} ${w}px ${theme}: the real core's rows, the chips, the pane`, async ({ browser }) => {
        const { s, page, ctx, errors } = await open(browser, access, w, h, theme);
        const tag = `${who}-${w}-${theme}`;
        // Open: needs you (Dee), running (the Manager's), then the two waiting ones.
        await expect(page.locator('[data-test=workflows-row]')).toHaveCount(4);
        await expect(page.locator('[data-test=workflows-row]').nth(0)).toHaveAttribute('data-wf', String(n('dee')));
        await expect(page.locator('[data-test=workflows-row]').nth(1)).toHaveAttribute('data-wf', String(n('lead')));
        await expect(row(page, n('cy')).locator('[data-test=workflows-status]')).toHaveText('Waiting');
        await expect(row(page, n('lead')).locator('[data-test=workflow-status-icon]')).toHaveAttribute('data-animated', 'false'); // reduced motion
        await expect(page.locator('[data-test=workflows-chip-open] [data-test=workflows-chip-count]')).toHaveText('4');
        await expect(page.locator('[data-test=workflows-summary]').first()).toContainText('4 open');
        expect(await noSideScroll(page), 'the page never scrolls sideways').toBe(true);
        if (shots) await page.screenshot({ path: path.join(shots, `c478-list-${tag}.png`) });

        // All: the ended ones after; the item labels link (the core said backlog: true).
        await page.locator('[data-test=workflows-chip-all]').click();
        await expect(page.locator('[data-test=workflows-row]')).toHaveCount(7);
        await expect(row(page, n('dispatched')).locator('[data-test=workflows-title]')).toHaveText('Workflows page');
        await expect(row(page, n('dispatched')).locator('[data-test=workflow-item-link]')).toHaveText(String(ID.spec));
        expect(await noSideScroll(page)).toBe(true);
        if (shots) await page.screenshot({ path: path.join(shots, `c478-all-${tag}.png`) });

        // The running workflow's pane: cards by lane, activity; Stop and Close only with full access.
        await row(page, n('lead')).click();
        const pane = page.locator('[data-test=workflow-pane]');
        await expect(pane).toBeVisible();
        await expect(pane.locator('[data-test=workflow-pane-title]')).toHaveText('lead it');
        await expect(pane.locator('[data-test=workflow-pane-card]')).toHaveCount(2);
        await expect(pane.locator('[data-test=workflow-pane-message]').first()).toHaveAttribute('data-kind', 'progress');
        if (access === 'read') await expect(pane.locator('[data-test=workflow-pane-actions]')).toHaveCount(0);
        else {
          await expect(pane.locator('[data-test=workflow-pane-stop]')).toBeVisible();
          await expect(pane.locator('[data-test=workflow-pane-nudge]')).toHaveCount(0);
        }
        if (w >= 760) {
          // The pane sits beside the table, like the Backlog's item pane; the table stays in sight.
          const t = await page.locator('[data-test=workflows-listwrap]').boundingBox();
          const p = await pane.boundingBox();
          expect(t && p && p.x >= t.x + t.width - 1).toBe(true);
          await expect(row(page, n('lead'))).toBeVisible();
        } else {
          await expect(page.locator('[data-test=workflows-listwrap]')).toBeHidden();
        }
        expect(await noSideScroll(page)).toBe(true);
        if (shots) await page.screenshot({ path: path.join(shots, `c478-pane-running-${tag}.png`) });

        // The dispatched one: its spec from the core.
        if (w < 760) { await pane.locator('[data-test=workflow-pane-back]').click(); await expect(page.locator('[data-test=workflows-listwrap]')).toBeVisible(); }
        await row(page, n('dispatched')).click();
        await expect(pane.locator('[data-test=workflow-pane-spec]')).toContainText("every team's work in one place");
        await expect(pane.locator('[data-test=workflow-pane-actions]')).toHaveCount(0); // ended: none
        if (shots) await page.screenshot({ path: path.join(shots, `c478-pane-spec-${tag}.png`) });

        // Esc (desktop) or Back (phone) returns to the list.
        if (w < 760) await pane.locator('[data-test=workflow-pane-back]').click();
        else await page.keyboard.press('Escape');
        await expect(pane).toHaveCount(0);
        expect(s.core.unknown.has('workflows.list') || s.core.unknown.has('workflows.get')).toBe(false);
        expect(errors).toEqual([]);
        await ctx.close();
      });
    }
  }
}

test('1280 light: the Timeline icon opens the Timeline dialog; an Item link opens the Backlog', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 'full', 1280, 800, 'light');
  await row(page, n('lead')).locator('[data-test=workflows-timeline]').click();
  await expect(page.locator('[aria-modal="true"]')).toBeVisible();
  if (shots) await page.screenshot({ path: path.join(shots, 'c478-timeline-full-1280-light.png') });
  await page.keyboard.press('Escape');
  await expect(page.locator('[aria-modal="true"]')).toHaveCount(0);
  await page.locator('[data-test=workflows-chip-all]').click();
  await row(page, n('dispatched')).locator('[data-test=workflow-item-link]').click();
  await expect(page.locator('[data-test=workflows-view]')).toHaveCount(0);
  await expect(page.locator('[data-test=nav-backlog]')).toHaveClass(/on/);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('1280 dark: the board\'s Workflows button opens the page', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 'full', 1280, 800, 'dark');
  await page.locator('[data-test=nav-board]').click();
  await expect(page.locator('[data-test=board-workflows]')).toBeVisible({ timeout: 15_000 });
  const bb = await page.locator('[data-test=board-backlog]').boundingBox();
  const wb = await page.locator('[data-test=board-workflows]').boundingBox();
  expect(bb && wb && Math.abs(bb.y - wb.y) < 4 && wb.x > bb.x).toBe(true); // beside Backlog
  if (shots) await page.screenshot({ path: path.join(shots, 'c478-board-button-full-1280-dark.png') });
  await page.locator('[data-test=board-workflows]').click();
  await expect(page.locator('[data-test=workflows-view]')).toBeVisible();
  await expect(page.locator('[data-test=nav-workflows]')).toHaveClass(/on/);
  expect(errors).toEqual([]);
  await ctx.close();
});
