// Card 347 (verify card 346): gap tests for team Settings on a phone (a list of sections, one section under a sticky
// Back + title) and the phone sheet's Close, in headless Chrome against the FAKE core of phone-remote.spec.ts.
// Replaces card329-team-settings-gaps (the sideways-scrolling tab row it tested is gone).
//  - 360x740 and 390x844: the list's eight rows are one line, >= 44 px, full width, label not cut; every section opens
//    alone under the head with its title; in the list and in every section nothing in the dialog is wider than the
//    screen, neither the page nor the sheet's body scrolls sideways (long env names/values, long repository paths).
//  - Back returns to the list from every section and from General's Repositories link; the head stays at the top of the
//    sheet (and is what a tap hits) while a long section scrolls; Back still works after the scroll; the next section
//    starts at its top.
//  - Other phone sheets built on Modal (Settings, Triggers, Git, Documents, Workflows, Logs): Close is 44x44, shown, an
//    18 px glyph inside its box, the title is not clipped, and Close closes the sheet (kept from card 329).
//  - 1280x800: the Settings tab row and the Modal Close (Settings and Triggers dialogs) have the same boxes and styles as
//    main BASE_COMMIT (before cards 328 and 346); both screenshots are saved (kept from card 329).
//
//   npx playwright test card347-team-settings-gaps   (B037_SKIP_BUILD=1 reuses out/b037/renderer and renderer-base)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import path from 'node:path';
import { existsSync, mkdirSync } from 'node:fs';
import { answers } from './phone/fake-core';
import { BASE_COMMIT, baseBuiltDir, baseShellDir, buildRenderer, builtDir, extractBaseTree, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(120_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), process.env.C347_SHOTS || 'C347-verify');
const IDS = ['general', 'state', 'repos', 'env', 'network', 'integrations', 'reset', 'delete'];
const LABELS = ['General', 'Pause and archive', 'Repositories', 'Environment', 'Network', 'Integrations', 'Reset', 'Delete'];
const PANES: Record<string, string> = {
  general: '[data-test=team-general]', state: '[data-test=team-approvals]', repos: '[data-test=repo-main]', env: '[data-test=env-add]',
  network: '[data-test=team-network]', integrations: '[data-test=team-integrations]', reset: '[data-test=team-reset]', delete: '[data-test=team-delete-pane]',
};

// Long content for the overflow checks; set for this file only and put back after it (the fake core is shared by the
// worker's other spec files).
const LONG = 'C:/Users/someone-with-a-long-name/AppData/Local/Yawble/teams/Payments-and-refunds-team/repos/payments-service-backend';
const env: Record<string, string> = { API_BASE_URL: 'http://localhost:5173/api/v1/some/long/path/that/would/overflow/the/screen/without/wrapping' };
for (let i = 0; i < 24; i++) env[`TEST_VARIABLE_WITH_A_LONG_NAME_${i}`] = `value-${i}-${'x'.repeat(60)}`;
const OVERRIDES: Record<string, (p?: any) => unknown> = {
  'teams.env': () => env,
  'teams.repos': () => ({
    main: { name: 'payments', path: LONG, baseBranch: 'main', origin: 'https://git.example.com/some-organisation/payments-service-backend-with-a-long-name.git', managed: false },
    extras: [{ name: 'docs-site', path: `${LONG}-docs-site-reference`, baseBranch: 'main', writable: false, source: 'https://git.example.com/some-organisation/docs-site.git' }],
  }),
  'integrations.list': () => ({ integrations: [] }),
  'integrations.mcpList': () => [],
  'integrations.apiList': () => [],
};
const saved: Record<string, unknown> = {};

test.beforeAll(async () => {
  test.setTimeout(900_000);
  for (const k of Object.keys(OVERRIDES)) { saved[k] = (answers as any)[k]; (answers as any)[k] = OVERRIDES[k]; }
  mkdirSync(SHOTS, { recursive: true });
  await buildRenderer();
  if (!(process.env.B037_SKIP_BUILD === '1' && existsSync(path.join(baseBuiltDir, 'remote.html')))) { extractBaseTree(); await buildRenderer(baseShellDir, baseBuiltDir); }
});
test.afterAll(() => { for (const k of Object.keys(saved)) { if (saved[k] === undefined) delete (answers as any)[k]; else (answers as any)[k] = saved[k]; } });

async function openTeam(browser: Browser, w: number, h: number, dir = builtDir) {
  const s = fakeRemote(dir);
  s.core.setAsks(false);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 2 });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#team=1`);
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  return { page, ctx, errors };
}

/** Clicks a team-header opener, through the phone ⋯ menu when it is not shown in the header. */
async function openFromTeam(page: Page, id: string) {
  const b = page.locator(`[data-test=${id}]`).first();
  if (!(await b.isVisible().catch(() => false))) {
    await page.locator('[data-test=team-more]').click();
    await expect(page.locator('[data-test=team-more-menu]')).toBeVisible();
  }
  await b.click();
  await expect(page.locator('[role=dialog]').last()).toBeVisible();
  await page.waitForTimeout(300);
}

const rowInfo = (page: Page) => page.evaluate(() => {
  const nav = document.querySelector<HTMLElement>('[data-test=manage-tabs]')!;
  return { navW: nav.clientWidth, rows: [...nav.querySelectorAll<HTMLElement>('button[data-test^=manage-tab-]')].map((b) => {
    const r = b.getBoundingClientRect(), range = document.createRange();
    // The label's text node only: the chevron is its own flex item, centred at another height.
    range.selectNodeContents(b.firstChild!);
    return { id: b.dataset.test!.replace('manage-tab-', ''), text: b.firstChild?.textContent?.trim() ?? '', top: r.top, w: r.width, h: r.height,
      lines: new Set([...range.getClientRects()].filter((q) => q.width > 0).map((q) => Math.round(q.top))).size, fits: b.scrollWidth <= b.clientWidth + 1,
      chevron: !!b.querySelector('.go-icon') };
  }) };
});

/** Everything in the dialog that sticks out of the screen, and whether the page or the sheet's body scrolls sideways
 *  (both measured and tried). */
const overflow = (page: Page) => page.evaluate(() => {
  const vw = innerWidth, dlg = [...document.querySelectorAll<HTMLElement>('[role=dialog]')].pop()!, body = dlg.querySelector<HTMLElement>('.body')!;
  const bad: string[] = [];
  for (const e of [dlg, ...dlg.querySelectorAll<HTMLElement>('*')]) {
    const r = e.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) continue;
    if (r.left < -0.5 || r.right > vw + 0.5) bad.push(`${e.tagName.toLowerCase()}${e.dataset.test ? `[${e.dataset.test}]` : ''}.${[...e.classList].join('.')} ${Math.round(r.left)}..${Math.round(r.right)}`);
    if (getComputedStyle(e).overflowX !== 'visible' && e.scrollWidth > e.clientWidth + 1 && e.tagName !== 'INPUT') bad.push(`scrolls sideways: ${e.tagName.toLowerCase()}.${[...e.classList].join('.')} ${e.scrollWidth}>${e.clientWidth}`);
  }
  scrollTo(200, scrollY); const pageX = scrollX; scrollTo(0, scrollY);
  const st = body.scrollTop; body.scrollLeft = 200; const bodyX = body.scrollLeft; body.scrollLeft = 0; body.scrollTop = st;
  return { bad, docW: document.documentElement.scrollWidth, vw, pageX, bodyX };
});

const shot = (page: Page, name: string) => page.screenshot({ path: path.join(SHOTS, name) });

for (const [w, h] of [[360, 740], [390, 844]] as const) {
  test.describe(`phone ${w}x${h}`, () => {
    test('list rows one line, >= 44 px, full width; every section opens; nothing wider than the screen; no sideways scroll', async ({ browser }) => {
      const { page, ctx, errors } = await openTeam(browser, w, h);
      await openFromTeam(page, 'open-manage');
      const root = page.locator('.manage');
      await expect(root).toHaveAttribute('data-phone-screen', 'list');
      await expect(page.locator('[data-test=manage-error]')).toHaveCount(0);
      const m = await rowInfo(page);
      await shot(page, `list-${w}x${h}.png`);
      expect(m.rows.map((r) => r.id)).toEqual(IDS);
      expect(m.rows.map((r) => r.text)).toEqual(LABELS);
      for (const r of m.rows) {
        expect.soft(r.lines, `${r.id}: one line`).toBe(1);
        expect.soft(r.fits, `${r.id}: label not cut`).toBe(true);
        expect.soft(r.h, `${r.id}: >= 44 px`).toBeGreaterThanOrEqual(44);
        expect.soft(Math.abs(r.w - m.navW), `${r.id}: full width of the list`).toBeLessThanOrEqual(1);
        expect.soft(r.chevron, `${r.id}: chevron`).toBe(true);
      }
      const o0 = await overflow(page);
      expect.soft(o0.bad, 'list: nothing sticks out').toEqual([]);
      expect.soft([o0.docW <= o0.vw, o0.pageX, o0.bodyX], 'list: no sideways scroll').toEqual([true, 0, 0]);

      for (let i = 0; i < IDS.length; i++) {
        const id = IDS[i]!;
        await page.locator(`[data-test=manage-tab-${id}]`).tap();
        await expect(root).toHaveAttribute('data-phone-screen', 'section');
        await expect(page.locator('[data-test=manage-section-title]')).toHaveText(LABELS[i]!);
        await expect(page.locator(PANES[id]!).first()).toBeVisible();
        await expect(page.locator('[data-test=manage-tabs]')).toHaveCount(0);
        await page.waitForTimeout(200);
        const o = await overflow(page);
        expect.soft(o.bad, `${id}: nothing sticks out`).toEqual([]);
        expect.soft([o.docW <= o.vw, o.pageX, o.bodyX], `${id}: no sideways scroll`).toEqual([true, 0, 0]);
        await shot(page, `section-${id}-${w}x${h}.png`);
        await page.locator('[data-test=manage-back]').tap();
        await expect(root).toHaveAttribute('data-phone-screen', 'list');
      }
      expect(errors).toEqual([]);
      await ctx.close();
    });

    test('Back returns to the list; the head stays put and tappable while a long section scrolls', async ({ browser }) => {
      const { page, ctx, errors } = await openTeam(browser, w, h);
      await openFromTeam(page, 'open-manage');
      const root = page.locator('.manage'), body = page.locator('[role=dialog] .body').last();

      // General's Repositories link opens that section; Back from there goes to the list (not General).
      await page.locator('[data-test=manage-tab-general]').tap();
      await page.locator('[data-test=team-general] a', { hasText: 'Repositories' }).tap();
      await expect(page.locator('[data-test=manage-section-title]')).toHaveText('Repositories');
      await expect(page.locator('[data-test=repo-main]')).toBeVisible();
      await page.locator('[data-test=manage-back]').tap();
      await expect(root).toHaveAttribute('data-phone-screen', 'list');
      await expect(page.locator('[data-test=manage-tabs]')).toBeVisible();

      // Environment with 25 variables is longer than the screen: scroll it in steps, the head stays at the same place,
      // fully shown, and is what a tap at Back's centre hits.
      await page.locator('[data-test=manage-tab-env]').tap();
      await expect(page.locator('[data-test=env-name-24]')).toBeAttached();
      const head = async () => page.evaluate(() => {
        const hd = document.querySelector<HTMLElement>('.section-head')!, b = document.querySelector<HTMLElement>('[data-test=manage-back]')!;
        const r = hd.getBoundingClientRect(), br = b.getBoundingClientRect(), hit = document.elementFromPoint(br.left + br.width / 2, br.top + br.height / 2);
        const t = document.querySelector<HTMLElement>('[data-test=manage-section-title]')!;
        return { top: Math.round(r.top), h: Math.round(r.height), backOnTop: hit === b || b.contains(hit), backH: br.height, titleClipped: t.scrollWidth > t.clientWidth + 1 };
      });
      const h0 = await head();
      expect(h0.backOnTop, 'Back is on top before scrolling').toBe(true);
      expect(h0.backH, 'Back >= 44 px').toBeGreaterThanOrEqual(44);
      expect(h0.titleClipped, 'title not clipped').toBe(false);
      const max = await body.evaluate((b) => b.scrollHeight - b.clientHeight);
      expect(max, 'the Environment section scrolls').toBeGreaterThan(200);
      for (const f of [0.25, 0.5, 1]) {
        await body.evaluate((b, f) => { b.scrollTop = (b.scrollHeight - b.clientHeight) * f; }, f);
        await page.waitForTimeout(120);
        const hn = await head();
        expect.soft(hn.top, `head top at ${f * 100}% scrolled`).toBe(h0.top);
        expect.soft(hn.h, `head height at ${f * 100}% scrolled`).toBe(h0.h);
        expect.soft(hn.backOnTop, `Back is what a tap hits at ${f * 100}% scrolled`).toBe(true);
      }
      // A real finger drag too.
      await body.evaluate((b) => { b.scrollTop = 0; });
      const bb = (await body.boundingBox())!;
      await page.mouse.move(bb.x + bb.width / 2, bb.y + bb.height - 40);
      await page.mouse.wheel(0, 600);
      await page.waitForTimeout(200);
      expect.soft((await head()).top, 'head top after a wheel scroll').toBe(h0.top);
      await shot(page, `env-scrolled-${w}x${h}.png`);

      // Back works scrolled down, and the next section starts at its top.
      await page.locator('[data-test=manage-back]').tap();
      await expect(root).toHaveAttribute('data-phone-screen', 'list');
      await page.locator('[data-test=manage-tab-delete]').tap();
      await expect(page.locator('[data-test=team-delete-pane]')).toBeVisible();
      await expect.poll(() => body.evaluate((b) => b.scrollTop), 'a section opens at its top').toBe(0);
      await page.locator('[data-test=manage-back]').tap();
      await expect(root).toHaveAttribute('data-phone-screen', 'list');
      // Close from a section, and reopening starts at the list.
      await page.locator('[data-test=manage-tab-network]').tap();
      await page.locator('[role=dialog] [data-test=modal-close]').last().tap();
      await expect(page.locator('[role=dialog]')).toHaveCount(0);
      await openFromTeam(page, 'open-manage');
      await expect(page.locator('.manage')).toHaveAttribute('data-phone-screen', 'list');
      expect(errors).toEqual([]);
      await ctx.close();
    });

    test('other Modal sheets: Close 44x44, shown, 18 px glyph, title not clipped, Close closes', async ({ browser }) => {
      const { page, ctx, errors } = await openTeam(browser, w, h);
      const seen: Record<string, unknown> = {};
      for (const id of ['open-manage', 'open-triggers', 'open-git', 'drawer-team-documents', 'drawer-team-workflows']) { // Logs is a page, not a sheet
        if (id.startsWith('drawer-') || id.startsWith('nav-')) {
          await page.goto(page.url().replace(/#.*/, '#team=1'));
          await expect(page.locator('[data-test=team-title]')).toBeVisible();
          await page.locator('[data-test=drawer-toggle]').click();
          await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'open');
          const item = page.locator(`[data-test=sidebar] [data-test=${id}]`).first();
          await expect(item, `${id} in the drawer`).toBeVisible();
          await item.click();
          await page.waitForTimeout(500);
        } else await openFromTeam(page, id);
        const sheet = page.locator('.modal.sheet').last();
        if (!(await sheet.count())) { seen[id] = 'not a Modal sheet'; continue; }
        const info = await sheet.evaluate((m) => {
          const head = m.querySelector(':scope > header')!, h2 = head.querySelector('h2')!, c = head.querySelector<HTMLElement>('[data-test=modal-close]')!;
          const cr = c.getBoundingClientRect(), cs = getComputedStyle(c), range = document.createRange();
          range.selectNodeContents(c);
          const g = range.getBoundingClientRect(), hr = h2.getBoundingClientRect();
          const hit = document.elementFromPoint(cr.left + cr.width / 2, cr.top + cr.height / 2);
          return { title: h2.textContent!.trim(), size: `${Math.round(cr.width)}x${Math.round(cr.height)}`, inView: cr.left >= 0 && cr.right <= innerWidth && cr.top >= 0,
            visible: c.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }), color: cs.color, font: cs.fontSize, glyphIn: g.left >= cr.left && g.right <= cr.right && g.top >= cr.top && g.bottom <= cr.bottom,
            onTop: hit === c || c.contains(hit), titleClipped: h2.scrollWidth > h2.clientWidth + 1, titleOverClose: hr.right > cr.left + 0.5 };
        });
        seen[id] = info;
        await shot(page, `sheet-${id}-${w}x${h}.png`);
        const where = `${id} @${w}x${h} (${info.title})`;
        expect.soft(info.size, `${where}: Close size`).toBe('44x44');
        expect.soft(info.inView, `${where}: Close inside the screen`).toBe(true);
        expect.soft(info.visible, `${where}: Close visible`).toBe(true);
        expect.soft(info.onTop, `${where}: Close is what a tap hits`).toBe(true);
        expect.soft(info.font, `${where}: glyph size`).toBe('18px');
        expect.soft(info.glyphIn, `${where}: glyph inside its box`).toBe(true);
        expect.soft(info.color, `${where}: glyph colour`).not.toMatch(/rgba\(.*,\s*0\)|transparent/);
        expect.soft(info.titleClipped, `${where}: title not clipped`).toBe(false);
        expect.soft(info.titleOverClose, `${where}: title clear of Close`).toBe(false);
        const before = await page.locator('.modal.sheet').count();
        await sheet.locator('[data-test=modal-close]').first().tap();
        await expect(page.locator('.modal.sheet'), `${where}: Close closes the sheet`).toHaveCount(before - 1);
        while (await page.locator('.modal.sheet').count()) await page.locator('.modal.sheet [data-test=modal-close]').last().click();
      }
      console.log(`C347 sheets @${w}x${h}: ${JSON.stringify(seen)}`);
      for (const id of ['open-manage', 'open-triggers', 'open-git']) expect.soft(typeof seen[id], `${id} opened a sheet`).toBe('object');
      expect(errors).toEqual([]);
      await ctx.close();
    });
  });
}

test(`desktop 1280x800: Settings tab row and Modal Close as on main ${BASE_COMMIT}`, async ({ browser }) => {
  const grab = async (which: 'base' | 'branch') => {
    const { page, ctx, errors } = await openTeam(browser, 1280, 800, which === 'base' ? baseBuiltDir : builtDir);
    const out: Record<string, unknown> = {};
    for (const id of ['open-manage', 'open-triggers']) {
      await openFromTeam(page, id);
      await shot(page, `desktop-1280-${id}-${which}.png`);
      // The Team settings dialog's height follows its General tab's content (B085 added the "Idle workflows" card:
      // 316 -> 469 px), so for open-manage the dialog box is left, top and width only. Everything else is unchanged.
      out[id] = await page.evaluate((anyHeight) => {
        const box = (e: Element | null, withHeight = true) => { if (!e) return null; const r = e.getBoundingClientRect(); return (withHeight ? [r.left, r.top, r.width, r.height] : [r.left, r.top, r.width]).map((n) => Math.round(n * 10) / 10).join(','); };
        const st = (e: Element | null, keys: string[]) => { if (!e) return null; const s = getComputedStyle(e); return keys.map((k) => `${k}=${s.getPropertyValue(k)}`).join(';'); };
        const m = [...document.querySelectorAll('[role=dialog]')].pop()!, c = m.querySelector('[data-test=modal-close]');
        const row = m.querySelector('[data-test=manage-tabs], nav.tabs');
        return { sheet: m.classList.contains('sheet'), dialog: box(m, !anyHeight), title: box(m.querySelector('h2')), close: box(c),
          closeStyle: st(c, ['width', 'height', 'font-size', 'border-top-color', 'border-top-width', 'background-color', 'color', 'padding-top', 'padding-left']),
          row: box(row), rowStyle: st(row, ['overflow-x', 'flex-wrap', 'flex-direction', 'border-bottom-width']),
          tabs: row ? [...row.querySelectorAll('button')].map((b) => `${b.textContent!.trim()}@${box(b)} ${st(b, ['white-space', 'flex-shrink', 'font-weight', 'color', 'border-bottom-color'])}`) : null,
          back: !!m.querySelector('[data-test=manage-back]'), chevrons: m.querySelectorAll('.go-icon').length };
      }, id === 'open-manage');
      await page.locator('[data-test=modal-close]').last().click();
      await expect(page.locator('[role=dialog]')).toHaveCount(0);
    }
    expect(errors).toEqual([]);
    await ctx.close();
    return out;
  };
  const base = await grab('base'), branch = await grab('branch');
  console.log(`C347 1280 base ${JSON.stringify(base)}\nC347 1280 branch ${JSON.stringify(branch)}`);
  expect((branch['open-manage'] as { sheet: boolean }).sheet, 'not a sheet at 1280').toBe(false);
  expect(branch).toEqual(base);
});
