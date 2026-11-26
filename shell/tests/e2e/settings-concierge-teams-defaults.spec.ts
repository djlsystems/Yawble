// Settings > Concierge and Settings > Teams in headless Chrome against the
// phone-remote fake core (remote.html, tests/e2e/phone), with the defaults RPCs faked here (concierge.defaults/setDefaults,
// teams.defaults/setDefaults/generateNames: the core's shapes). Generate is answered by this fake: no agent runs.
//  - 1280x800 and 375x812: both tabs.
//  - Concierge: "First available on this computer" when nothing is saved; picking an agent sends concierge.setDefaults.
//  - Teams: saved Manager / rows / names / count / country; Generate is busy and disabled while it runs, REPLACES the box
//    and saves nothing; Save sends {manager, members, memberNamesText, country} and lists the rejected lines; a failed
//    Generate shows its error and leaves the box.
//  - Visitor (read-only): both tabs shown, nothing can be changed, Generate and Save are disabled.
//  - New Team dialog: starts from teams.defaults (ticked rows in the list, others as "+ <name>").
//
//   npx playwright test settings-concierge-teams-defaults --workers=1      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { answers, createFakeCore, type Access } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';
import { settled } from './settle';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(120_000);


const COUNTRIES = [
  { code: 'en-US', label: 'United States (US English)' }, { code: 'de-DE', label: 'Germany (German)' }, { code: 'ja-JP', label: 'Japan (Japanese)' },
];
const GENERATED = ['Emma', 'Liam', 'Olivia', 'Noah', 'Ava', 'Elijah', 'Sophia', 'James'];

/** The fake's defaults state: what is saved, and how Generate answers. */
function defaultsState(opts: { generate?: 'ok' | 'fail' } = {}) {
  const state = {
    defaultAgent: null as string | null,
    manager: { name: 'Manager', cli: 'claude' },
    members: [{ name: 'Developer', cli: 'claude', onNewTeams: true }, { name: 'Tester', cli: 'claude', onNewTeams: true },
      { name: 'Researcher Amara', cli: 'claude', onNewTeams: false }],
    memberNames: ['Priya', 'Tomas', 'Wren', 'Ada', 'Hana'],
    country: 'en-US',
    release: null as null | (() => void),
  };
  const read = () => ({ manager: state.manager, members: state.members, memberNames: state.memberNames, country: state.country, countries: COUNTRIES,
    usedCount: state.memberNames.filter((n) => ['Ada', 'Wren'].includes(n)).length });
  answers['concierge.defaults'] = () => ({ defaultAgent: state.defaultAgent });
  answers['concierge.setDefaults'] = (p: any) => { state.defaultAgent = p?.defaultAgent ?? null; return { defaultAgent: state.defaultAgent }; };
  answers['teams.defaults'] = () => read();
  answers['teams.setDefaults'] = (p: any) => {
    if (p?.manager) state.manager = p.manager;
    if (p?.members) state.members = p.members;
    const rejected: { line: string; reason: string }[] = [];
    if (typeof p?.memberNamesText === 'string') {
      const seen = new Set<string>(); const names: string[] = [];
      for (const raw of p.memberNamesText.split(/\r?\n/)) {
        const line = raw.trim();
        if (!line) continue;
        if (!/^[A-Za-z]+(?:-[A-Za-z]+)*$/.test(line) || line.length < 2 || line.length > 20) { rejected.push({ line, reason: 'only the letters A-Z (a hyphen inside a name is allowed)' }); continue; }
        if (!seen.has(line.toLowerCase())) { seen.add(line.toLowerCase()); names.push(line); }
      }
      state.memberNames = names;
    }
    if (p?.country) state.country = p.country;
    return { saved: read(), rejected };
  };
  answers['teams.generateNames'] = (p: any) => {
    if (opts.generate === 'fail') { const e = new Error('the claude run failed: fake failure'); (e as any).code = 'generate_failed'; throw e; }
    return { names: p?.country === 'de-DE' ? ['Lukas', 'Mia', 'Leon', 'Emilia'] : GENERATED };
  };
  return state;
}

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

async function open(browser: Browser, size: { width: number; height: number }, access: Access = 'full', opts: { generate?: 'ok' | 'fail'; slowGenerate?: boolean } = {}) {
  const state = defaultsState(opts);
  const core = createFakeCore();
  core.setAsks(false);
  core.setAccess(access);
  const sent: { method: string; params: any }[] = [];
  const answered: string[] = []; // the methods the fake has answered, in order
  let hold: Promise<void> | null = null;
  let release = () => {};
  if (opts.slowGenerate) hold = new Promise((r) => { release = r; });
  const ctx = await browser.newContext({ viewport: size, colorScheme: 'light', isMobile: size.width < 500, hasTouch: size.width < 500 });
  await ctx.addInitScript(() => { try { localStorage.setItem('yawble.remoteTheme', 'light'); } catch { /* none */ } });
  await ctx.route(`${ORIGIN}/**`, async (route) => {
    const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
    const file = path.resolve(builtDir, rel || 'remote.html');
    if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
    return route.fulfill({ path: file });
  });
  await ctx.routeWebSocket(/\/rpc$/, (ws) => {
    ws.onMessage(async (m) => {
      const msg = JSON.parse(String(m));
      sent.push({ method: msg.method, params: msg.params });
      if (msg.method === 'teams.generateNames' && hold) await hold;
      const out = core.answer(String(m));
      if (out) ws.send(out);
      answered.push(msg.method);
    });
  });
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${ORIGIN}/remote.html`);
  await startedUp(page, answered);
  return { page, ctx, errors, sent, answered, state, release: () => release(), core };
}

/** The window's start-up reads are answered (the agents, the custom agents, the saved Concierge default) and the page is at rest. */
async function startedUp(page: Page, answered: string[]) {
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await expect.poll(() => ['agents.detect', 'agentProfiles.list', 'concierge.defaults'].filter((m) => !answered.includes(m)), { message: 'the start-up reads answered' }).toEqual([]);
  await settled(page);
}

/** A reload, then the start-up reads answered again. */
async function reload(page: Page, answered: string[]) {
  answered.length = 0;
  await page.reload();
  await startedUp(page, answered);
}

/** What a tab shows once it is open. */
const TAB_SHOWS = { 'open-concierge': '[data-test=concierge-default-agent]', 'open-teams': '[data-test=default-manager-name]' } as const;

async function openTab(page: Page, tab: 'open-concierge' | 'open-teams') {
  const back = page.locator('[data-test=settings] [data-test=settings-back]');
  if (await back.isVisible().catch(() => false)) await back.click(); // a phone shows one section at a time
  if (!(await page.locator('[data-test=settings]').isVisible().catch(() => false))) {
    // Settings is the gear at the top right (the desktop window, the remote view and the phone top bar).
    await page.getByTestId('settings-gear').click();
    await expect(page.locator('[data-test=settings]')).toBeVisible();
  }
  await page.locator(`[data-test=settings] [data-test=${tab}]`).first().click();
  await expect(page.locator(TAB_SHOWS[tab])).toBeVisible();
  await settled(page);
}

const DESKTOP = { width: 1280, height: 800 };
const PHONE = { width: 375, height: 812 };

for (const [label, size] of [['desktop-1280', DESKTOP], ['phone-375', PHONE]] as const) {
  test(`${label}: Settings > Concierge shows "First available", picking Codex saves it`, async ({ browser }) => {
    const { page, ctx, errors, sent, state } = await open(browser, size);
    await openTab(page, 'open-concierge');
    const select = page.locator('[data-test=concierge-default-agent]');
    await expect(select).toBeVisible();
    await expect(select).toHaveValue('');
    await expect(select.locator('option').first()).toHaveText('First available on this computer');
    await select.selectOption('codex');
    await expect(page.locator('[data-test=concierge-defaults-saved]')).toBeVisible();
    expect(sent.filter((s) => s.method === 'concierge.setDefaults').at(-1)!.params).toEqual({ defaultAgent: 'codex' });
    expect(state.defaultAgent).toBe('codex');
    // Fits a phone: nothing wider than the screen.
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow).toBeLessThanOrEqual(1);
    expect(errors).toEqual([]);
    await ctx.close();
  });

  test(`${label}: Settings > Teams shows the saved defaults, Generate replaces the box unsaved, Save sends and lists rejected lines`, async ({ browser }) => {
    const { page, ctx, errors, sent, state, release } = await open(browser, size, 'full', { slowGenerate: true });
    await openTab(page, 'open-teams');
    await expect(page.locator('[data-test=default-manager-name]')).toHaveValue('Manager');
    await expect(page.locator('[data-test=default-member-name-2]')).toHaveValue('Researcher Amara');
    await expect(page.locator('[data-test=default-member-on-2]')).not.toBeChecked();
    await expect(page.locator('[data-test=member-names]')).toHaveValue('Priya\nTomas\nWren\nAda\nHana');
    await expect(page.locator('[data-test=member-names-count]')).toHaveText('5 names, 2 used by a member');
    await expect(page.locator('[data-test=names-country]')).toHaveValue('en-US');

    // Generate: busy and disabled while it runs; then it replaces the box and nothing is saved.
    await page.locator('[data-test=names-country]').selectOption('de-DE');
    await page.locator('[data-test=names-generate]').click();
    await expect(page.locator('[data-test=names-generate]')).toBeDisabled();
    await expect(page.locator('[data-test=names-generate]')).toHaveAttribute('aria-busy', 'true');
    release();
    await expect(page.locator('[data-test=member-names]')).toHaveValue('Lukas\nMia\nLeon\nEmilia');
    await expect(page.locator('[data-test=names-generate]')).toBeEnabled();
    expect(sent.filter((s) => s.method === 'teams.generateNames').map((s) => s.params)).toEqual([{ country: 'de-DE' }]);
    expect(sent.some((s) => s.method === 'teams.setDefaults')).toBe(false);
    expect(state.memberNames).toEqual(['Priya', 'Tomas', 'Wren', 'Ada', 'Hana']);

    // Save: one bad line is reported, the rest saved.
    await page.locator('[data-test=member-names]').fill('Lukas\nMia\nMary Ann\n\n mia \nJosé');
    await page.locator('[data-test=team-defaults-save]').click();
    await expect(page.locator('[data-test=team-defaults-saved]')).toBeVisible();
    const params = sent.filter((s) => s.method === 'teams.setDefaults').at(-1)!.params;
    expect(Object.keys(params).sort()).toEqual(['country', 'manager', 'memberNamesText', 'members']);
    expect(params.country).toBe('de-DE');
    expect(params.members).toEqual(state.members);
    await expect(page.locator('[data-test=member-names-rejected-line]')).toHaveCount(2);
    await expect(page.locator('[data-test=member-names]')).toHaveValue('Lukas\nMia');
    await page.locator('[data-test=member-names-rejected]').scrollIntoViewIfNeeded();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow).toBeLessThanOrEqual(1);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('a failed Generate shows its error line and leaves the box alone', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, DESKTOP, 'full', { generate: 'fail' });
  await openTab(page, 'open-teams');
  await page.locator('[data-test=names-generate]').click();
  await expect(page.locator('[data-test=names-generate-error]')).toContainText('fake failure');
  await expect(page.locator('[data-test=member-names]')).toHaveValue('Priya\nTomas\nWren\nAda\nHana');
  expect(errors).toEqual([]);
  await ctx.close();
});

for (const [label, size] of [['desktop-1280', DESKTOP], ['phone-375', PHONE]] as const) {
  test(`${label}: a Visitor sees both tabs but cannot change or Generate`, async ({ browser }) => {
    const { page, ctx, errors, sent } = await open(browser, size, 'read');
    await openTab(page, 'open-concierge');
    await expect(page.locator('[data-test=concierge-default-agent]')).toBeDisabled();
    await openTab(page, 'open-teams');
    await expect(page.locator('[data-test=member-names]')).toHaveValue('Priya\nTomas\nWren\nAda\nHana');
    await expect(page.locator('[data-test=default-manager-name]')).toBeDisabled();
    await expect(page.locator('[data-test=default-member-cli-0]')).toBeDisabled();
    await expect(page.locator('[data-test=default-member-on-0]')).toBeDisabled();
    await expect(page.locator('[data-test=names-country]')).toBeDisabled();
    await expect(page.locator('[data-test=member-names]')).toHaveAttribute('readonly', '');
    for (const t of ['names-generate', 'team-defaults-save', 'default-member-add', 'default-member-remove-0']) {
      await expect(page.locator(`[data-test=${t}]`)).toBeDisabled();
    }
    await page.locator('[data-test=names-generate]').click({ force: true }).catch(() => {});
    await page.locator('[data-test=team-defaults-save]').click({ force: true }).catch(() => {});
    // Nothing is sent: a read the page makes after the clicks goes over the same socket behind anything they would have
    // sent, so once it is answered, such a send would be in `sent` too.
    await page.evaluate(() => (window as any).yawble.call('teams.defaults').catch(() => null));
    expect(sent.filter((s) => ['teams.generateNames', 'teams.setDefaults', 'concierge.setDefaults'].includes(s.method))).toEqual([]);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

// The window's New chat starts on the saved Concierge default when it is ready.
// Agents here: claude and grok signed in, codex installed but signed out, gemini not installed.
test.describe('F1: a new chat starts on the saved Concierge default', () => {
  const detect = answers['agents.detect'];
  test.afterEach(() => { answers['agents.detect'] = detect; });
  const agents = () => [
    { cliId: 'claude', found: true, version: '2.0.0', path: 'C:/fake/claude.exe', signIn: { status: 'signed-in', detail: null } },
    { cliId: 'grok', found: true, version: '1.0.0', path: 'C:/fake/grok.exe', signIn: { status: 'signed-in', detail: null } },
    { cliId: 'codex', found: true, version: '1.0.0', path: 'C:/fake/codex.exe', signIn: { status: 'signed-out', detail: null } },
    { cliId: 'gemini', found: false, version: null, path: null, signIn: { status: 'unknown', detail: null } },
  ];

  /** New chat, then the saved default read again (App.vue newChat) and the composer shown. */
  async function newChat(page: Page, answered: string[]) {
    const reads = answered.filter((m) => m === 'concierge.defaults').length;
    await page.locator('[data-test=sidebar] [data-test=new-chat]').first().click();
    await expect.poll(() => answered.filter((m) => m === 'concierge.defaults').length, { message: 'the default read again' }).toBeGreaterThan(reads);
    await expect(composerAgent(page)).toBeVisible();
  }
  const composerAgent = (page: Page) => page.locator('[data-test=composer-agent]').first();
  async function sendHello(page: Page, sent: { method: string; params: any }[]) {
    const before = sent.filter((s) => s.method === 'concierge.send').length;
    await page.locator('[data-test=composer-text]').first().fill('hello');
    await page.locator('[data-test=composer-text]').first().press('Enter');
    await expect.poll(() => sent.filter((s) => s.method === 'concierge.send').length).toBe(before + 1);
    return sent.filter((s) => s.method === 'concierge.send').at(-1)!.params;
  }

  test('grok saved: New chat is on grok; changing the default applies to the next New chat; an existing chat keeps its agent', async ({ browser }) => {
    answers['agents.detect'] = agents;
    const { page, ctx, errors, sent, answered, state } = await open(browser, DESKTOP);
    state.defaultAgent = 'grok';
    await reload(page, answered);
    await newChat(page, answered);
    await expect(composerAgent(page)).toHaveValue('grok');
    expect((await sendHello(page, sent)).cli).toBe('grok');

    // Change the default in Settings > Concierge, then New chat: no reload.
    await openTab(page, 'open-concierge');
    await page.locator('[data-test=concierge-default-agent]').selectOption('claude');
    await expect(page.locator('[data-test=concierge-defaults-saved]')).toBeVisible();
    await newChat(page, answered);
    await expect(composerAgent(page)).toHaveValue('claude');
    await openTab(page, 'open-concierge');
    await page.locator('[data-test=concierge-default-agent]').selectOption('grok');
    await expect(page.locator('[data-test=concierge-defaults-saved]')).toBeVisible();
    await newChat(page, answered);
    await expect(composerAgent(page)).toHaveValue('grok');

    // An existing chat (c2, on claude) keeps its agent.
    await page.locator('[data-test=sidebar] [data-test=chat-item]').filter({ hasText: 'A long chat title' }).first().click();
    // The chat opens on its own agent (the composer was on grok before the click).
    await expect(composerAgent(page)).toHaveValue('claude');
    expect(errors).toEqual([]);
    await ctx.close();
  });

  for (const [why, saved] of [['signed out', 'codex'], ['not installed', 'gemini']] as const) {
    test(`a saved default that is ${why} (${saved}) falls back to the first ready agent`, async ({ browser }) => {
      answers['agents.detect'] = agents;
      const { page, ctx, errors, sent, answered, state } = await open(browser, DESKTOP);
      state.defaultAgent = saved;
      await reload(page, answered);
      await newChat(page, answered);
      await expect(composerAgent(page)).toHaveValue('claude');
      expect((await sendHello(page, sent)).cli).toBe('claude');
      expect(errors).toEqual([]);
      await ctx.close();
    });
  }
});

test('the New Team dialog starts from teams.defaults: ticked rows in the list, the other as "+ Researcher Amara"', async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser, DESKTOP);
  await page.locator('[data-test=sidebar] [data-test=new-team]').first().click();
  await expect(page.locator('[data-test=manager-name]')).toHaveValue('Manager');
  await expect(page.locator('[data-test=member-name-0]')).toHaveValue('Developer');
  await expect(page.locator('[data-test=member-name-1]')).toHaveValue('Tester');
  await expect(page.locator('[data-test=member-offers] button')).toHaveText(['+ Researcher Amara']);
  await page.locator('[data-test=member-offers] button').click();
  await expect(page.locator('[data-test=member-name-2]')).toHaveValue('Researcher Amara');
  await expect(page.locator('[data-test=member-offers]')).toHaveCount(0);
  expect(sent.some((s) => s.method === 'teams.defaults')).toBe(true);
  expect(errors).toEqual([]);
  await ctx.close();
});
