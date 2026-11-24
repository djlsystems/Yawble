// Card 492 (B110): Member settings > Policy in headless Chrome against the phone-remote FAKE core (remote.html,
// tests/e2e/phone), with the policy RPCs faked here (policy.getMember / previewLoosen / saveLoosen / saveOverride, the
// core's shapes; the merge is a small stand-in for PolicyMerger). No real program runs.
//  - 1280x800 and 375x812, light and dark: the remote view (no Browse…, text only).
//  - Folder access: add -> "This gives TesterBen read-write on: ..." -> Confirm -> the row shows with source "this member
//    (loosened)", the result says what changed and the Effective row is highlighted; close and reopen -> still there;
//    Remove -> confirm -> gone.
//  - Save override with no change says "Nothing changed." (the Override table is under "Advanced", collapsed).
//  - A relative path is warned about before Confirm.
//  - Text typed and not saved: "Unsaved changes", and closing asks "Discard?" (Cancel keeps the dialog, Discard closes it).
//
//   npx playwright test card492-folder-access --workers=1      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/B110 (else test-results/B110).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { answers, createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(150_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'B110');

/** The fake core's policy for TesterBen (member 3): the global policy, its override and loosening, merged roughly as the core does. */
function policyFake() {
  const global = {
    filesystem: { grantableRoots: ['C:/src'], readonly: ['C:/tools', 'C:/Program Files/Git'], readwrite: ['C:/scratch'], denied: ['C:/Users/pat/.ssh', 'C:/Users/pat/.aws'] },
    network: { allowHosts: ['api.github.com', 'registry.npmjs.org'] },
    compute: { wallTimeoutSec: 7200, idleTimeoutSec: 1800, memoryMb: null, cpuPercent: null, maxProcesses: null },
    ui: { clipboard: 'read' }, app: { maxConcurrentAgents: 4, backend: 'sandbox', heavyWorkAtOnce: 1 },
  };
  const implicit = { readwrite: ['C:/Users/pat/AppData/Local/Yawble/teams/payments/members/TesterBen/repo'], readonly: ['C:/Program Files/Yawble/bin'], denied: [] };
  const state = { override: {} as any, loosen: {} as any };
  const merge = (override: any, loosen: any) => {
    const rw = [...implicit.readwrite, ...(override.filesystem?.readwrite ?? global.filesystem.readwrite)];
    const ro = [...implicit.readonly, ...(override.filesystem?.readonly ?? global.filesystem.readonly)];
    const denied = [...global.filesystem.denied, ...(override.filesystem?.denied ?? [])];
    const loosened: string[] = [];
    for (const p of loosen.filesystem?.readonly ?? []) { if (![...ro, ...rw].includes(p)) { ro.push(p); loosened.push(`filesystem.readonly + ${p}`); } }
    for (const p of loosen.filesystem?.readwrite ?? []) { if (!rw.includes(p)) { rw.push(p); loosened.push(`filesystem.readwrite + ${p}`); } }
    const hosts = [...global.network.allowHosts];
    for (const h of loosen.network?.allowHosts ?? []) { if (!hosts.includes(h)) { hosts.push(h); loosened.push(`network.allowHosts + ${h}`); } }
    return { readonly: ro, readwrite: rw, denied, allowHosts: hosts, loosened, notes: [],
      compute: { ...global.compute, ...(override.compute ?? {}) }, clipboard: override.ui?.clipboard ?? global.ui.clipboard };
  };
  const view = () => {
    const e = merge(state.override, state.loosen);
    return { memberId: 3, global, override: state.override, loosen: state.loosen, loosenSummary: e.loosened.filter((l) => !l.startsWith('compute')),
      loosenApprovedAt: e.loosened.length ? new Date().toISOString() : null, teamHosts: [], effective: e, implicit, widening: [], violations: [],
      net: `model + bus + ${e.allowHosts.length} hosts`, backend: 'fake sandbox', machineMemoryMb: 32768 };
  };
  const clone = (x: unknown) => JSON.parse(JSON.stringify(x ?? {}));
  answers['members.get'] = () => ({ id: 3, teamId: 1, team: 'payments', name: 'TesterBen', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' });
  answers['policy.getMember'] = () => view();
  answers['policy.teamHosts'] = () => ({ teamId: 1, hosts: [] });
  answers['policy.checkHosts'] = (p: any) => (p?.hosts ?? []).map((host: string) => ({ host, problem: null }));
  answers['agents.models'] = () => ({ models: ['claude-opus-5-5', 'claude-sonnet-5-5'] });
  answers['agents.commandLine'] = () => ({ model: { label: 'default' }, headless: { argv: ['claude', '-p'] }, interactive: { argv: ['claude'] } });
  answers['policy.previewLoosen'] = (p: any) => ({ loosened: merge(state.override, p?.loosen ?? {}).loosened });
  answers['policy.saveLoosen'] = (p: any) => {
    const want = merge(state.override, p?.loosen ?? {}).loosened;
    if (JSON.stringify(want) !== JSON.stringify(p?.confirmed ?? [])) throw Object.assign(new Error('the confirmation does not match what this loosen block loosens; preview it again and confirm the new list'), { code: 'policy' });
    state.loosen = clone(p?.loosen);
    return view();
  };
  answers['policy.saveOverride'] = (p: any) => { state.override = clone(p?.override); return view(); };
  return state;
}

async function open(browser: Browser, size: { width: number; height: number }, theme: 'light' | 'dark') {
  const state = policyFake();
  const core = createFakeCore();
  core.setAsks(false);
  core.setAccess('full');
  const sent: { method: string; params: any }[] = [];
  const ctx = await browser.newContext({ viewport: size, colorScheme: theme, isMobile: size.width < 500, hasTouch: size.width < 500 });
  await ctx.addInitScript((t) => { try { localStorage.setItem('yawble.remoteTheme', t); } catch { /* none */ } }, theme);
  await ctx.route(`${ORIGIN}/**`, async (route) => {
    const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
    const file = path.resolve(builtDir, rel || 'remote.html');
    if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
    return route.fulfill({ path: file });
  });
  await ctx.routeWebSocket(/\/rpc$/, (ws) => {
    ws.onMessage((m) => {
      const msg = JSON.parse(String(m));
      sent.push({ method: msg.method, params: msg.params });
      const out = core.answer(String(m));
      if (out) ws.send(out);
    });
  });
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${ORIGIN}/remote.html#team=1`); // a link into the remote view opens the team (remoteHash.ts)
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  return { page, ctx, errors, sent, state };
}

async function openPolicy(page: Page) {
  const pane = page.locator('[data-test=agent-TesterBen]');
  await pane.locator('[data-test=member-more]').click();
  await pane.locator('[data-test=member-more-menu] [data-test=open-settings]').click();
  await page.locator('[data-test=tab-policy]').click();
  await expect(page.locator('[data-test=folder-access]')).toBeVisible();
}

const shot = (page: Page, name: string) => page.screenshot({ path: path.join(SHOTS, `${name}.png`) });
const rw = (page: Page) => page.locator('[data-test=folders-readwrite] [data-test=folder-row]');

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

const SIZES = [{ name: '1280', size: { width: 1280, height: 800 } }, { name: '375', size: { width: 375, height: 812 } }];
for (const { name, size } of SIZES) {
  for (const theme of ['light', 'dark'] as const) {
    test(`card492 folder access, save model and honest results at ${name} (${theme})`, async ({ browser }) => {
      const { page, ctx, errors, sent, state } = await open(browser, size, theme);
      const tag = `${name}-${theme}`;
      await openPolicy(page);
      // The remote view: text only, no Browse….
      await expect(page.locator('[data-test=folder-add-path]')).toBeVisible();
      await expect(page.locator('[data-test=folder-browse]')).toHaveCount(0);
      await expect(rw(page)).toHaveCount(2);
      await expect(page.locator('[data-test=folders-readwrite] [data-source=global]')).toContainText('C:/scratch');
      await expect(page.locator('[data-test=policy-advanced]')).not.toHaveAttribute('open', '');
      await shot(page, `01-policy-${tag}`);
      // Nothing in the Policy tab makes the dialog scroll sideways (a phone included).
      const sideways = await page.locator('[role=dialog] .body').evaluate((b) => b.scrollWidth - b.clientWidth);
      expect(sideways).toBeLessThanOrEqual(1);

      // Add -> confirm -> saved, with its source; what changed is said and highlighted.
      await page.locator('[data-test=folder-add-path]').fill('C:\\work\\shared');
      await expect(page.locator('[data-test=folder-unsaved]')).toHaveText('Unsaved changes');
      await page.locator('[data-test=folder-add]').click();
      await expect(page.locator('[data-test=folder-confirm-title]')).toHaveText('This gives TesterBen read-write on:');
      await expect(page.locator('[data-test=folder-confirm-item]')).toHaveText(['C:\\work\\shared']);
      expect(sent.some((s) => s.method === 'policy.saveLoosen')).toBe(false);
      await page.locator('[data-test=folder-confirm]').scrollIntoViewIfNeeded();
      await shot(page, `02-add-confirm-${tag}`);
      await page.locator('[data-test=folder-confirm]').click();
      await expect(page.locator('[data-test=settings-saved]')).toContainText('Read/write now:');
      await expect(page.locator('[data-test=settings-saved]')).toContainText('C:\\work\\shared');
      const added = page.locator('[data-test=folders-readwrite] [data-test=folder-row][data-path="C:\\\\work\\\\shared"]');
      await expect(added.locator('[data-test=folder-source]')).toHaveText('this member (loosened)');
      await expect(added).toHaveAttribute('data-changed', 'true');
      await expect(page.locator('[data-test=row-filesystem-readwrite]')).toHaveAttribute('data-changed', 'true');
      expect(state.loosen).toEqual({ filesystem: { readwrite: ['C:\\work\\shared'] } });
      await page.locator('[data-test=settings-saved]').scrollIntoViewIfNeeded();
      await shot(page, `03-added-${tag}`);
      await page.locator('[data-test=row-filesystem-readwrite]').scrollIntoViewIfNeeded();
      await shot(page, `04-effective-highlight-${tag}`);

      // Nothing typed: Close closes at once. Reopen: still there.
      await page.locator('[data-test=modal-close]').click();
      await expect(page.locator('[data-test=confirm-dialog]')).toHaveCount(0);
      await expect(page.locator('[data-test=folder-access]')).toHaveCount(0);
      await openPolicy(page);
      await expect(added.locator('[data-test=folder-source]')).toHaveText('this member (loosened)');

      // Save override with no change: "Nothing changed." (never "Override saved").
      await page.locator('[data-test=policy-advanced-toggle]').click();
      await expect(page.locator('[data-test=save-override]')).toBeVisible();
      await page.locator('[data-test=save-override]').click();
      await expect(page.locator('[data-test=settings-saved]')).toHaveText('Nothing changed.');
      await page.locator('[data-test=policy-advanced-toggle]').click();

      // A relative path is warned about before Confirm.
      await page.locator('[data-test=folder-add-path]').fill('shared');
      await page.locator('[data-test=folder-add]').click();
      await expect(page.locator('[data-test=folder-warning]')).toContainText('"shared" is not a full path');
      await expect(page.locator('[data-test=folder-confirm]')).toHaveText('Confirm anyway');
      await page.locator('[data-test=folder-confirm]').scrollIntoViewIfNeeded();
      await shot(page, `05-relative-warning-${tag}`);
      await page.locator('[data-test=folder-cancel]').click();
      await page.locator('[data-test=folder-add-path]').fill('');

      // Remove -> confirm -> gone.
      await added.locator('[data-test=folder-remove]').click();
      await expect(page.locator('[data-test=folder-confirm-title]')).toHaveText("This takes away TesterBen's read-write access to:");
      await page.locator('[data-test=folder-confirm]').click();
      await expect(added).toHaveCount(0);
      await expect(rw(page)).toHaveCount(2);
      expect(state.loosen).toEqual({});

      // Typed and not saved: "Unsaved changes"; Close asks "Discard?". Cancel keeps it, Discard closes.
      await page.locator('[data-test=loosen-network-allowHosts]').fill('pypi.org');
      await expect(page.locator('[data-test=loosen-unsaved]')).toHaveText('Unsaved changes');
      await page.locator('[data-test=modal-close]').click();
      await expect(page.locator('[data-test=confirm-message]')).toHaveText('Policy has unsaved changes. Discard?');
      await shot(page, `06-discard-${tag}`);
      await page.locator('[data-test=confirm-cancel]').click();
      await expect(page.locator('[data-test=folder-access]')).toBeVisible();
      await page.locator('[data-test=modal-close]').click();
      await page.locator('[data-test=confirm-ok]').click();
      await expect(page.locator('[data-test=folder-access]')).toHaveCount(0);
      expect(sent.filter((s) => s.method === 'policy.saveLoosen')).toHaveLength(2);

      expect(errors).toEqual([]);
      await ctx.close();
    });
  }
}
