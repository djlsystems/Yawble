// A read-only Visitor's workflows dialog in a real browser (headless Chrome), against the FAKE core of
// phone-remote.spec.ts (remote.html, canned answers). Opened at #workflow=10: the team's page with its workflows and
// that workflow selected. For a Visitor its Nudge and Close are disabled (v-change, the read-only title), and a call
// forced past the screen (bus.nudgeWorkflow, bus.closeWorkflow on the page's own socket) is refused by the fake, as
// core's RemoteAccess refuses it. With full access the same buttons are enabled.
//
//   npx playwright test workflows-visitor      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
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
  await page.goto(`${s.url}#workflow=10`);
  await expect(page.locator('[data-test=workflow-10]')).toBeVisible({ timeout: 30_000 });
  return { s, page, ctx, errors };
}

for (const [w, h] of [[1280, 800], [375, 667]] as const) {
  test(`Visitor at ${w}x${h}, #workflow=10: Nudge and Close are disabled with the read-only title`, async ({ browser }) => {
    const { s, page, ctx, errors } = await open(browser, 'read', w, h);
    const wf = page.locator('[data-test=workflow-10]');
    await expect(wf).toHaveAttribute('aria-current', 'true');
    await expect(page.locator('[data-test=remote-access-chip]')).toBeVisible();
    for (const b of ['workflow-nudge', 'workflow-close']) {
      const btn = wf.locator(`[data-test=${b}]`);
      await expect(btn, b).toBeDisabled();
      await expect(btn, b).toHaveAttribute('title', /read-only access/);
    }
    // A tap on a disabled button opens nothing: no note field, no confirm button, no call.
    await wf.locator('[data-test=workflow-nudge]').click({ force: true });
    await expect(wf.locator('[data-test=workflow-text]')).toHaveCount(0);
    await expect(wf.locator('[data-test=workflow-confirm]')).toHaveCount(0);
    expect(s.core.calls).not.toContain('bus.nudgeWorkflow');
    expect(s.core.calls).not.toContain('bus.closeWorkflow');
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
    ws.send(JSON.stringify({ id: 901, method: 'bus.nudgeWorkflow', params: { teamId: 1, workflow: 10 } }));
    ws.send(JSON.stringify({ id: 902, method: 'bus.closeWorkflow', params: { teamId: 1, workflow: 10, reason: 'forced' } }));
    ws.send(JSON.stringify({ id: 903, method: 'bus.stopWorkflow', params: { teamId: 1, workflow: 10 } }));
    await done;
    ws.close();
    return [got[901], got[902], got[903]];
  });
  for (const r of replies) expect(r.error?.code, JSON.stringify(r)).toBe('remote_refused');
  expect(s.core.calls).toEqual(expect.arrayContaining(['bus.nudgeWorkflow', 'bus.closeWorkflow', 'bus.stopWorkflow']));
  await ctx.close();
});

test('full access, #workflow=10: Nudge and Close are enabled and Nudge opens its confirm button, enabled', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 'full');
  const wf = page.locator('[data-test=workflow-10]');
  await expect(page.locator('[data-test=remote-access-chip]')).toHaveCount(0);
  await expect(wf.locator('[data-test=workflow-nudge]')).toBeEnabled();
  await expect(wf.locator('[data-test=workflow-close]')).toBeEnabled();
  await wf.locator('[data-test=workflow-nudge]').click();
  await expect(wf.locator('[data-test=workflow-confirm]')).toBeEnabled();
  expect(errors).toEqual([]);
  await ctx.close();
});
