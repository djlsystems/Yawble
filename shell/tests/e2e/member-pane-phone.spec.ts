// B061 verify (card 288): a member's pane on the team page in a real browser (headless Chrome), against the FAKE core
// of phone-remote.spec.ts (remote.html, canned answers). DeveloperAda's pane is on Terminal (the pane's own toggle).
// On a phone (375x501, 375x667, 667x263, 667x375): no sideways scroll, the terminal full width, its transcript (the
// screen under the terminal's bar and tabs) at least 60% of the visible height and on screen once scrolled to it, the
// toolbar's controls 44 px. At 1280 x 800: one frame, the two areas side by side, the pane no taller than the old
// card + activity row (193 px at the default output height, see phone-member-pane.test.ts).
// Card 286 fix round (from this verify's F3): also, with the transcript scrolled to its end, the terminal's one header
// row (its tabs and ⋯) is not under the app's top bar, and its ⋯ opens the state line and Policy.
//
//   npx playwright test member-pane-phone      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser } from '@playwright/test';
import path from 'node:path';
import { mkdirSync } from 'node:fs';
import { answers } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), process.env.B061_SHOTS || 'B061-screens');

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
  await expect(page.locator('[data-test=agent-DeveloperAda] [data-test=member-terminal]')).toBeVisible({ timeout: 30_000 });
  await page.waitForTimeout(500);
  return { page, ctx, errors };
}

for (const [w, h] of [[375, 501], [375, 667], [667, 263], [667, 375]] as const) {
  test(`phone ${w}x${h}: stacked, terminal full width, transcript >= 60% of the height, 44 px toolbar`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, w, h);
    const pane = page.locator('[data-test=agent-DeveloperAda]');
    const term = pane.locator('[data-test=member-terminal]');
    await term.scrollIntoViewIfNeeded();
    // The transcript's bottom at the screen's bottom (as far as the page scrolls): what of it is seen under the app's header.
    await page.evaluate(() => { const t = document.querySelector('[data-test=agent-DeveloperAda] [data-test=member-terminal]'); const l = [...(t?.querySelectorAll('.screen-wrap > .layer') ?? [])].find((e) => !e.classList.contains('hidden')); l?.scrollIntoView({ block: 'end' }); });
    await page.waitForTimeout(300);
    await page.screenshot({ path: path.join(SHOTS, `member-pane-${w}x${h}.png`) });
    const m = await page.evaluate(() => {
      const pane = document.querySelector('[data-test=agent-DeveloperAda]')!;
      const r = (e: Element | null) => (e ? e.getBoundingClientRect() : null);
      const term = pane.querySelector('[data-test=member-terminal]');
      const screen = [...(term?.querySelectorAll('.screen-wrap > .layer') ?? [])].find((e) => !e.classList.contains('hidden')) ?? null;
      const said = pane.querySelector('[data-test=member-said]');
      const tools = [...pane.querySelectorAll('[data-test=member-toolbar] button')].filter((b) => (b as HTMLElement).offsetParent)
        .map((b) => ({ t: b.getAttribute('data-test'), w: b.getBoundingClientRect().width, h: b.getBoundingClientRect().height }));
      const sv = document.documentElement;
      // What is on top at the middle of the terminal's header row (its ⋯): the row itself, not the app's top bar.
      const more = term?.querySelector('[data-test=member-terminal-more]') ?? null;
      const mr = r(more);
      const atHead = mr ? document.elementFromPoint(mr.left + mr.width / 2, mr.top + mr.height / 2) : null;
      const headShown = !!more && !!atHead && more.contains(atHead);
      return { vw: innerWidth, vh: innerHeight, scrollW: sv.scrollWidth, pane: r(pane), term: r(term), screen: r(screen), said: r(said), tools, headShown,
        topBar: r(document.querySelector('.main > header.bar')) };
    });
    // eslint-disable-next-line no-console
    console.log(JSON.stringify({ size: `${w}x${h}`, ...m }));
    expect(m.scrollW, 'no sideways scroll').toBeLessThanOrEqual(m.vw);
    expect(m.said!.bottom, 'stacked: what was said above the terminal').toBeLessThanOrEqual(m.term!.top + 1);
    expect(m.term!.width, 'terminal full width of the pane').toBeGreaterThanOrEqual(m.pane!.width - 30);
    expect(m.screen, 'the transcript area').not.toBeNull();
    expect(m.screen!.height / m.vh, `transcript ${m.screen!.height} of ${m.vh}`).toBeGreaterThanOrEqual(0.6);
    expect(m.term!.height, 'terminal fits the screen').toBeLessThanOrEqual(m.vh + 1);
    expect(m.screen!.bottom, 'the transcript reaches no further than the screen').toBeLessThanOrEqual(m.vh + 1);
    expect(m.headShown, `the terminal's header row is not under the top bar (bar bottom ${m.topBar?.bottom}, terminal top ${m.term!.top})`).toBe(true);
    if (m.topBar) expect(m.term!.top, 'the whole terminal is below the top bar').toBeGreaterThanOrEqual(m.topBar.bottom - 1);
    // Its ⋯ opens the terminal's state line and Policy (the B057 header, now in the member's pane).
    await pane.locator('[data-test=member-terminal-more]').click();
    await expect(pane.locator('[data-test=member-terminal-menu] [data-test=terminal-state]')).toBeVisible();
    await expect(pane.locator('[data-test=member-terminal-menu] [data-test=terminal-details-toggle]')).toBeVisible();
    for (const t of m.tools) expect.soft(Math.min(t.w, t.h), `${t.t} ${t.w}x${t.h}`).toBeGreaterThanOrEqual(44);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('desktop 1280x800: one frame, side by side, no taller than the old card + activity row', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 1280, 800);
  await page.screenshot({ path: path.join(SHOTS, 'member-pane-1280x800.png') });
  const m = await page.evaluate(() => {
    const out: Record<string, { h: number; saidRight: number; sideLeft: number }> = {};
    for (const n of ['Manager', 'DeveloperAda', 'TesterBen']) {
      const p = document.querySelector(`[data-test=agent-${n}]`)!;
      out[n] = { h: p.getBoundingClientRect().height, saidRight: p.querySelector('[data-test=member-said]')!.getBoundingClientRect().right,
        sideLeft: p.querySelector('[data-test=member-side]')!.getBoundingClientRect().left };
    }
    return out;
  });
  // eslint-disable-next-line no-console
  console.log(JSON.stringify(m));
  for (const [n, v] of Object.entries(m)) {
    expect(v.saidRight, `${n}: side by side`).toBeLessThanOrEqual(v.sideLeft);
    expect(v.h, `${n}: pane height`).toBeLessThanOrEqual(193);
  }
  expect(errors).toEqual([]);
  await ctx.close();
});
