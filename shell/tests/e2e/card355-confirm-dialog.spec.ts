// Card 355: the renderer's own confirm dialog (ConfirmDialog.vue / confirm.ts) in place of window.confirm, in the remote
// view (remote.html in headless Chrome against the FAKE core of phone-remote.spec.ts), on a 390 px phone and an iPad:
//  - Triggers: Delete asks in the page; Cancel keeps the trigger (no triggers.delete), OK deletes it.
//  - A member's ⋯ > Remove agent asks in the page; Cancel keeps the member (no members.remove), OK removes it.
//  - Escape cancels the confirm and leaves the Triggers dialog under it open; the box fits the screen; on a phone its
//    buttons are >= 44 px tall.
//  - No browser dialog (page 'dialog' event) at any point.
//
//   npx playwright test card355-confirm-dialog   (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser, type Page } from '@playwright/test';
import { answers } from './phone/fake-core';
import { buildRenderer, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(120_000);

const iso = (min: number) => new Date(Date.now() + min * 60_000).toISOString();
const trigger = (id: number, name: string) => ({
  id, teamId: 1, memberId: 2, name, schedule: { kind: 'every', intervalSec: 3600, cron: null, timezone: null, fireAt: null },
  instruction: 'Check the build.', onlyWhenIdle: false, enabled: true, nextDueAt: iso(30), lastFiredAt: null, wakeManager: 'always',
  lastOutcome: null, lastReason: null, lastSeq: null, missedCount: 0,
});
let triggers: ReturnType<typeof trigger>[] = [];
let removed = new Set<number>();
const saved: Record<string, unknown> = {};
const OVERRIDES: Record<string, (p?: any, a?: any) => unknown> = {
  'triggers.list': () => triggers,
  'triggers.delete': (p) => { triggers = triggers.filter((t) => t.id !== p?.id); return { ok: true }; },
  'members.remove': (p) => { removed.add(p?.memberId); return { ok: true }; },
};

test.beforeAll(async () => {
  test.setTimeout(900_000);
  const agents = answers['teams.agents']!;
  saved['teams.agents'] = agents;
  (answers as any)['teams.agents'] = (p: any, a: any) => (agents(p, a) as { member: { id: number } }[]).filter((c) => !removed.has(c.member.id));
  for (const k of Object.keys(OVERRIDES)) { saved[k] = (answers as any)[k]; (answers as any)[k] = OVERRIDES[k]; }
  await buildRenderer();
});
test.afterAll(() => { for (const k of Object.keys(saved)) { if (saved[k] === undefined) delete (answers as any)[k]; else (answers as any)[k] = saved[k]; } });
test.beforeEach(() => { triggers = [trigger(41, 'nightly-check'), trigger(42, 'weekly-report')]; removed = new Set(); });

async function openTeam(browser: Browser, w: number, h: number) {
  const s = fakeRemote();
  s.core.setAsks(false);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 2 });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  const dialogs: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('dialog', (d) => { dialogs.push(`${d.type()}: ${d.message()}`); void d.dismiss(); });
  await page.goto(`${s.url}#team=1`);
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  return { page, ctx, errors, dialogs, calls: s.core.calls };
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
}

const confirmBox = (page: Page) => page.locator('[data-test=confirm-dialog]');

/** The confirm is shown with `message`, inside the screen, OK has the focus; on a phone its buttons are tap-sized. */
async function expectConfirm(page: Page, message: string, okLabel: string, phone: boolean) {
  await expect(confirmBox(page)).toBeVisible();
  await expect(page.locator('[data-test=confirm-message]')).toHaveText(message);
  await expect(page.locator('[data-test=confirm-ok]')).toHaveText(okLabel);
  await expect(page.locator('[data-test=confirm-ok]')).toBeFocused();
  const m = await page.evaluate(() => {
    const r = document.querySelector('[data-test=confirm-dialog]')!.getBoundingClientRect();
    const b = [...document.querySelectorAll('[data-test=confirm-dialog] button')].map((x) => x.getBoundingClientRect().height);
    const ok = document.querySelector<HTMLElement>('[data-test=confirm-ok]')!.getBoundingClientRect();
    const hit = document.elementFromPoint(ok.left + ok.width / 2, ok.top + ok.height / 2);
    return { left: r.left, right: r.right, top: r.top, bottom: r.bottom, vw: innerWidth, vh: innerHeight, minButton: Math.min(...b),
      okOnTop: !!hit?.closest('[data-test=confirm-ok]') };
  });
  expect.soft(m.left, 'confirm inside the screen (left)').toBeGreaterThanOrEqual(0);
  expect.soft(m.right, 'confirm inside the screen (right)').toBeLessThanOrEqual(m.vw);
  expect.soft(m.bottom, 'confirm inside the screen (bottom)').toBeLessThanOrEqual(m.vh);
  expect.soft(m.okOnTop, 'OK is what a tap hits (above every sheet)').toBe(true);
  if (phone) expect.soft(m.minButton, 'buttons >= 44 px on a phone').toBeGreaterThanOrEqual(44);
}

for (const [label, w, h] of [['phone 390x844', 390, 844], ['iPad 820x1180', 820, 1180]] as const) {
  test.describe(label, () => {
    const phone = w < 760;

    test('Triggers: Delete asks in the page; Escape and Cancel keep it, OK deletes it; no browser dialog', async ({ browser }) => {
      const { page, ctx, errors, dialogs, calls } = await openTeam(browser, w, h);
      await openFromTeam(page, 'open-triggers');
      const row = page.locator('[data-test=trigger-nightly-check]');
      await expect(row).toBeVisible();

      // Escape: the confirm closes, the trigger stays, and the Triggers dialog under it stays open.
      await row.locator('[data-test=trigger-delete]').click();
      await expectConfirm(page, 'Delete the trigger nightly-check?', 'Delete', phone);
      await page.screenshot({ path: test.info().outputPath(`confirm-trigger-${w}.png`) });
      await page.keyboard.press('Escape');
      await expect(confirmBox(page)).toHaveCount(0);
      await expect(page.locator('[data-test=triggers-table]')).toBeVisible();
      await expect(row.locator('[data-test=trigger-delete]')).toBeFocused(); // the focus is back on the opener

      // Cancel keeps it.
      await row.locator('[data-test=trigger-delete]').click();
      await expectConfirm(page, 'Delete the trigger nightly-check?', 'Delete', phone);
      await page.locator('[data-test=confirm-cancel]').click();
      await expect(confirmBox(page)).toHaveCount(0);
      await page.waitForTimeout(300);
      expect(calls).not.toContain('triggers.delete');
      await expect(row).toBeVisible();

      // OK deletes it; the other stays.
      await row.locator('[data-test=trigger-delete]').click();
      await expectConfirm(page, 'Delete the trigger nightly-check?', 'Delete', phone);
      await page.locator('[data-test=confirm-ok]').click();
      await expect(row).toHaveCount(0);
      expect(calls.filter((c) => c === 'triggers.delete')).toHaveLength(1);
      await expect(page.locator('[data-test=trigger-weekly-report]')).toBeVisible();

      expect(dialogs, 'no browser dialog').toEqual([]);
      expect(errors).toEqual([]);
      await ctx.close();
    });

    test('a member: Remove agent asks in the page; Cancel keeps it, OK removes it; no browser dialog', async ({ browser }) => {
      const { page, ctx, errors, dialogs, calls } = await openTeam(browser, w, h);
      const pane = page.locator('[data-test=agent-TesterBen]');
      await expect(pane).toBeVisible();
      const ask = async () => {
        await pane.locator('[data-test=member-more]').click();
        await expect(pane.locator('[data-test=member-more-menu]')).toBeVisible();
        await pane.locator('[data-test=remove-agent]').click();
        await expectConfirm(page, 'Remove TesterBen from payments? Its queued instructions are dropped.', 'Remove', phone);
      };

      await ask();
      await page.screenshot({ path: test.info().outputPath(`confirm-member-${w}.png`) });
      await page.locator('[data-test=confirm-cancel]').click();
      await expect(confirmBox(page)).toHaveCount(0);
      await page.waitForTimeout(1500); // a refresh or two: still there
      expect(calls).not.toContain('members.remove');
      await expect(pane).toBeVisible();

      await ask();
      await page.locator('[data-test=confirm-ok]').click();
      await expect(confirmBox(page)).toHaveCount(0);
      await expect(pane).toHaveCount(0, { timeout: 10_000 });
      expect(calls.filter((c) => c === 'members.remove')).toHaveLength(1);
      await expect(page.locator('[data-test=agent-DeveloperAda]')).toBeVisible();

      expect(dialogs, 'no browser dialog').toEqual([]);
      expect(errors).toEqual([]);
      await ctx.close();
    });
  });
}
