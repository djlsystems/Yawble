// Every workflows entry point opens the Workflows page, in headless Chrome against the
// phone-remote fake core (remote.html, tests/e2e/phone): the Teams-list chip, the Team page's Workflows button and its
// ⋯ menu item, the Monitor's screen-reader list (a team's header and a workflow), the phone drawer's Workflows item and
// #workflow=N (a known one, one outside the loaded rows, an unknown one). Each lands on the page with the right team
// filter, the named workflow selected and its pane open, no dialog; Esc and the phone pane's Back close the pane. In
// ?embed=1 yawble.nav reports Workflows as the active place. A Visitor switch while a confirm is open closes it and sends
// nothing. #12's expiry on the page (setExpiry) and the Needs you warning after Extend.
//
//   npx playwright test workflows-entry-points --workers=1      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import path from 'node:path';
import { answers } from './phone/fake-core';
import { ORIGIN, buildRenderer, ensureDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'workflows-entry-points') : null;
test.beforeAll(async () => { test.setTimeout(600_000); if (shots) ensureDir(shots); await buildRenderer(); });

const saved = { ...answers };
test.afterEach(() => { for (const k of Object.keys(answers)) if (!(k in saved)) delete answers[k]; Object.assign(answers, saved); });

async function open(browser: Browser, w: number, h: number, opts: { hash?: string; access?: 'full' | 'read'; expiry?: boolean } = {}) {
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, reducedMotion: 'reduce' });
  const s = fakeRemote();
  s.core.setAsks(false);
  if (opts.access) s.core.setAccess(opts.access);
  if (opts.expiry) s.core.setExpiry(true);
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}${opts.hash ?? ''}`);
  await expect(page.locator('.app')).toBeAttached({ timeout: 30_000 });
  await page.waitForTimeout(400);
  return { s, page, ctx, errors };
}

const view = (p: Page) => p.locator('[data-test=workflows-view]');
const pane = (p: Page) => p.locator('[data-test=workflow-pane]');
async function onPage(p: Page, team: string, wf: number | null) {
  await expect(view(p)).toBeVisible({ timeout: 15_000 });
  await expect(p.locator('[data-test=workflows-team-filter]')).toHaveValue(team);
  await expect(p.locator('[role=dialog]')).toHaveCount(0);
  if (wf === null) await expect(pane(p)).toHaveCount(0);
  else {
    await expect(pane(p)).toHaveAttribute('data-id', String(wf), { timeout: 15_000 });
    await expect(p.locator(`[data-test=workflows-row][data-wf="${wf}"]`).first()).toBeAttached();
  }
}
async function navTo(p: Page, place: string) {
  if (await p.locator('[data-test=drawer-toggle]').isVisible()) await p.locator('[data-test=drawer-toggle]').click();
  await p.locator(`[data-test=sidebar] [data-test=nav-${place}]`).first().click();
}
const shot = async (p: Page, name: string) => { if (shots) await p.screenshot({ path: path.join(shots, `${name}.png`) }); };

for (const [w, h] of [[1280, 800], [375, 740]] as const) {
  test(`${w}: Teams-list chip opens the page at that workflow, team 1, pane open; Esc (or Back on a phone) closes the pane`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, w, h);
    await navTo(page, 'teams');
    await page.locator('[data-test=team-workflow][data-id="10"]').first().click();
    await onPage(page, '1', 10);
    await shot(page, `teams-chip-${w}`);
    if (w < 760) await pane(page).locator('[data-test=workflow-pane-back]').click();
    else await page.keyboard.press('Escape');
    await expect(pane(page)).toHaveCount(0);
    await expect(view(page)).toBeVisible();
    expect(errors).toEqual([]);
    await ctx.close();
  });

  test(`${w}: Team page Workflows (button at 1280, ⋯ menu at 375) opens the page filtered to the team, no pane, no dialog`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, w, h);
    await navTo(page, 'teams');
    await page.locator('[data-test=team-open-checkout-web-with-a-rather-long-team-name], [data-test=team-open-payments]').last().click();
    const title = await page.locator('[data-test=team-title]').first().textContent();
    const want = /checkout/.test(title ?? '') ? '2' : '1';
    if (w < 760) {
      await page.locator('[data-test=team-more]').click();
      await page.locator('[data-test=team-more-menu] [data-test=open-workflows-menu]').click();
    } else await page.locator('[data-test=open-workflows]').first().click();
    await onPage(page, want, null);
    // Browser Back after the page opened: recorded.
    await page.goBack().catch(() => null);
    await page.waitForTimeout(600);
    console.log(`BACK ${w}: team page shown=${await page.locator('[data-test=team-title]').count()} workflows page shown=${await view(page).count()} url=${page.url()}`);
    expect(errors).toEqual([]);
    await ctx.close();
  });

  test(`${w}: #workflow=16 opens team 2's #16 (team from the core); #workflow=8 outside the loaded rows opens; #workflow=999 is not found, no pane`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, w, h, { hash: '#workflow=16' });
    await onPage(page, '2', 16);
    await shot(page, `hash-16-${w}`);
    await ctx.close();
    // #8 (closed) is left out of every workflows.list answer: only workflows.get knows it.
    const list = answers['workflows.list'];
    answers['workflows.list'] = (p, a) => { const r = list(p, a) as any; return { ...r, workflows: r.workflows.filter((x: any) => x.id !== 8) }; };
    const b = await open(browser, w, h, { hash: '#workflow=8' });
    await expect(pane(b.page)).toHaveAttribute('data-id', '8', { timeout: 15_000 });
    await expect(pane(b.page).locator('[data-test=workflow-pane-title]')).toHaveText('Try a new payment provider');
    await expect(b.page.locator('[data-test=workflows-team-filter]')).toHaveValue('2');
    await expect(b.page.locator('[role=dialog]')).toHaveCount(0);
    await shot(b.page, `hash-8-old-${w}`);
    await b.ctx.close();
    const c = await open(browser, w, h, { hash: '#workflow=999' });
    await expect(c.page.locator('[data-test=workflows-not-found]')).toContainText('Workflow #999 was not found', { timeout: 15_000 });
    await expect(pane(c.page)).toHaveCount(0);
    await expect(c.page.locator('[role=dialog]')).toHaveCount(0);
    await shot(c.page, `hash-999-${w}`);
    expect([...errors, ...b.errors, ...c.errors]).toEqual([]);
    await c.ctx.close();
  });

  test(`${w}: Monitor's screen-reader list: a workflow opens the page at it, a team header at the team; no dialog over the Monitor`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, w, h);
    await navTo(page, 'monitor');
    const item = page.locator('[data-test=monitor-workflow]').first();
    await expect(item).toBeAttached({ timeout: 20_000 });
    const id = Number(await item.getAttribute('data-id'));
    const team = id === 15 || id === 16 || id === 8 ? '2' : '1';
    await item.evaluate((el) => (el as HTMLElement).click());
    await onPage(page, team, id);
    await expect(page.locator('[data-test=monitor-view], .monitor')).toHaveCount(0);
    await navTo(page, 'monitor');
    const hdr = page.locator('[data-test=monitor-workflows-team]').first();
    await expect(hdr).toBeAttached({ timeout: 20_000 });
    const name = (await hdr.textContent()) ?? '';
    await hdr.evaluate((el) => (el as HTMLElement).click());
    await onPage(page, /^\s*payments/.test(name) ? '1' : '2', null);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('375: the phone drawer\'s Workflows item (a team page open) opens the page filtered to that team, no dialog', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 375, 740);
  await navTo(page, 'teams');
  await page.locator('[data-test=team-open-payments]').click();
  await expect(page.locator('[data-test=team-title]').first()).toBeVisible();
  await page.locator('[data-test=drawer-toggle]').click();
  await page.locator('[data-test=drawer-team-workflows]').click();
  await onPage(page, '1', null);
  await shot(page, 'drawer-375');
  expect(errors).toEqual([]);
  await ctx.close();
});

for (const [kind, wf, btn] of [['Nudge', 12, 'workflow-pane-nudge'], ['Stop', 10, 'workflow-pane-stop'], ['Close', 10, 'workflow-pane-close-wf'], ['Extend', 16, 'workflow-pane-extend']] as const) {
  test(`a Visitor switch while the ${kind} confirm is open closes it and sends nothing`, async ({ browser }) => {
    const { s, page, ctx, errors } = await open(browser, 1280, 800, { hash: `#workflow=${wf}` });
    await expect(pane(page)).toHaveAttribute('data-id', String(wf), { timeout: 15_000 });
    const b = pane(page).locator(`[data-test=${btn}]`);
    await expect(b, `${kind} offered`).toBeVisible();
    await b.click();
    await expect(page.locator('[data-test=confirm-dialog]')).toBeVisible();
    const before = s.core.calls.length;
    s.core.setAccess('read');
    s.push('remote.connected', {});
    await expect(page.locator('[data-test=remote-access-chip]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-test=confirm-dialog]')).toHaveCount(0);
    await expect(pane(page).locator(`[data-test=${btn}]`)).toHaveCount(0);
    await page.waitForTimeout(500);
    const after = s.core.calls.slice(before);
    for (const m of ['bus.nudgeWorkflow', 'bus.stopWorkflow', 'bus.closeWorkflow', 'bus.workflowExtend']) expect(after, m).not.toContain(m);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test("#12's expiry on the page (setExpiry): its pane offers Extend; after Extend the Kanban board's warning is gone", async ({ browser }) => {
  const { s, page, ctx, errors } = await open(browser, 1280, 800, { hash: '#workflow=12', expiry: true });
  await expect(pane(page)).toHaveAttribute('data-id', '12', { timeout: 15_000 });
  const row = page.locator('[data-test=workflows-row][data-wf="12"]');
  const paneText = (await pane(page).innerText()).replace(/\s+/g, ' ');
  const rowText = (await row.innerText()).replace(/\s+/g, ' ');
  const tips = await page.locator('[data-test=workflows-row][data-wf="12"] [title], [data-test=workflow-pane] [title]').evaluateAll((els) => els.map((e) => e.getAttribute('title')));
  console.log(`#12 PANE: ${paneText.slice(0, 400)}`);
  console.log(`#12 ROW: ${rowText}`);
  console.log(`#12 says expiry: ${/expire/i.test(paneText + rowText + tips.join(' '))} tips=${JSON.stringify(tips.filter((t) => /xpir/i.test(t ?? '')))}`);
  await shot(page, 'expiry-12-page');
  await pane(page).locator('[data-test=workflow-pane-extend]').click();
  await page.locator('[data-test=confirm-ok]').click();
  await expect.poll(() => s.core.calls.filter((c) => c === 'bus.workflowExtend').length).toBe(1);
  await navTo(page, 'board');
  await page.waitForTimeout(1500);
  await expect(page.locator('[data-test=workflow-expiry-text]')).toHaveCount(0);
  expect(errors).toEqual([]);
  await ctx.close();
});

// ?embed=1: an entry point inside the frame; yawble.nav then reports Workflows as the active place.
const parentHtml = (src: string) => `<!doctype html><html><body style="margin:0"><iframe id="f" style="width:100vw;height:100vh;border:0" src="${src}"></iframe>
<script>window.got=[];const f=document.getElementById('f');addEventListener('message',(e)=>{if(e.source===f.contentWindow)window.got.push(e.data)});
window.send=(m)=>f.contentWindow.postMessage(m,'*');</script></body></html>`;
for (const [w, h] of [[1280, 800], [375, 740]] as const) {
  test(`${w} embed: the Teams-list chip opens the Workflows page in the frame and yawble.nav reports workflows active`, async ({ browser }) => {
    const mobile = w < 760;
    const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile });
    const s = fakeRemote();
    s.core.setAsks(false);
    await s.attach(ctx);
    await ctx.route(`${ORIGIN}/cloud-parent.html`, (r) => r.fulfill({ status: 200, contentType: 'text/html', body: parentHtml(`${ORIGIN}/remote.html?embed=1&amp;theme=dark`) }));
    const page = await ctx.newPage();
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.goto(`${ORIGIN}/cloud-parent.html`);
    const frame = page.frameLocator('#f');
    await expect(frame.locator('.app.embed')).toBeAttached({ timeout: 30_000 });
    const lastNav = () => page.evaluate(() => (window as any).got.filter((m: any) => m.type === 'yawble.nav').at(-1)?.nav);
    await expect.poll(async () => (await lastNav())?.teams?.length ?? 0, { timeout: 15_000 }).toBeGreaterThan(0);
    await page.evaluate(() => (window as any).send({ type: 'yawble.navigate', to: { kind: 'place', id: 'teams' } }));
    await expect(frame.locator('[data-test=team-open-payments]')).toBeVisible({ timeout: 15_000 });
    const chip = frame.locator('[data-test=team-workflow][data-id="10"]').first();
    await expect(chip).toBeAttached({ timeout: 15_000 });
    await chip.click();
    await page.waitForTimeout(1500);
    const f = page.frames().find((x) => x.url().includes('remote.html'));
    console.log(`EMBED ${w} after chip: frame url=${f?.url()} app=${await frame.locator('.app').count()} view=${await frame.locator('[data-test=workflows-view]').count()} errors=${JSON.stringify(errors)}`);
    await expect(frame.locator('[data-test=workflows-view]')).toBeVisible({ timeout: 15_000 });
    await expect(frame.locator('[data-test=workflows-team-filter]')).toHaveValue('1');
    await expect(frame.locator('[data-test=workflow-pane]')).toHaveAttribute('data-id', '10');
    await expect(frame.locator('[role=dialog]')).toHaveCount(0);
    await expect.poll(async () => (await lastNav())?.places?.find((p: any) => p.active)?.id, { timeout: 10_000 }).toBe('workflows');
    if (shots) await page.screenshot({ path: path.join(shots, `embed-${w}.png`) });
    expect(errors).toEqual([]);
    await ctx.close();
  });
}
