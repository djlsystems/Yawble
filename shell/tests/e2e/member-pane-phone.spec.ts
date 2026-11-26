// A member's pane on the team page in a real browser (headless Chrome), against the FAKE core
// of phone-remote.spec.ts (remote.html, canned answers). DeveloperAda's pane is on its Terminal tab.
// On a phone (375x501, 375x667, 667x263, 667x375): no sideways scroll, the terminal full width, its transcript at least
// 60% of the visible height, the header's controls 44 px. At 1280 x 800: one frame, one view (the chosen tab's), the
// pane no taller than the old card + activity row (193 px at the default output height, see phone-member-pane.test.ts).
// On a phone the page opens on the Report; the pane's Terminal tab opens the member's terminal full screen over the
// page with one header row (title, ⋯, ✕) that is on top of the app's top bar, its transcript >= 60% of the height; the
// ⋯ opens the state line and Stop (never Open interactive CLI: the terminal in a pane is never interactive), ✕ goes
// back to the Report.
//
//   npx playwright test member-pane-phone      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser } from './phone/fixtures';
import path from 'node:path';
import { mkdirSync } from 'node:fs';
import { answers } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';
import { settled } from './settle';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), process.env.MEMBER_PANE_SHOTS || 'member-pane-screens');

// The terminal's calls (not in the shared fake): an idle member's last run, replayed.
answers['terminal.banner'] = (p) => ({ memberId: p?.memberId, channel: `member:${p?.memberId}`, title: 'payments / DeveloperAda', details: ['timeout 2h'], loosened: [],
  state: 'idle', queuePosition: null, queueLength: 0, runId: null, interactive: false, lastRunState: 'completed', lastRunReason: null });
answers['terminal.attach'] = (p) => ({ channel: p?.channel, replay: Buffer.from('$ npm test\r\nok 1 refund\r\n').toString('base64'), end: 24,
  session: { kind: 'headless', runId: 41, startedAt: new Date().toISOString(), inputEnabled: false, command: 'claude', ended: true, exitCode: 0, endReason: null } });
answers['terminal.resize'] = () => null;
answers['terminal.events'] = () => ({ end: 24, chunks: [] });

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

async function open(browser: Browser, w: number, h: number) {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 2 });
  await ctx.addInitScript(() => { try { localStorage.setItem('yawble.memberPaneView', JSON.stringify({ 'payments/DeveloperAda': 'terminal' })); } catch { /* none */ } });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#team=1`);
  const pane = page.locator('[data-test=agent-DeveloperAda]');
  if (mobile) {
    // A phone opens the team page with every member on the Report: the pane's Terminal tab opens the terminal.
    await expect(pane.locator('[data-test=member-view-report]')).toHaveAttribute('aria-selected', 'true', { timeout: 30_000 });
    await pane.locator('[data-test=member-view-terminal]').click();
  }
  await expect(pane.locator('[data-test=member-terminal]')).toBeVisible({ timeout: 30_000 });
  await settled(page);
  return { page, ctx, errors };
}

for (const [w, h] of [[375, 501], [375, 667], [667, 263], [667, 375]] as const) {
  test(`phone ${w}x${h}: the terminal is the whole screen, one header row over the top bar, transcript >= 60%, 44 px controls`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, w, h);
    const pane = page.locator('[data-test=agent-DeveloperAda]');
    await page.screenshot({ path: path.join(SHOTS, `member-pane-${w}x${h}.png`) });
    const m = await page.evaluate(() => {
      const pane = document.querySelector('[data-test=agent-DeveloperAda]')!;
      const r = (e: Element | null) => (e ? e.getBoundingClientRect() : null);
      const term = pane.querySelector('[data-test=member-terminal]');
      const screen = [...(term?.querySelectorAll('.screen-wrap > .layer') ?? [])].find((e) => !e.classList.contains('hidden')) ?? null;
      const head = term?.querySelector('[data-test=terminal-pane-head]') ?? null;
      const controls = [...(head?.querySelectorAll('button') ?? [])].filter((b) => (b as HTMLElement).offsetParent)
        .map((b) => ({ t: b.getAttribute('data-test'), w: b.getBoundingClientRect().width, h: b.getBoundingClientRect().height }));
      // What is on top at the middle of the terminal's ⋯: the header row itself, not the app's top bar.
      const more = term?.querySelector('[data-test=terminal-pane-more]') ?? null;
      const mr = r(more);
      const atHead = mr ? document.elementFromPoint(mr.left + mr.width / 2, mr.top + mr.height / 2) : null;
      return { vw: innerWidth, vh: innerHeight, scrollW: document.documentElement.scrollWidth, term: r(term), head: r(head), screen: r(screen), controls,
        headShown: !!more && !!atHead && more.contains(atHead), heads: term?.querySelectorAll('[data-test=terminal-pane-head]').length ?? 0 };
    });
    // eslint-disable-next-line no-console
    console.log(JSON.stringify({ size: `${w}x${h}`, ...m }));
    expect(m.scrollW, 'no sideways scroll').toBeLessThanOrEqual(m.vw);
    expect(m.heads, 'one header row').toBe(1);
    expect(m.term!.top, 'the terminal starts at the top of the screen').toBeLessThanOrEqual(1);
    expect(m.term!.width, 'the terminal is the screen wide').toBeGreaterThanOrEqual(m.vw - 1);
    expect(m.term!.height, 'terminal fits the screen').toBeLessThanOrEqual(m.vh + 1);
    expect(m.head!.height, 'one 44 px header row').toBeLessThanOrEqual(46);
    expect(m.screen, 'the transcript area').not.toBeNull();
    expect(m.screen!.height / m.vh, `transcript ${m.screen!.height} of ${m.vh}`).toBeGreaterThanOrEqual(0.6);
    expect(m.screen!.bottom, 'the transcript reaches no further than the screen').toBeLessThanOrEqual(m.vh + 1);
    expect(m.headShown, "the terminal's header row is on top (not under the app's top bar)").toBe(true);
    for (const t of m.controls) expect.soft(Math.min(t.w, t.h), `${t.t} ${t.w}x${t.h}`).toBeGreaterThanOrEqual(44);
    // Its ⋯ opens the terminal's state line and Stop, never Open interactive CLI.
    await pane.locator('[data-test=terminal-pane-more]').click();
    await expect(pane.locator('[data-test=terminal-pane-menu] [data-test=terminal-state]')).toBeVisible();
    await expect(pane.locator('[data-test=terminal-pane-menu] [data-test=terminal-stop]')).toBeVisible();
    await expect(pane.locator('[data-test=terminal-pane-menu] [data-test=open-interactive]')).toHaveCount(0);
    // ✕ goes back to the Report.
    await pane.locator('[data-test=terminal-pane-close]').click();
    await expect(pane.locator('[data-test=member-terminal]')).toHaveCount(0);
    await expect(pane.locator('[data-test=agent-output]')).toBeVisible();
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('desktop 1280x800: one frame, one view, the pane fills its grid tile', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 1280, 800);
  await page.screenshot({ path: path.join(SHOTS, 'member-pane-1280x800.png') });
  const m = await page.evaluate(() => {
    const out: Record<string, { h: number; cell: number; views: number; tabs: string[]; menu: boolean }> = {};
    for (const n of ['Manager', 'DeveloperAda', 'TesterBen']) {
      const p = document.querySelector(`[data-test=agent-${n}]`)!;
      out[n] = { h: Math.round(p.getBoundingClientRect().height), cell: Math.round(p.closest('.member-cell')!.getBoundingClientRect().height), views: p.querySelector('[data-test=member-pane-body]')!.children.length,
        tabs: [...p.querySelectorAll('[data-test=member-tabs] [role=tab]')].map((t) => t.textContent!.trim()),
        menu: !!p.querySelector('[data-test=agent-title] [data-test=member-more]') };
    }
    return out;
  });
  // eslint-disable-next-line no-console
  console.log(JSON.stringify(m));
  for (const [n, v] of Object.entries(m)) {
    expect(v.views, `${n}: one view`).toBe(1);
    expect(v.tabs, `${n}: tabs`).toEqual(['Report', 'Activity', 'Terminal']);
    expect(v.menu, `${n}: the ☰ in the title bar`).toBe(true);
    // The members are tiles of one shared height (340 by default); the pane fills its tile (it was <= 193 before the tiles).
    expect(v.h, `${n}: pane height = its tile's`).toBe(v.cell);
    expect(v.cell, `${n}: the default tile height`).toBe(340);
  }
  expect(errors).toEqual([]);
  await ctx.close();
});
