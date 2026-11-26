// The Deliverables page in a real browser (headless Chrome) on remote.html, the remote
// view, with the phone-remote FAKE core whose deliverables.list / deliverables.get answer the REAL core's answers
// (tests/unit/fixtures/deliverables, written by the core's deliverables verify test: live running / lease / slot / needs-you states,
// a dispatched item with its spec, an archived item, an expired deliverable). At 1280, 1440 and 375 px, light and dark,
// with full access and as a Visitor (read-only). Screenshots go to $YAWBLE_TEAM_DOCS/deliverables-page/verify.
//
//   npx playwright test deliverables-page-verify      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { mkdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { answers, type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);
test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const fx = (name: string) => JSON.parse(readFileSync(path.join(__dirname, '..', 'unit', 'fixtures', 'deliverables', `${name}.json`), 'utf8'));
const ID = fx('ids') as Record<string, number | string>;
const LIST = { all: fx('list-all'), open: fx('list-open') };
const GETS = ['get-running', 'get-dispatched', 'get-needs-you', 'get-closed-archived'].map(fx);
const n = (k: string) => Number(ID[k]);

// The real core's answers in place of the fake's canned ones (this worker only).
answers['deliverables.list'] = (p) => structuredClone(String(p?.state ?? 'all').toLowerCase() === 'open' ? LIST.open : LIST.all);
answers['deliverables.get'] = (p) => {
  const g = GETS.find((d) => d.deliverable.id === Number(p?.deliverable));
  if (!g) throw Object.assign(new Error('no such deliverable'), { code: 'no_such_deliverable' });
  return structuredClone(g);
};

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'deliverables-page', 'verify') : null;
if (shots) mkdirSync(shots, { recursive: true });

async function open(browser: Browser, access: Access, w: number, h: number, theme: 'light' | 'dark', hash = '#deliverables') {
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
  await expect(page.locator('[data-test=deliverables-view]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test=deliverables-row]').first()).toBeVisible();
  return { s, page, ctx, errors };
}
const noSideScroll = (page: Page) => page.evaluate(() => document.scrollingElement!.scrollWidth <= window.innerWidth + 1);
const row = (page: Page, id: number) => page.locator(`[data-test=deliverables-row][data-wf="${id}"]`);

const sizes = [[1280, 800], [1440, 900], [375, 740]] as const;
for (const access of ['full', 'read'] as const) {
  for (const [w, h] of sizes) {
    for (const theme of ['light', 'dark'] as const) {
      const who = access === 'read' ? 'visitor' : 'full';
      test(`${who} ${w}px ${theme}: the real core's rows, the chips, the pane`, async ({ browser }) => {
        const { s, page, ctx, errors } = await open(browser, access, w, h, theme);
        const tag = `${who}-${w}-${theme}`;
        // Open: needs you (Dee), running (the Manager's), then the two waiting ones.
        await expect(page.locator('[data-test=deliverables-row]')).toHaveCount(4);
        await expect(page.locator('[data-test=deliverables-row]').nth(0)).toHaveAttribute('data-wf', String(n('dee')));
        await expect(page.locator('[data-test=deliverables-row]').nth(1)).toHaveAttribute('data-wf', String(n('lead')));
        await expect(row(page, n('cy')).locator('[data-test=deliverables-status]')).toHaveText('Waiting');
        await expect(row(page, n('lead')).locator('[data-test=deliverable-status-icon]')).toHaveAttribute('data-animated', 'false'); // reduced motion
        await expect(page.locator('[data-test=deliverables-chip-open] [data-test=deliverables-chip-count]')).toHaveText('4');
        await expect(page.locator('[data-test=deliverables-summary]').first()).toContainText('4 open');
        expect(await noSideScroll(page), 'the page never scrolls sideways').toBe(true);
        if (shots) await page.screenshot({ path: path.join(shots, `c478-list-${tag}.png`) });

        // All: the ended ones after; the item labels link (the core said backlog: true).
        await page.locator('[data-test=deliverables-chip-all]').click();
        await expect(page.locator('[data-test=deliverables-row]')).toHaveCount(7);
        await expect(row(page, n('dispatched')).locator('[data-test=deliverables-title]')).toHaveText('Deliverables page');
        await expect(row(page, n('dispatched')).locator('[data-test=deliverable-item-link]')).toHaveText(String(ID.spec));
        expect(await noSideScroll(page)).toBe(true);
        if (shots) await page.screenshot({ path: path.join(shots, `c478-all-${tag}.png`) });

        // The running deliverable's pane: cards by lane, activity; Stop and Close only with full access.
        await row(page, n('lead')).click();
        const pane = page.locator('[data-test=deliverable-pane]');
        await expect(pane).toBeVisible();
        await expect(pane.locator('[data-test=deliverable-pane-title]')).toHaveText('lead it');
        await expect(pane.locator('[data-test=deliverable-pane-card]')).toHaveCount(2);
        await expect(pane.locator('[data-test=deliverable-pane-message]').first()).toHaveAttribute('data-kind', 'progress');
        if (access === 'read') await expect(pane.locator('[data-test=deliverable-pane-actions]')).toHaveCount(0);
        else {
          await expect(pane.locator('[data-test=deliverable-pane-stop]')).toBeVisible();
          await expect(pane.locator('[data-test=deliverable-pane-nudge]')).toHaveCount(0);
        }
        if (w >= 760) {
          // The pane sits beside the table, like the Backlog's item pane; the table stays in sight.
          const t = await page.locator('[data-test=deliverables-listwrap]').boundingBox();
          const p = await pane.boundingBox();
          expect(t && p && p.x >= t.x + t.width - 1).toBe(true);
          await expect(row(page, n('lead'))).toBeVisible();
        } else {
          await expect(page.locator('[data-test=deliverables-listwrap]')).toBeHidden();
        }
        expect(await noSideScroll(page)).toBe(true);
        if (shots) await page.screenshot({ path: path.join(shots, `c478-pane-running-${tag}.png`) });

        // The dispatched one: its spec from the core.
        if (w < 760) { await pane.locator('[data-test=deliverable-pane-back]').click(); await expect(page.locator('[data-test=deliverables-listwrap]')).toBeVisible(); }
        await row(page, n('dispatched')).click();
        await expect(pane.locator('[data-test=deliverable-pane-spec]')).toContainText("every team's work in one place");
        await expect(pane.locator('[data-test=deliverable-pane-actions]')).toHaveCount(0); // ended: none
        if (shots) await page.screenshot({ path: path.join(shots, `c478-pane-spec-${tag}.png`) });

        // Esc (desktop) or Back (phone) returns to the list.
        if (w < 760) await pane.locator('[data-test=deliverable-pane-back]').click();
        else await page.keyboard.press('Escape');
        await expect(pane).toHaveCount(0);
        expect(s.core.unknown.has('deliverables.list') || s.core.unknown.has('deliverables.get')).toBe(false);
        expect(errors).toEqual([]);
        await ctx.close();
      });
    }
  }
}

test('1280 light: the Timeline icon opens the Timeline dialog; an Item link opens the Backlog', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 'full', 1280, 800, 'light');
  await row(page, n('lead')).locator('[data-test=deliverables-timeline]').click();
  await expect(page.locator('[aria-modal="true"]')).toBeVisible();
  if (shots) await page.screenshot({ path: path.join(shots, 'c478-timeline-full-1280-light.png') });
  await page.keyboard.press('Escape');
  await expect(page.locator('[aria-modal="true"]')).toHaveCount(0);
  await page.locator('[data-test=deliverables-chip-all]').click();
  await row(page, n('dispatched')).locator('[data-test=deliverable-item-link]').click();
  await expect(page.locator('[data-test=deliverables-view]')).toHaveCount(0);
  await expect(page.locator('[data-test=nav-backlog]')).toHaveClass(/on/);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('1280 dark: the board\'s Deliverables button opens the page', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 'full', 1280, 800, 'dark');
  await page.locator('[data-test=nav-board]').click();
  await expect(page.locator('[data-test=board-deliverables]')).toBeVisible({ timeout: 15_000 });
  const bb = await page.locator('[data-test=board-backlog]').boundingBox();
  const wb = await page.locator('[data-test=board-deliverables]').boundingBox();
  expect(bb && wb && Math.abs(bb.y - wb.y) < 4 && wb.x > bb.x).toBe(true); // beside Backlog
  if (shots) await page.screenshot({ path: path.join(shots, 'c478-board-button-full-1280-dark.png') });
  await page.locator('[data-test=board-deliverables]').click();
  await expect(page.locator('[data-test=deliverables-view]')).toBeVisible();
  await expect(page.locator('[data-test=nav-deliverables]')).toHaveClass(/on/);
  expect(errors).toEqual([]);
  await ctx.close();
});

// The Backlog answers the two items the rows point at (one live, one archived), so an Item link can land on its item.
const bItem = (id: number, label: string, archivedAt: string | null) => ({
  id, label, team: null, title: label === String(ID.spec) ? 'Deliverables page' : 'Old item', body: '# x', state: archivedAt ? 'done' : 'declared', archivedAt,
  dispatchedTeam: null, dispatchedDeliverable: null, dispatchedAt: null, inFlight: false, createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-02T00:00:00Z', dispatchCount: 1,
});
const B_LIVE = [bItem(901, String(ID.spec), null)];
const B_ARCH = [bItem(902, String(ID.old), '2026-11-03T00:00:00Z')];

for (const theme of ['light', 'dark'] as const) {
  test(`re-check 1280 ${theme}: F1 the Item column marks the match; the archived link opens the archive`, async ({ browser }) => {
    answers['backlog.list'] = (p) => structuredClone(p?.archived ? B_ARCH : B_LIVE);
    answers['backlog.item'] = (p) => ({ ...[...B_LIVE, ...B_ARCH].find((b) => b.id === Number(p?.id))!, dispatches: [] });
    const { page, ctx, errors } = await open(browser, 'full', 1280, 800, theme);
    await page.locator('[data-test=deliverables-chip-all]').click();
    const search = page.locator('[data-test=deliverables-search]');
    const link = (id: number) => row(page, id).locator('td.itm [data-test=deliverable-item-link]');

    // The full label.
    await search.fill(String(ID.spec));
    await expect(link(n('dispatched')).locator('mark')).toHaveText(String(ID.spec));
    await expect(link(n('dispatched'))).toHaveText(String(ID.spec));
    // Same look as the title cell's mark (the ink is inherited in both rules: the link's blue stays blue).
    const look = (sel: string) => page.locator(sel).first().evaluate((e) => { const s = getComputedStyle(e); return [s.backgroundColor, s.boxShadow, s.borderRadius, s.paddingLeft].join('|'); });
    await search.fill('deliverables');
    const titleLook = await look('[data-test=deliverables-hit]');
    await search.fill(String(ID.spec));
    expect(await look(`[data-test=deliverables-row][data-wf="${n('dispatched')}"] td.itm mark`)).toBe(titleLook);
    if (shots) await page.screenshot({ path: path.join(shots, `c478r-f1-full-${theme}.png`) });

    // A partial match: only 001 is marked.
    await search.fill('001');
    await expect(link(n('dispatched')).locator('mark')).toHaveText('001');
    await expect(link(n('dispatched'))).toHaveText(String(ID.spec));
    if (shots) await page.screenshot({ path: path.join(shots, `c478r-f1-partial-${theme}.png`) });

    // The pane (another ItemLink user) is not marked while the row is.
    await row(page, n('dispatched')).click();
    const pane = page.locator('[data-test=deliverable-pane]');
    await expect(pane.locator('[data-test=deliverable-item-link]')).toHaveText(String(ID.spec));
    await expect(pane.locator('[data-test=deliverable-item-link] mark')).toHaveCount(0);
    await page.keyboard.press('Escape');

    // The archived item, lower case: marked, and the link opens the Backlog's archive at it.
    await search.fill(String(ID.old).toLowerCase());
    await expect(link(n('archived')).locator('mark')).toHaveText(String(ID.old));
    if (shots) await page.screenshot({ path: path.join(shots, `c478r-f1-archived-${theme}.png`) });
    await link(n('archived')).click();
    await expect(page.locator('[data-test=deliverables-view]')).toHaveCount(0);
    await expect(page.locator('[data-test=backlog-tab-archived]').first()).toHaveClass(/on/);
    await expect(page.locator('[data-test=backlog-row][aria-selected=true]')).toContainText(String(ID.old));
    if (shots) await page.screenshot({ path: path.join(shots, `c478r-f1-archive-opened-${theme}.png`) });
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

for (const [w, h] of [[1280, 800], [375, 740]] as const) {
  test(`re-check ${w}px: O2 Clear is hidden at the defaults and puts them back; Show all deliverables goes to All`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, 'full', w, h, 'light');
    const clear = page.locator('[data-test=deliverables-clear]');
    const chip = (c: string) => page.locator(`[data-test=deliverables-chip-${c}]`);
    await expect(chip('open')).toHaveAttribute('aria-selected', 'true');
    await expect(clear).toHaveCount(0);
    await chip('all').click();
    await expect(clear).toBeVisible();
    await page.locator('[data-test=deliverables-team-filter]').selectOption(String(n('web')));
    await page.locator('[data-test=deliverables-search]').fill('lead');
    if (shots) await page.screenshot({ path: path.join(shots, `c478r-o2-filtered-${w}.png`) });
    await clear.click();
    await expect(chip('open')).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('[data-test=deliverables-search]')).toHaveValue('');
    await expect(page.locator('[data-test=deliverables-team-filter]')).toHaveValue('');
    await expect(clear).toHaveCount(0);
    await expect(page.locator('[data-test=deliverables-row]')).toHaveCount(4);
    // Nothing matches: the empty state's button goes to All.
    await page.locator('[data-test=deliverables-search]').fill('zzz-no-such');
    await page.locator('[data-test=deliverables-empty-clear]').click();
    await expect(chip('all')).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('[data-test=deliverables-search]')).toHaveValue('');
    await expect(page.locator('[data-test=deliverables-row]')).toHaveCount(7);
    await expect(clear).toBeVisible();
    expect(await noSideScroll(page)).toBe(true);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}