// A read-only Visitor's Deliverables page in a real browser (headless Chrome), against the FAKE core of
// phone-remote.spec.ts (remote.html, canned answers). Opened at #deliverable=10: the Deliverables page with that deliverable's
// pane open. For a Visitor the pane has no Nudge, Stop or Close (nothing to tap, nothing called), and a call forced past
// the screen (bus.nudgeDeliverable, bus.closeDeliverable, bus.stopDeliverable on the page's own socket) is refused by the fake,
// as core's RemoteAccess refuses it. With full access the running one's Stop and Close are there and asked first.
////   npx playwright test deliverables-visitor      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser } from './phone/fixtures';
import { type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

async function open(browser: Browser, access: Access, w = 1280, h = 800) {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  s.core.setAccess(access);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#deliverable=10`);
  await expect(page.locator('[data-test=deliverable-pane][data-id="10"]')).toBeVisible({ timeout: 30_000 });
  return { s, page, ctx, errors };
}

for (const [w, h] of [[1280, 800], [375, 667]] as const) {
  test(`Visitor at ${w}x${h}, #deliverable=10: the pane has no Nudge, Stop or Close, and nothing is called`, async ({ browser }) => {
    const { s, page, ctx, errors } = await open(browser, 'read', w, h);
    const pane = page.locator('[data-test=deliverable-pane]');
    await expect(page.locator('[data-test=remote-access-chip]')).toBeVisible();
    await expect(pane.locator('[data-test=deliverable-pane-title]')).toHaveText('Partial refunds');
    await expect(pane.locator('[data-test=deliverable-pane-actions]')).toHaveCount(0);
    for (const b of ['deliverable-pane-nudge', 'deliverable-pane-stop', 'deliverable-pane-close-wf', 'deliverable-pane-extend']) await expect(pane.locator(`[data-test=${b}]`), b).toHaveCount(0);
    await expect(page.locator('[data-test=confirm-dialog]')).toHaveCount(0);
    expect(s.core.calls).not.toContain('bus.nudgeDeliverable');
    expect(s.core.calls).not.toContain('bus.closeDeliverable');
    expect(s.core.calls).not.toContain('bus.stopDeliverable');
    expect(errors).toEqual([]);
    await ctx.close();
  });
}
test('Visitor: a nudge or close forced on the page\'s socket is refused by the fake (as core refuses it)', async ({ browser }) => {
  const { s, page, ctx } = await open(browser, 'read');
  const replies = await page.evaluate(async () => {
    const url = new URL('rpc', location.href);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(url.href);
    await new Promise((ok, bad) => { ws.onopen = ok; ws.onerror = bad; });
    const got: Record<number, any> = {};
    const done = new Promise<void>((ok) => { ws.onmessage = (m) => { const d = JSON.parse(String(m.data)); if (d.id) got[d.id] = d; if (got[901] && got[902] && got[903]) ok(); }; });
    ws.send(JSON.stringify({ id: 901, method: 'bus.nudgeDeliverable', params: { teamId: 1, deliverable: 10 } }));
    ws.send(JSON.stringify({ id: 902, method: 'bus.closeDeliverable', params: { teamId: 1, deliverable: 10, reason: 'forced' } }));
    ws.send(JSON.stringify({ id: 903, method: 'bus.stopDeliverable', params: { teamId: 1, deliverable: 10 } }));
    await done;
    ws.close();
    return [got[901], got[902], got[903]];
  });
  for (const r of replies) expect(r.error?.code, JSON.stringify(r)).toBe('remote_refused');
  expect(s.core.calls).toEqual(expect.arrayContaining(['bus.nudgeDeliverable', 'bus.closeDeliverable', 'bus.stopDeliverable']));
  await ctx.close();
});

test('full access, #deliverable=10: the running one has Stop and Close, and Stop asks first (declined: nothing sent)', async ({ browser }) => {
  const { s, page, ctx, errors } = await open(browser, 'full');
  const pane = page.locator('[data-test=deliverable-pane]');
  await expect(page.locator('[data-test=remote-access-chip]')).toHaveCount(0);
  await expect(pane.locator('[data-test=deliverable-pane-stop]')).toBeEnabled();
  await expect(pane.locator('[data-test=deliverable-pane-close-wf]')).toBeEnabled();
  await expect(pane.locator('[data-test=deliverable-pane-nudge]')).toHaveCount(0); // a run is on it
  await pane.locator('[data-test=deliverable-pane-stop]').click();
  await expect(page.locator('[data-test=confirm-message]')).toHaveText('Stop what runs in #10 now? The deliverable stays open.');
  await expect(page.locator('[data-test=confirm-ok]')).toBeEnabled();
  await page.locator('[data-test=confirm-cancel]').click();
  await expect(page.locator('[data-test=confirm-dialog]')).toHaveCount(0);
  expect(s.core.calls).not.toContain('bus.stopDeliverable');
  expect(errors).toEqual([]);
  await ctx.close();
});