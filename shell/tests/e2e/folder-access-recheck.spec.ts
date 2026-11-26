// Folder access re-check: Member settings > Policy > Folder access in headless Chrome
// against the phone-remote FAKE core (remote.html), the policy RPCs faked here with a merge that folds "." / ".." as
// PolicyPaths.Resolve does. No real program runs.
//  - A typed path, read-only mode and a pending confirm survive General and back; Close asks "Discard?"; Confirm after the
//    round trip saves what it showed.
//  - A folder typed with ".." is listed once; Remove takes it out (stored loosen back to empty).
//
//   npx playwright test folder-access-recheck --workers=1      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { answers, createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(150_000);


/** PolicyPaths.Resolve on Windows, lexically: "/" -> "\", drive upper case, "." and ".." folded, never above C:\. */
function resolve(raw: string): string {
  const p = raw.trim().replace(/\//g, '\\');
  const m = /^([a-zA-Z]):\\(.*)$/.exec(p);
  if (!m) return p;
  const out: string[] = [];
  for (const seg of m[2].split('\\')) { if (seg === '' || seg === '.') continue; if (seg === '..') out.pop(); else out.push(seg); }
  return `${m[1].toUpperCase()}:\\${out.join('\\')}`;
}

function policyFake() {
  const global = {
    filesystem: { grantableRoots: ['C:\\src'], readonly: ['C:\\tools'], readwrite: ['C:\\scratch'], denied: ['C:\\Users\\pat\\.ssh'] },
    network: { allowHosts: ['api.github.com'] },
    compute: { wallTimeoutSec: 7200, idleTimeoutSec: 1800, memoryMb: null, cpuPercent: null, maxProcesses: null },
    ui: { clipboard: 'read' }, app: { maxConcurrentAgents: 4, backend: 'sandbox', heavyWorkAtOnce: 1 },
  };
  const implicit = { readwrite: ['C:\\clone'], readonly: [], denied: [] };
  const state = { override: {} as any, loosen: {} as any };
  const merge = (override: any, loosen: any) => {
    const rw = [...implicit.readwrite, ...global.filesystem.readwrite];
    const ro = [...global.filesystem.readonly];
    const loosened: string[] = [];
    for (const r of loosen.filesystem?.readonly ?? []) { const p = resolve(r); if (![...ro, ...rw].includes(p)) { ro.push(p); loosened.push(`filesystem.readonly + ${p}`); } }
    for (const r of loosen.filesystem?.readwrite ?? []) { const p = resolve(r); if (!rw.includes(p)) { rw.push(p); loosened.push(`filesystem.readwrite + ${p}`); } }
    return { readonly: ro, readwrite: rw, denied: [...global.filesystem.denied], allowHosts: [...global.network.allowHosts], loosened, notes: [],
      compute: { ...global.compute, ...(override.compute ?? {}) }, clipboard: global.ui.clipboard };
  };
  const view = () => {
    const e = merge(state.override, state.loosen);
    return { memberId: 3, global, override: state.override, loosen: state.loosen, loosenSummary: e.loosened,
      loosenApprovedAt: e.loosened.length ? new Date().toISOString() : null, teamHosts: [], effective: e, implicit, widening: [], violations: [],
      net: `model + bus + ${e.allowHosts.length} hosts`, backend: 'fake sandbox', machineMemoryMb: 32768 };
  };
  const clone = (x: unknown) => JSON.parse(JSON.stringify(x ?? {}));
  answers['members.get'] = () => ({ id: 3, teamId: 1, team: 'payments', name: 'TesterBen', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' });
  answers['policy.getMember'] = () => view();
  answers['policy.teamHosts'] = () => ({ teamId: 1, hosts: [] });
  answers['policy.checkHosts'] = (p: any) => (p?.hosts ?? []).map((host: string) => ({ host, problem: null }));
  answers['agents.models'] = () => ({ models: ['claude-opus-5-5'] });
  answers['agents.commandLine'] = () => ({ model: { label: 'default' }, headless: { argv: ['claude', '-p'] }, interactive: { argv: ['claude'] } });
  answers['policy.previewLoosen'] = (p: any) => ({ loosened: merge(state.override, p?.loosen ?? {}).loosened });
  answers['policy.saveLoosen'] = (p: any) => {
    const want = merge(state.override, p?.loosen ?? {}).loosened;
    if (JSON.stringify(want) !== JSON.stringify(p?.confirmed ?? [])) throw Object.assign(new Error('the confirmation does not match what this loosen block loosens'), { code: 'policy' });
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
  await page.goto(`${ORIGIN}/remote.html#team=1`);
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
async function generalAndBack(page: Page) {
  await page.locator('[data-test=tab-general]').click();
  await expect(page.locator('[data-test=folder-access]')).toHaveCount(0);
  await page.locator('[data-test=tab-policy]').click();
  await expect(page.locator('[data-test=folder-access]')).toBeVisible();
}
const rwPaths = (page: Page) => page.locator('[data-test=folders-readwrite] [data-test=folder-row] [data-test=folder-path]');

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const RUNS = [{ name: '1280-light', size: { width: 1280, height: 800 }, theme: 'light' as const }, { name: '375-dark', size: { width: 375, height: 812 }, theme: 'dark' as const }];
for (const { name, size, theme } of RUNS) {
  test(`folder access re-check: tab round trip and ".." at ${name}`, async ({ browser }) => {
    const { page, ctx, errors, sent, state } = await open(browser, size, theme);
    await openPolicy(page);

    // 1. Typed path + read-only + pending confirm survive General and back.
    await page.locator('[data-test=folder-add-mode]').selectOption('readonly');
    await page.locator('[data-test=folder-add-path]').fill('C:\\work\\shared');
    await page.locator('[data-test=folder-add]').click();
    await expect(page.locator('[data-test=folder-confirm-title]')).toHaveText('This gives TesterBen read-only on:');
    await generalAndBack(page);
    await expect(page.locator('[data-test=folder-add-path]')).toHaveValue('C:\\work\\shared');
    await expect(page.locator('[data-test=folder-add-mode]')).toHaveValue('readonly');
    await expect(page.locator('[data-test=folder-confirm-title]')).toHaveText('This gives TesterBen read-only on:');
    await expect(page.locator('[data-test=folder-confirm-item]')).toHaveText(['C:\\work\\shared']);
    await page.locator('[data-test=folder-confirm]').scrollIntoViewIfNeeded();
    // Close asks; Cancel keeps it.
    await page.locator('[data-test=modal-close]').click();
    await expect(page.locator('[data-test=confirm-dialog]')).toContainText('Policy has unsaved changes. Discard?');
    await page.locator('[data-test=confirm-dialog] [data-test=confirm-cancel]').click();
    await expect(page.locator('[data-test=confirm-dialog]')).toHaveCount(0);
    await expect(page.locator('[data-test=folder-access]')).toBeVisible();
    expect(sent.some((s) => s.method === 'policy.saveLoosen')).toBe(false);
    // Confirm saves what it showed.
    await page.locator('[data-test=folder-confirm]').click();
    await expect(page.locator('[data-test=settings-saved]')).toContainText('C:\\work\\shared');
    expect(state.loosen).toEqual({ filesystem: { readonly: ['C:\\work\\shared'] } });
    await expect(page.locator('[data-test=folder-add-path]')).toHaveValue('');
    await expect(page.locator('[data-test=folders-readonly] [data-test=folder-row][data-path="C:\\\\work\\\\shared"] [data-test=folder-source]')).toHaveText('this member (loosened)');

    // 2. A folder typed with "..": listed once, Remove takes it out.
    await page.locator('[data-test=folder-add-mode]').selectOption('readwrite');
    await page.locator('[data-test=folder-add-path]').fill('D:\\work\\x\\..\\data');
    await page.locator('[data-test=folder-add]').click();
    await expect(page.locator('[data-test=folder-confirm-item]')).toHaveText(['D:\\work\\data']);
    await page.locator('[data-test=folder-confirm]').click();
    await expect(page.locator('[data-test=settings-saved]')).toContainText('D:\\work\\data');
    expect(state.loosen.filesystem.readwrite).toEqual(['D:\\work\\x\\..\\data']);
    await expect(rwPaths(page)).toHaveText(['C:\\clone', 'C:\\scratch', 'D:\\work\\data']);
    // Close and reopen: still listed once.
    await page.locator('[data-test=modal-close]').click();
    await expect(page.locator('[data-test=confirm-dialog]')).toHaveCount(0);
    await openPolicy(page);
    await expect(rwPaths(page)).toHaveText(['C:\\clone', 'C:\\scratch', 'D:\\work\\data']);
    await page.locator('[data-test=folders-readwrite]').scrollIntoViewIfNeeded();
    const row = page.locator('[data-test=folders-readwrite] [data-test=folder-row][data-path="D:\\\\work\\\\data"]');
    await row.locator('[data-test=folder-remove]').click();
    await expect(page.locator('[data-test=folder-confirm-title]')).toHaveText("This takes away TesterBen's read-write access to:");
    await page.locator('[data-test=folder-confirm]').click();
    await expect(page.locator('[data-test=settings-saved]')).not.toHaveText('Nothing changed.');
    await expect(rwPaths(page)).toHaveText(['C:\\clone', 'C:\\scratch']);
    expect(state.loosen).toEqual({ filesystem: { readonly: ['C:\\work\\shared'] } });
    expect(errors).toEqual([]);
    await ctx.close();
  });
}
