// Card 356 (verify card 355): gap tests for the in-page confirm (confirm.ts / ConfirmDialog.vue) at every former
// window.confirm site, in headless Chrome against the FAKE core of phone-remote.spec.ts (remote.html built by Vite).
// Sizes: phone 390x844 and iPad 1024x1366 (the remote view), and 1280x800 "desktop": the same build with
// window.yawble.remote forced to false, which is what the desktop renderer has (Electron preload, no `remote`): only
// there do the Connections list and the "Also delete the connection" follow-up exist (both are skipped when remote).
// For each site:
//  - the confirm shows the call site's message and named OK, fits the screen (nothing cut, nothing outside), OK has
//    the focus and is what a tap hits; buttons >= 44 px on a phone;
//  - Tab and Shift+Tab keep the focus inside it;
//  - Escape and Cancel close it, make no API call and leave the screen as it was (the dialog under it stays open);
//    the focus goes back to the opener;
//  - a click on the backdrop cancels too;
//  - OK does the action (the API call is made once);
//  - no browser dialog (page 'dialog' event) and no page error at any point.
// Sites: triggers delete, team member remove, skill delete, task delete, CLI integration remove, API remove (+ the
// "Also delete the connection" follow-up on desktop), MCP server remove (+ follow-up on desktop), connection remove
// (desktop), Discover scope switch with unsaved changes, IntegrationDialog Close with unsaved changes.
//
//   npx playwright test card356-confirm-sites-gaps   (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
//   screenshots: $YAWBLE_TEAM_DOCS/C-B070-screens (or test-results/C-B070-screens)
import { expect, test, type Browser, type Locator, type Page } from './phone/fixtures';
import path from 'node:path';
import { mkdirSync } from 'node:fs';
import { answers } from './phone/fake-core';
import { buildRenderer, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(180_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'C-B070-screens');

const iso = (min: number) => new Date(Date.now() + min * 60_000).toISOString();
const trigger = (id: number, name: string) => ({
  id, teamId: 1, memberId: 2, name, schedule: { kind: 'every', intervalSec: 3600, cron: null, timezone: null, fireAt: null },
  instruction: 'Check the build.', onlyWhenIdle: false, enabled: true, nextDueAt: iso(30), lastFiredAt: null, wakeManager: 'always',
  lastOutcome: null, lastReason: null, lastSeq: null, missedCount: 0,
});
const counts = { allowed: 1, ask: 1, off: 0, undecided: 0 };
const cli = { name: 'gh', label: 'GitHub', description: 'GitHub CLI', known: true, configured: true, enabled: true, all: true, teams: [], path: 'C:\\bin\\gh.exe',
  version: '2.45.0', status: 'signed in as me', floors: [], login: 'gh auth login', counts, preset: [], rules: [] };
const apiRow = { kind: 'http', name: 'stripe', baseUrl: 'https://api.stripe.example/', docsUrl: null, specUrl: null, title: 'Stripe',
  connection: { id: 3, name: 'Stripe test key', status: 'works', statusReason: null }, auth: { type: 'bearer', name: null, description: null }, signIn: null,
  status: null, all: true, teams: [], counts };
const mcp = { name: 'files', command: 'npx', args: ['-y', 'files-mcp'], status: 'running', all: true, teams: [], transport: 'stdio', url: null, auth: null, env: [],
  connection: { id: 4, name: 'Files token', status: 'works', statusReason: null }, sources: [], counts, tools: [], rules: [] };
const conn = (id: number, name: string, usedBy: string[]) => ({ id, name, kind: 'token', fields: [{ key: 'token', label: 'Token', secret: true, set: true }],
  status: 'works', statusReason: null, testedAt: iso(-60), usedBy, usedByTasks: [] });
const node = { path: 'pr-list', parent: '', name: 'pr-list', depth: 1, summary: 'List pull requests', hasChildren: false, class: 'readonly', source: 'help', reason: '',
  state: 'free', ticked: false, tickedFrom: null, defaultTick: false, decision: null, gone: false, badge: null, seenVersion: null, suggested: 'allow',
  defaultDecision: null, teamDecision: null, effective: 'deny', origin: 'discovered' };
const checklist = (p: any) => ({ cli: 'gh', teamId: p?.teamId ?? null, version: '2.45.0', discoveredAt: iso(-600), runs: 1, partial: false, partialReason: null, error: null,
  new: 0, changed: 0, gone: 0, choices: 0, kind: 'cli', offeredAll: true, offeredTeams: [], counts, nodes: [structuredClone(node)], otherRules: [] });
const task = { id: 7, def: { name: 'e2e', repo: 'main', program: 'npm', args: ['test'], workdir: '.', timeoutSeconds: 600, envNames: [], watch: [], pinMode: 'files',
  commandLineOnly: false, envFromConnections: [] }, commandLine: 'npm test', approval: 'asks', approvedAt: null, approvedCommit: null, approvedBy: null,
  programResolved: 'C:/bin/npm.cmd', commit: null, pinned: [], warnings: [], problem: null, runningRunId: null, pendingAskId: null, lastRun: null, updatedAt: iso(-60), updatedBy: 'you' };

/** The params of every call to a method this file fakes, in order. */
let made: { method: string; p: any }[] = [];
let triggers: ReturnType<typeof trigger>[] = [];
let removed = new Set<number>();
const rec = (method: string, answer: (p: any) => unknown) => (p: any) => { made.push({ method, p }); return answer(p); };
const OVERRIDES: Record<string, (p?: any) => unknown> = {
  'triggers.list': () => triggers,
  'triggers.delete': rec('triggers.delete', (p) => { triggers = triggers.filter((t) => t.id !== p?.id); return { ok: true }; }),
  'members.remove': rec('members.remove', (p) => { removed.add(p?.memberId); return { ok: true }; }),
  'skills.delete': rec('skills.delete', () => ({ ok: true })),
  'tasks.list': () => ({ tasks: made.some((m) => m.method === 'tasks.delete') ? [] : [task], repos: ['main'] }),
  'tasks.runs': () => [],
  'tasks.delete': rec('tasks.delete', () => ({ ok: true })),
  'integrations.list': () => ({ integrations: made.some((m) => m.method === 'integrations.remove') ? [] : [cli], pendingAsks: 0 }),
  'integrations.remove': rec('integrations.remove', () => ({ ok: true })),
  'integrations.apiList': () => (made.some((m) => m.method === 'integrations.apiRemove') ? [] : [apiRow]),
  'integrations.apiRemove': rec('integrations.apiRemove', () => ({ ok: true })),
  'integrations.mcpList': () => (made.some((m) => m.method === 'integrations.mcpRemove') ? [] : [mcp]),
  'integrations.mcpRemove': rec('integrations.mcpRemove', () => ({ ok: true })),
  'integrations.connections': () => [conn(3, 'Stripe test key', ['stripe']), conn(4, 'Files token', ['MCP: files']), conn(5, 'Spare token', [])]
    .filter((c) => !made.some((m) => m.method === 'integrations.connectionRemove' && m.p?.id === c.id)),
  'integrations.connectionRemove': rec('integrations.connectionRemove', () => ({ ok: true })),
  'integrations.checklist': rec('integrations.checklist', checklist),
  'integrations.discoverStatus': () => ({ state: 'idle' }),
  'integrations.tasks': () => [],
};
const saved: Record<string, unknown> = {};

test.beforeAll(async () => {
  test.setTimeout(900_000);
  mkdirSync(SHOTS, { recursive: true });
  const agents = answers['teams.agents']!;
  saved['teams.agents'] = agents;
  (answers as any)['teams.agents'] = (p: any, a: any) => (agents(p, a) as { member: { id: number } }[]).filter((c) => !removed.has(c.member.id));
  for (const k of Object.keys(OVERRIDES)) { saved[k] = (answers as any)[k]; (answers as any)[k] = OVERRIDES[k]; }
  await buildRenderer();
});
test.afterAll(() => { for (const k of Object.keys(saved)) { if (saved[k] === undefined) delete (answers as any)[k]; else (answers as any)[k] = saved[k]; } });
test.beforeEach(() => { triggers = [trigger(41, 'nightly-check'), trigger(42, 'weekly-report')]; removed = new Set(); made = []; });

type Size = { label: string; w: number; h: number; desktop: boolean };
const SIZES: Size[] = [
  { label: 'phone 390x844', w: 390, h: 844, desktop: false },
  { label: 'iPad 1024x1366', w: 1024, h: 1366, desktop: false },
  { label: 'desktop 1280x800', w: 1280, h: 800, desktop: true },
];

async function open(browser: Browser, s: Size, hash = '') {
  const srv = fakeRemote();
  srv.core.setAsks(false);
  const mobile = s.w < 760;
  const ctx = await browser.newContext({ viewport: { width: s.w, height: s.h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 2 });
  await srv.attach(ctx);
  if (s.desktop) {
    // The desktop renderer's window.yawble has no `remote`: keep the transport, drop the flag.
    await ctx.addInitScript(() => {
      let v: unknown;
      Object.defineProperty(window, 'yawble', { configurable: true, get: () => v, set: (x) => { v = { ...(x as object), remote: false }; } });
    });
  }
  const page = await ctx.newPage();
  const errors: string[] = [];
  const dialogs: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('dialog', (d) => { dialogs.push(`${d.type()}: ${d.message()}`); void d.dismiss(); });
  await page.goto(`${srv.url}${hash}`);
  return { page, ctx, errors, dialogs };
}

async function openTeam(page: Page) {
  // The remote view opens #team=1 by itself; the desktop renderer has no hash link: open it from Teams.
  const title = page.locator('[data-test=team-title]');
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  if (!(await page.evaluate(() => !!(window as any).yawble?.remote))) {
    await page.locator('[data-test=sidebar] [data-test=nav-teams]').first().click();
    await page.locator('[data-test=team-open-payments]').first().click();
  }
  await expect(title).toBeVisible({ timeout: 30_000 });
}
async function openFromTeam(page: Page, id: string) {
  const b = page.locator(`[data-test=${id}]`).first();
  if (!(await b.isVisible().catch(() => false))) {
    await page.locator('[data-test=team-more]').click();
    await expect(page.locator('[data-test=team-more-menu]')).toBeVisible();
  }
  await b.click();
  await expect(page.locator('[role=dialog]').last()).toBeVisible();
}
async function openSettings(page: Page, section: 'open-skills' | 'open-integrations') {
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  // B115: Settings is the gear at the top right (the desktop window, the remote view and the phone top bar).
  await page.getByTestId('settings-gear').click();
  await expect(page.locator('[data-test=settings]')).toBeVisible();
  await page.locator(`[data-test=settings] [data-test=${section}]`).first().click();
  await page.waitForTimeout(400);
}

const box = (page: Page) => page.locator('[data-test=confirm-dialog]');

/** The confirm is shown as asked, fits, has the focus, is on top; Tab / Shift+Tab stay inside. Saves a screenshot. */
async function expectConfirm(page: Page, s: Size, site: string, message: string | RegExp, okLabel: string, shot: boolean) {
  await expect(box(page)).toBeVisible();
  await expect(page.locator('[data-test=confirm-message]')).toHaveText(message);
  await expect(page.locator('[data-test=confirm-ok]')).toHaveText(okLabel);
  await expect(page.locator('[data-test=confirm-ok]')).toBeFocused();
  const m = await page.evaluate(() => {
    const d = document.querySelector('[data-test=confirm-dialog]')!;
    const r = d.getBoundingClientRect();
    const msg = document.querySelector<HTMLElement>('[data-test=confirm-message]')!;
    const buttons = [...d.querySelectorAll<HTMLButtonElement>('button')];
    const ok = document.querySelector<HTMLElement>('[data-test=confirm-ok]')!.getBoundingClientRect();
    const hit = document.elementFromPoint(ok.left + ok.width / 2, ok.top + ok.height / 2);
    const inside = (e: Element) => { const b = e.getBoundingClientRect(); return b.left >= r.left - 0.5 && b.right <= r.right + 0.5 && b.top >= r.top - 0.5 && b.bottom <= r.bottom + 0.5; };
    return {
      left: r.left, right: r.right, top: r.top, bottom: r.bottom, vw: innerWidth, vh: innerHeight,
      msgCut: msg.scrollWidth > msg.clientWidth + 1 || msg.scrollHeight > msg.clientHeight + 1,
      buttonCut: buttons.some((b) => b.scrollWidth > b.clientWidth + 1),
      allInside: [msg, ...buttons].every(inside),
      minButton: Math.min(...buttons.map((b) => b.getBoundingClientRect().height)),
      okOnTop: !!hit?.closest('[data-test=confirm-ok]'),
      pageWide: document.documentElement.scrollWidth > innerWidth + 1,
    };
  });
  const at = `${site} @${s.w}x${s.h}`;
  expect.soft(m.left, `${at}: inside the screen (left)`).toBeGreaterThanOrEqual(0);
  expect.soft(m.top, `${at}: inside the screen (top)`).toBeGreaterThanOrEqual(0);
  expect.soft(m.right, `${at}: inside the screen (right)`).toBeLessThanOrEqual(m.vw);
  expect.soft(m.bottom, `${at}: inside the screen (bottom)`).toBeLessThanOrEqual(m.vh);
  expect.soft(m.msgCut, `${at}: the message is not cut`).toBe(false);
  expect.soft(m.buttonCut, `${at}: no button label is cut`).toBe(false);
  expect.soft(m.allInside, `${at}: message and buttons inside the box`).toBe(true);
  expect.soft(m.okOnTop, `${at}: OK is what a tap hits`).toBe(true);
  expect.soft(m.pageWide, `${at}: the page does not scroll sideways`).toBe(false);
  if (s.w < 760) expect.soft(m.minButton, `${at}: buttons >= 44 px on a phone`).toBeGreaterThanOrEqual(44);
  if (shot) await page.screenshot({ path: path.join(SHOTS, `${site}-${s.w}x${s.h}.png`) });

  // The focus stays inside, both ways round.
  for (const k of ['Tab', 'Tab', 'Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab']) {
    await page.keyboard.press(k);
    const inside = await page.evaluate(() => !!document.activeElement?.closest('[data-test=confirm-dialog]'));
    expect.soft(inside, `${at}: after ${k} the focus is inside the confirm`).toBe(true);
  }
  await page.locator('[data-test=confirm-ok]').focus();
}

interface Site {
  id: string;
  /** Gets the page to where `opener` is shown. */
  setup: (page: Page) => Promise<void>;
  opener: (page: Page) => Locator;
  /** Presses the opener (a select's change for the scope switch). */
  ask?: (page: Page) => Promise<void>;
  message: string | RegExp;
  ok: string;
  /** The method OK calls (counted in `made`); or `okDone` checks what OK did. */
  method?: string;
  okDone?: (page: Page) => Promise<void>;
  /** Checks the screen is as it was after Escape / Cancel / the backdrop. */
  kept: (page: Page) => Promise<void>;
  /** The focus returns to this (default: the opener). */
  focusBack?: (page: Page) => Locator;
  desktopOnly?: boolean;
  /** Runs after OK and the check (the follow-up). */
  after?: (page: Page, s: Size) => Promise<void>;
}

const tabs = (tab: string) => async (page: Page) => { await openSettings(page, 'open-integrations'); await page.locator(`[data-test=${tab}]`).click(); await page.waitForTimeout(300); };
const editGh = async (page: Page) => {
  await openSettings(page, 'open-integrations');
  await page.locator('[data-test=integration-edit-gh]').click();
  await expect(page.locator('[data-test=integration-dialog]')).toBeVisible();
  await expect(page.locator('[data-test=tick-pr-list]')).toBeVisible();
  await page.locator('[data-test=tick-pr-list]').check();
  await expect(page.locator('[data-test=discover-save]')).toContainText('1 change');
};
const checklistCalls = () => made.filter((m) => m.method === 'integrations.checklist');

const SITES: Site[] = [
  {
    id: 'trigger-delete',
    setup: async (page) => { await openTeam(page); await openFromTeam(page, 'open-triggers'); },
    opener: (page) => page.locator('[data-test=trigger-nightly-check] [data-test=trigger-delete]'),
    message: 'Delete the trigger nightly-check?', ok: 'Delete', method: 'triggers.delete',
    kept: async (page) => { await expect(page.locator('[data-test=triggers-table]')).toBeVisible(); await expect(page.locator('[data-test=trigger-nightly-check]')).toBeVisible(); },
  },
  {
    id: 'member-remove',
    setup: async (page) => { await openTeam(page); },
    opener: (page) => page.locator('[data-test=agent-TesterBen] [data-test=member-more]'),
    ask: async (page) => {
      const pane = page.locator('[data-test=agent-TesterBen]');
      await pane.locator('[data-test=member-more]').click();
      await expect(pane.locator('[data-test=member-more-menu]')).toBeVisible();
      await pane.locator('[data-test=remove-agent]').click();
    },
    // The member menu closes once Remove is picked: the focus goes back to its button, or the Remove item if it stays.
    focusBack: (page) => page.locator('[data-test=agent-TesterBen] [data-test=member-more], [data-test=agent-TesterBen] [data-test=remove-agent]'),
    message: 'Remove TesterBen from payments? Its queued instructions are dropped.', ok: 'Remove', method: 'members.remove',
    kept: async (page) => { await expect(page.locator('[data-test=agent-TesterBen]')).toBeVisible(); },
  },
  {
    id: 'skill-delete',
    setup: async (page) => { await openSettings(page, 'open-skills'); await page.locator('[data-test=skill-skill-10]').first().click(); },
    opener: (page) => page.locator('[data-test=skill-delete]'),
    message: /^Delete the skill skill-10 \(.+\)\?$/, ok: 'Delete', method: 'skills.delete',
    kept: async (page) => { await expect(page.locator('[data-test=skill-view]')).toBeVisible(); },
  },
  {
    id: 'task-delete',
    setup: async (page) => { await openSettings(page, 'open-integrations'); await expect(page.locator('[data-test=tasks]')).toContainText('npm test'); },
    opener: (page) => page.locator('[data-test=tasks] button.danger', { hasText: 'Delete' }).first(),
    message: 'Delete the task e2e? Its run records stay.', ok: 'Delete', method: 'tasks.delete',
    kept: async (page) => { await expect(page.locator('[data-test=tasks]')).toContainText('npm test'); },
  },
  {
    id: 'cli-remove',
    setup: tabs('tab-cli'),
    opener: (page) => page.locator('[data-test=integration-remove-gh]'),
    message: 'Remove GitHub? Agents can no longer use it.', ok: 'Remove', method: 'integrations.remove',
    kept: async (page) => { await expect(page.locator('[data-test=integration-gh]')).toBeVisible(); },
  },
  {
    id: 'api-remove', desktopOnly: true,
    setup: tabs('tab-api'),
    opener: (page) => page.locator('[data-test=api-remove-stripe]'),
    message: 'Remove stripe? Agents can no longer use it.', ok: 'Remove', method: 'integrations.apiRemove',
    kept: async (page) => { await expect(page.locator('[data-test=api-remove-stripe]')).toBeVisible(); },
    after: async (page, s) => { await followUp(page, s, 'api', 'integrations.apiRemove', "Also delete the connection 'Stripe test key'?"); },
  },
  {
    id: 'mcp-remove',
    setup: tabs('tab-mcp'),
    opener: (page) => page.locator('[data-test=mcp-remove-files]'),
    message: 'Remove files? Agents can no longer use it.', ok: 'Remove', method: 'integrations.mcpRemove',
    kept: async (page) => { await expect(page.locator('[data-test=mcp-remove-files]')).toBeVisible(); },
    after: async (page, s) => { await followUp(page, s, 'mcp', 'integrations.mcpRemove', "Also delete the connection 'Files token'?"); },
  },
  {
    id: 'connection-remove', desktopOnly: true,
    setup: tabs('tab-connections'),
    opener: (page) => page.locator('[data-test=connection-remove-5]'),
    message: 'Remove the connection Spare token?', ok: 'Remove', method: 'integrations.connectionRemove',
    kept: async (page) => { await expect(page.locator('[data-test=connection-remove-5]')).toBeVisible(); },
  },
  {
    id: 'discover-scope-switch',
    setup: editGh,
    opener: (page) => page.locator('[data-test=discover-scope]'),
    // A tap on the select focuses it; Playwright's selectOption alone does not.
    ask: async (page) => { const sel = page.locator('[data-test=discover-scope]'); await sel.focus(); await sel.selectOption({ index: 1 }); },
    message: 'You have unsaved changes. Switch the scope and lose them?', ok: 'Switch',
    kept: async (page) => {
      await expect(page.locator('[data-test=discover-scope]')).toHaveValue('');
      await expect(page.locator('[data-test=discover-save]')).toContainText('1 change');
      await expect(page.locator('[data-test=integration-dialog]')).toBeVisible();
      expect(checklistCalls().filter((c) => c.p?.teamId != null), 'no checklist read for a team after a cancelled switch').toHaveLength(0);
    },
    okDone: async (page) => {
      await expect.poll(() => checklistCalls().filter((c) => c.p?.teamId != null).length, { message: 'the team scope is read after OK' }).toBe(1);
      await expect(page.locator('[data-test=discover-scope]')).not.toHaveValue('');
    },
  },
  {
    id: 'integration-dialog-close',
    setup: editGh,
    opener: (page) => page.locator('[data-test=dialog-close]'),
    message: 'You have unsaved changes. Close and lose them?', ok: 'Close',
    kept: async (page) => {
      await expect(page.locator('[data-test=integration-dialog]')).toBeVisible();
      await expect(page.locator('[data-test=discover-save]')).toContainText('1 change');
    },
    okDone: async (page) => { await expect(page.locator('[data-test=integration-dialog]')).toHaveCount(0); },
  },
];

/** After OK on an API / MCP removal whose connection nothing else uses (desktop only): the follow-up asks; Cancel removes the
 *  integration only (removeConnection false), and on a second run (re-listed) OK removes both. In the remote view no follow-up. */
async function followUp(page: Page, s: Size, kind: 'api' | 'mcp', method: string, question: string) {
  const call = () => made.filter((m) => m.method === method).at(-1);
  if (!s.desktop) {
    await expect.poll(() => made.filter((m) => m.method === method).length).toBe(1);
    await expect(box(page), 'remote: no follow-up question').toHaveCount(0);
    expect(!!call()?.p?.removeConnection, 'remote: the connection is kept').toBe(false);
    return;
  }
  await expectConfirm(page, s, `${kind}-also-delete-connection`, question, 'Delete', true);
  expect(made.filter((m) => m.method === method), 'nothing removed while the follow-up is open').toHaveLength(0);
  await page.keyboard.press('Escape');
  await expect(box(page)).toHaveCount(0);
  await expect.poll(() => made.filter((m) => m.method === method).length).toBe(1);
  expect(!!call()?.p?.removeConnection, 'Escape on the follow-up: the integration only').toBe(false);
}

/** The second half of the follow-up: OK on both questions removes the connection too (a fresh page, as nothing was listed). */
async function followUpOk(browser: Browser, s: Size, kind: 'api' | 'mcp') {
  made = [];
  const { page, ctx, errors, dialogs } = await open(browser, s);
  const method = kind === 'api' ? 'integrations.apiRemove' : 'integrations.mcpRemove';
  await tabs(kind === 'api' ? 'tab-api' : 'tab-mcp')(page);
  await page.locator(kind === 'api' ? '[data-test=api-remove-stripe]' : '[data-test=mcp-remove-files]').click();
  await expect(box(page)).toBeVisible();
  await page.locator('[data-test=confirm-ok]').click();
  await expect(page.locator('[data-test=confirm-message]')).toHaveText(/^Also delete the connection/);
  await page.locator('[data-test=confirm-ok]').click();
  await expect(box(page)).toHaveCount(0);
  await expect.poll(() => made.filter((m) => m.method === method).length).toBe(1);
  expect(made.find((m) => m.method === method)!.p?.removeConnection, 'OK on the follow-up removes the connection too').toBe(true);
  expect(dialogs, 'no browser dialog').toEqual([]);
  expect(errors).toEqual([]);
  await ctx.close();
}

const count = (method?: string) => (method ? made.filter((m) => m.method === method).length : 0);

for (const s of SIZES) {
  test.describe(s.label, () => {
    for (const site of SITES) {
      test(`${site.id}: Escape, Cancel and the backdrop keep it (no call), OK does it; focus, Tab, fit; no browser dialog`, async ({ browser }) => {
        test.skip(!!site.desktopOnly && !s.desktop, 'not in the remote view: ConnectionsPanel load() returns when remote, ApisPanel shows Remove only v-if="!remote"');
        const { page, ctx, errors, dialogs } = await open(browser, s, site.id.startsWith('trigger') || site.id.startsWith('member') ? '#team=1' : '');
        await site.setup(page);
        const opener = site.opener(page);
        await expect(opener).toBeVisible();
        const ask = async () => { if (site.ask) await site.ask(page); else await opener.click(); };
        const focusBack = site.focusBack?.(page) ?? opener;
        const before = count(site.method);

        // Escape
        await ask();
        await expectConfirm(page, s, site.id, site.message, site.ok, true);
        await page.keyboard.press('Escape');
        await expect(box(page)).toHaveCount(0);
        await page.waitForTimeout(300);
        expect(count(site.method), 'Escape: no call').toBe(before);
        await site.kept(page);
        const back = await page.evaluate(() => document.activeElement?.getAttribute('data-test') ?? document.activeElement?.tagName ?? null);
        expect.soft(await focusBack.evaluateAll((els) => els.includes(document.activeElement as Element)), `Escape: the focus is back on the opener (it is on ${back})`).toBe(true);

        // Cancel
        await ask();
        await expectConfirm(page, s, site.id, site.message, site.ok, false);
        await page.locator('[data-test=confirm-cancel]').click();
        await expect(box(page)).toHaveCount(0);
        await page.waitForTimeout(300);
        expect(count(site.method), 'Cancel: no call').toBe(before);
        await site.kept(page);
        expect.soft(await focusBack.evaluateAll((els) => els.includes(document.activeElement as Element)), 'Cancel: the focus is back on the opener').toBe(true);

        // The backdrop
        await ask();
        await expect(box(page)).toBeVisible();
        await page.mouse.click(3, 3);
        await expect(box(page)).toHaveCount(0);
        await page.waitForTimeout(300);
        expect(count(site.method), 'backdrop: no call').toBe(before);
        await site.kept(page);

        // OK
        await ask();
        await expectConfirm(page, s, site.id, site.message, site.ok, false);
        await page.locator('[data-test=confirm-ok]').click();
        if (site.after) await site.after(page, s);
        else {
          await expect(box(page)).toHaveCount(0);
          if (site.method) await expect.poll(() => count(site.method), { message: 'OK: the call is made once' }).toBe(before + 1);
          if (site.okDone) await site.okDone(page);
        }

        expect(dialogs, 'no browser dialog').toEqual([]);
        expect(errors).toEqual([]);
        await ctx.close();
      });
    }
    if (s.desktop) {
      for (const kind of ['api', 'mcp'] as const) {
        test(`${kind} remove: OK on "Also delete the connection" removes the connection too`, async ({ browser }) => { await followUpOk(browser, s, kind); });
      }
    }
  });
}
