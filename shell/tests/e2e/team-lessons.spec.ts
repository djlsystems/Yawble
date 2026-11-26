// A team's lessons in headless Chrome against the phone-remote fake core (remote.html,
// tests/e2e/phone) with team_retros fixtures. Team settings > General reads "Lessons: 2, last updated <date> by the retro
// of deliverable 12" with a link; the link opens Settings > Skills at payments' team_retros, which says who last wrote it
// and lists its versions newest first; Restore on version 1 (after the confirm) sends skills.restore { id: 1 }.
//
//   npx playwright test team-lessons      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/team-lessons (else test-results/team-lessons).
import { expect, test, type Browser } from './phone/fixtures';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { answers, createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';
import { settled } from './settle';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'team-lessons');
const BODY = '## Building and testing\n- Run `npm run build` in shell before any e2e spec: specs load shell/out (2026-11-18, deliverable 12)\n' +
  '## Git\n- Fetch the team branch before merging a member\'s branch (2026-11-17, deliverable 9)\n\n```sh\nnpm run build\n```\nSee [the docs](https://example.com/docs).';
const retros = { name: 'team_retros', description: 'Lessons from this team\'s earlier work, kept by the Manager\'s retrospectives.', roles: ['any'], body: BODY,
  kind: 'custom', teamId: 1, team: 'payments', id: 31, updatedAt: '2026-11-18T09:00:00Z' };
const version = (n: number, body: string, deliverable: number, at: string) =>
  ({ id: n, teamId: 1, name: 'team_retros', version: n, body, writer: 'Manager', deliverable, restoredFrom: null, writtenAt: at });
const VERSIONS = [version(2, BODY, 12, '2026-11-18T09:00:00Z'), version(1, '- Fetch the team branch before merging a member\'s branch (2026-11-17, deliverable 9)', 9, '2026-11-17T09:00:00Z')];

const OVERRIDES: Record<string, (p?: any) => unknown> = {
  'teams.lessons': (p) => p?.teamId === 1
    ? { count: 2, version: 2, updatedAt: '2026-11-18T09:00:00Z', writer: 'Manager', deliverable: 12, skillId: 31 }
    : { count: 0, version: 0, updatedAt: null, writer: null, deliverable: null, skillId: null },
  'skills.versions': (p) => (p?.teamId === 1 && p?.name === 'team_retros' ? VERSIONS : []),
  'skills.restore': () => ({ ...retros, body: VERSIONS[1]!.body }),
};
const saved: Record<string, unknown> = {};

test.beforeAll(async () => {
  test.setTimeout(600_000);
  mkdirSync(SHOTS, { recursive: true });
  const list = answers['skills.list']!;
  OVERRIDES['skills.list'] = (p) => [...(list(p, 'full') as unknown[]), retros];
  for (const k of Object.keys(OVERRIDES)) { saved[k] = (answers as any)[k]; (answers as any)[k] = OVERRIDES[k]; }
  await buildRenderer();
});
test.afterAll(() => { for (const k of Object.keys(saved)) { if (saved[k] === undefined) delete (answers as any)[k]; else (answers as any)[k] = saved[k]; } });

async function open(browser: Browser) {
  const core = createFakeCore();
  core.setAsks(false);
  const sent: { method: string; params: any }[] = [];
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, colorScheme: 'light' });
  await ctx.addInitScript(() => { try { localStorage.setItem('yawble.remoteTheme', 'light'); } catch { /* none */ } });
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
  await page.goto(`${ORIGIN}/remote.html`);
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await settled(page);
  return { page, ctx, errors, sent };
}

test('Team settings reads the Lessons line; its link opens Settings > Skills at team_retros with its versions and last writer; Restore', async ({ browser }) => {
  const { page, ctx, errors, sent } = await open(browser);
  await page.locator('[data-test=sidebar] [data-test=nav-teams]').first().click();
  await page.locator('[data-test=team-open-payments]').click();
  await page.locator('[data-test=open-manage]').first().click();

  const line = page.locator('[data-test=team-lessons-line]');
  await expect(line).toHaveText(/^Lessons: 2, last updated .+ by the retro of deliverable 12$/);
  expect(sent.find((s) => s.method === 'teams.lessons')!.params).toEqual({ teamId: 1 });
  await page.locator('[data-test=team-lessons]').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'team-settings-lessons.png') });

  await page.locator('[data-test=team-lessons-link]').click();
  await expect(page.locator('[data-test=team-general]')).toHaveCount(0); // the team's Settings closed
  // The text renders as markdown (two headings, a list item each, inline code), not the raw source.
  const view = page.locator('[data-test=skill-view]');
  await expect(view.locator('h2')).toHaveText(['Building and testing', 'Git']);
  await expect(view.locator('li')).toHaveCount(2);
  await expect(view.locator('li code')).toHaveText('npm run build');
  await expect(view).not.toContainText('## ');
  await expect(view.locator('pre code')).toHaveText(/npm run build/);
  await expect(view.locator('a')).toHaveAttribute('target', '_blank');
  // Both themes (styles.css keys its variables on <html data-theme>): the person can compare the rendered text.
  const theme = (t: string) => page.evaluate((v) => document.documentElement.setAttribute('data-theme', v), t);
  await theme('light');
  await page.screenshot({ path: path.join(SHOTS, 'skill-markdown-light.png') });
  await theme('dark');
  await page.screenshot({ path: path.join(SHOTS, 'skill-markdown-dark.png') });
  await theme('light');
  await expect(page.locator('[data-test=skills-pane] h3')).toHaveText('team_retros');
  await expect(page.locator('[data-test=skill-last-writer]')).toContainText('Last written by the retro of deliverable 12');
  await expect(page.locator('[data-test=skill-last-writer]')).toContainText('(version 2)');
  await expect(page.locator('[data-test=skill-lessons-note]')).toContainText('every run of team payments gets this text in its prompt');
  await page.locator('[data-test=skill-versions] summary').click();
  await expect(page.locator('[data-test=skill-versions] li')).toHaveCount(2);
  await expect(page.locator('[data-test=skill-version-1]')).toContainText('the retro of deliverable 9');
  await page.locator('[data-test=skill-version-show-1]').click();
  await expect(page.locator('[data-test=skill-version-body-1]')).toContainText('Fetch the team branch');
  await page.screenshot({ path: path.join(SHOTS, 'skills-versions.png') });

  await page.locator('[data-test=skill-version-restore-1]').click();
  await page.locator('[data-test=confirm-ok]').click();
  await expect.poll(() => sent.filter((s) => s.method === 'skills.restore').map((s) => s.params)).toEqual([{ id: 1 }]);
  await expect(page.locator('[data-test=skill-saved]')).toContainText('Restored version 1 of team_retros');
  expect(errors).toEqual([]);
  await ctx.close();
});
