// The team Settings dialog on a phone, in a real browser (headless Chrome) against the FAKE core
// of phone-remote.spec.ts (remote.html, canned answers). At 390x844 (an iPhone) it is laid out like the app's Settings on
// a phone: it opens at the list of its nine sections (full-width rows, one line each, at least 44 px tall, a › chevron);
// a tap opens that section alone under a Back + title head; Back returns to the list. Nothing is wider than the screen.
// The sheet's Close keeps its 44 px tap target. At 1280x800 it is one row of tabs, one line each.
//
//   npx playwright test team-settings-phone      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import path from 'node:path';
import { mkdirSync } from 'node:fs';
import { answers } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';
import { settled } from './settle';

// The Settings dialog's own calls (not in the shared fake): two environment variables (one long), no integrations.
answers['teams.env'] = () => ({ API_BASE_URL: 'http://localhost:5173/api/v1/some/long/path/that/would/overflow', TEST_USER: 'tester@example.com' });
answers['integrations.list'] = () => ({ integrations: [] });
answers['integrations.mcpList'] = () => [];
answers['integrations.apiList'] = () => [];

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), process.env.C346_SHOTS || 'C346-screens');

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

const IDS = ['general', 'hiring', 'state', 'repos', 'env', 'network', 'integrations', 'reset', 'delete'];
const LABELS = ['General', 'Hiring', 'Pause and archive', 'Repositories', 'Environment', 'Network', 'Integrations', 'Reset', 'Delete'];
// Each section's own pane (a data-test inside it that only it has).
const PANES: Record<string, string> = {
  general: '[data-test=team-general]', hiring: '[data-test=team-hiring]', state: '[data-test=team-approvals]', repos: '[data-test=repo-main]', env: '[data-test=env-add]',
  network: '[data-test=team-network]', integrations: '[data-test=team-integrations]', reset: '[data-test=team-reset]', delete: '[data-test=team-delete-pane]',
};

async function openSettings(browser: Browser, w: number, h: number) {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 3 });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#team=1`);
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  // On a phone Settings is in the team page's ⋯ menu.
  const manage = page.locator('[data-test=open-manage]').first();
  if (!(await manage.isVisible().catch(() => false))) {
    await page.locator('[data-test=team-more]').click();
    await expect(page.locator('[data-test=team-more-menu]')).toBeVisible();
  }
  await manage.click();
  await expect(page.locator('[data-test=manage-tabs]')).toBeVisible();
  await settled(page);
  await expect(page.locator('[data-test=manage-error]')).toHaveCount(0);
  return { page, ctx, errors };
}

/** Each row's box and how many lines its text takes. */
const rowInfo = (page: Page) => page.evaluate(() => {
  const nav = document.querySelector<HTMLElement>('[data-test=manage-tabs]')!;
  return [...nav.querySelectorAll<HTMLElement>('button[data-test^=manage-tab-]')].map((b) => {
    const r = b.getBoundingClientRect();
    const range = document.createRange();
    range.selectNodeContents(b.firstChild!);
    const lines = new Set([...range.getClientRects()].map((q) => Math.round(q.top))).size;
    return { id: b.dataset.test!, text: (b.firstChild?.textContent ?? '').trim(), left: r.left, right: r.right, top: r.top, h: r.height, lines, chevron: !!b.querySelector('.go-icon') };
  });
});

/** Elements wider than the screen, or reaching past its right edge (scrollers' own content excluded: none expected). */
const overflow = (page: Page) => page.evaluate(() => {
  const vw = innerWidth, bad: string[] = [];
  const sheet = document.querySelector('[role=dialog]')!;
  for (const e of sheet.querySelectorAll<HTMLElement>('*')) {
    const r = e.getBoundingClientRect();
    if (r.width === 0) continue;
    if (r.width > vw + 0.5 || r.right > vw + 0.5 || r.left < -0.5) bad.push(`${e.tagName.toLowerCase()}.${e.className}[${e.dataset.test ?? ''}] ${Math.round(r.left)}..${Math.round(r.right)}`);
  }
  return { bad, docW: document.documentElement.scrollWidth, vw };
});

test('phone 390x844: a list of sections; a tap opens one under Back; Back returns to the list', async ({ browser }) => {
  const { page, ctx, errors } = await openSettings(browser, 390, 844);
  const root = page.locator('.manage');
  await expect(root).toHaveAttribute('data-phone-screen', 'list');

  // The sheet's Close: a 44 px tap target.
  const close = await page.locator('[role=dialog] header button').last().boundingBox();
  expect(close && Math.round(close.width)).toBe(44);
  expect(close && Math.round(close.height)).toBe(44);

  // The list: nine full-width rows in order, one line each, >= 44 px, a chevron; no pane under it.
  const rows = await rowInfo(page);
  expect(rows.map((r) => r.id)).toEqual(IDS.map((t) => `manage-tab-${t}`));
  expect(rows.map((r) => r.text)).toEqual(LABELS);
  for (const r of rows) {
    expect.soft(r.lines, `${r.id}: one line`).toBe(1);
    expect.soft(r.h, `${r.id}: at least 44 px tall`).toBeGreaterThanOrEqual(44);
    expect.soft(r.right - r.left, `${r.id}: full width`).toBeGreaterThan(330);
    expect.soft(r.chevron, `${r.id}: a chevron`).toBe(true);
  }
  for (let i = 1; i < rows.length; i++) expect.soft(rows[i]!.top, `${rows[i]!.id} under ${rows[i - 1]!.id}`).toBeGreaterThanOrEqual(rows[i - 1]!.top + rows[i - 1]!.h - 0.5);
  await expect(page.locator('[data-test=team-general]')).toHaveCount(0);
  expect((await overflow(page)).bad, 'list: nothing wider than the screen').toEqual([]);
  await page.screenshot({ path: path.join(SHOTS, 'team-settings-390x844-list.png') });

  // Each section: tap, its pane alone under Back + title, nothing too wide, Back to the list.
  for (let i = 0; i < IDS.length; i++) {
    const id = IDS[i]!;
    await page.locator(`[data-test=manage-tab-${id}]`).tap();
    await expect(root).toHaveAttribute('data-phone-screen', 'section');
    await expect(page.locator('[data-test=manage-tabs]')).toHaveCount(0);
    await expect(page.locator('[data-test=manage-section-title]')).toHaveText(LABELS[i]!);
    await expect(page.locator(PANES[id]!).first()).toBeVisible();
    const back = await page.locator('[data-test=manage-back]').boundingBox();
    expect.soft(back && back.height, `${id}: Back is a 44 px tap target`).toBeGreaterThanOrEqual(44);
    await settled(page);
    const o = await overflow(page);
    expect.soft(o.bad, `${id}: nothing wider than the screen`).toEqual([]);
    if (['general', 'env', 'delete'].includes(id)) await page.screenshot({ path: path.join(SHOTS, `team-settings-390x844-${id}.png`) });
    await page.locator('[data-test=manage-back]').tap();
    await expect(root).toHaveAttribute('data-phone-screen', 'list');
    await expect(page.locator('[data-test=manage-tabs]')).toBeVisible();
  }

  // General's "Repositories tab" link opens that section.
  await page.locator('[data-test=manage-tab-general]').tap();
  await page.locator('[data-test=team-general] a').tap();
  await expect(page.locator('[data-test=manage-section-title]')).toHaveText('Repositories');
  await expect(page.locator('[data-test=repo-main]')).toBeVisible();

  // A long section: the head stays at the top of the sheet as it scrolls.
  const body = page.locator('[role=dialog] .body');
  const headTop = async () => (await page.locator('.section-head').boundingBox())!.y;
  const before = await headTop();
  await body.evaluate((b) => { b.scrollTop = b.scrollHeight; });
  await settled(page);
  const scrolled = await body.evaluate((b) => b.scrollTop);
  if (scrolled > 20) expect(Math.abs((await headTop()) - before), 'the head stays put as the section scrolls').toBeLessThanOrEqual(13);

  expect(errors).toEqual([]);
  await ctx.close();
});

test('desktop 1280x800: the tabs are one row, one line each, no list', async ({ browser }) => {
  const { page, ctx, errors } = await openSettings(browser, 1280, 800);
  await page.screenshot({ path: path.join(SHOTS, 'team-settings-1280x800.png') });
  await expect(page.locator('.manage')).not.toHaveAttribute('data-phone-screen', /.*/);
  await expect(page.locator('[data-test=manage-back]')).toHaveCount(0);
  await expect(page.locator('[data-test=team-general]')).toBeVisible();
  const rows = await rowInfo(page);
  expect(rows.map((r) => r.id)).toEqual(IDS.map((t) => `manage-tab-${t}`));
  for (const r of rows) {
    expect.soft(r.lines, `${r.id}: one line`).toBe(1);
    expect.soft(r.chevron, `${r.id}: no chevron`).toBe(false);
    expect.soft(Math.round(r.top), `${r.id}: on the one row`).toBe(Math.round(rows[0]!.top));
  }
  const nav = await page.locator('[data-test=manage-tabs]').evaluate((e) => { const cs = getComputedStyle(e); return { dir: cs.flexDirection, ov: cs.overflowX }; });
  expect(nav).toEqual({ dir: 'row', ov: 'visible' });
  await expect(page.locator('[data-test=manage-tab-general]')).toHaveClass(/\bon\b/);
  await page.locator('[data-test=manage-tab-delete]').click();
  await expect(page.locator('[data-test=team-delete-pane]')).toBeVisible();
  await expect(page.locator('[data-test=manage-tabs]')).toBeVisible();
  expect(errors).toEqual([]);
  await ctx.close();
});
