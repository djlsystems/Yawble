// B091 (card 484): the `test` every phone spec takes (instead of @playwright/test's), and everything else from
// @playwright/test unchanged (expect, devices, the types), so a spec changes only the module it imports from.
//
// Why: the phone specs open their own contexts with browser.newContext() (a phone viewport, isMobile, deviceScaleFactor 2)
// on the worker's browser, which lives as long as the worker and is shared by every test and spec file it runs. Most of
// phone-remote.spec.ts never closed them, and a test that fails before its last `ctx.close()` leaves one too; each holds
// a renderer process with the built app loaded. They piled up until the worker restarted (only after a failure), which is
// what took Chromium to 7.2-7.5 GB with 2 phone workers in B082 and crashed newPage ("Target crashed").
//
// This auto fixture closes, after every test, each context the test opened and did not close (the ones open before it
// started, such as a beforeAll's, are left alone). B091_CONTEXT_LOG=1 prints how many it had to close.
import { test as base } from '@playwright/test';

export * from '@playwright/test';

export const test = base.extend<{ _closeLeftContexts: void }>({
  _closeLeftContexts: [async ({ browser }, use, testInfo) => {
    const before = new Set(browser.contexts());
    await use();
    const left = browser.contexts().filter((c) => !before.has(c));
    if (left.length && process.env.B091_CONTEXT_LOG === '1') console.log(`[b091] closing ${left.length} context(s) left open by ${testInfo.titlePath.join(' > ')}`);
    await Promise.all(left.map((c) => c.close().catch(() => { /* already closed or the browser has gone */ })));
  }, { auto: true }],
});
