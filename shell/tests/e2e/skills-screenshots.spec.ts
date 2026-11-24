// Card 530 (verify of card 529): Settings > Skills in light and dark with a skill that has every markdown element
// (h1-h3, bullet and numbered lists, inline code, a code block, a link, a blockquote, raw HTML that must stay text) and an
// all-teams skill so the filter pill shows its four choices. Headless Chrome against the phone-remote fake core.
//
//   npx playwright test card530-skill-shots      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/B100/card530-*.png (else test-results/B100).
import { expect, test } from './phone/fixtures';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { answers, createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'B100');
const BODY = '# Release notes\n\nWrite the notes for **each** release; keep them short.\n\n## Steps\n- Read `git log` since the last tag\n- Group by area\n  - nested item\n\n'
  + '1. Draft\n2. Review\n\n### Commands\n\n```powershell\ngit log --oneline v1.2..HEAD\nnpm run build\n```\n\n> Never paste a secret.\n\n'
  + 'See [the guide](https://example.com/guide). <b>raw</b> <img src=x onerror="window.__x=1">';
const mine = { name: 'release-notes', description: 'How to write release notes.', roles: ['member'], body: BODY,
  kind: 'custom', teamId: null, team: null, id: 77, updatedAt: '2026-11-20T09:00:00Z' };
const saved: Record<string, unknown> = {};

test.beforeAll(async () => {
  test.setTimeout(600_000);
  mkdirSync(SHOTS, { recursive: true });
  const list = answers['skills.list']!;
  saved['skills.list'] = list;
  (answers as any)['skills.list'] = (p: unknown) => [...(list(p, 'full') as unknown[]), mine];
  await buildRenderer();
});
test.afterAll(() => { for (const k of Object.keys(saved)) (answers as any)[k] = saved[k]; });

test('Settings > Skills: rendered markdown and the filter pill, light and dark', async ({ browser }) => {
  const core = createFakeCore();
  core.setAsks(false);
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, colorScheme: 'light' });
  await ctx.addInitScript(() => { try { localStorage.setItem('yawble.remoteTheme', 'light'); } catch { /* none */ } });
  await ctx.route(`${ORIGIN}/**`, async (route) => {
    const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
    const file = path.resolve(builtDir, rel || 'remote.html');
    if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
    return route.fulfill({ path: file });
  });
  await ctx.routeWebSocket(/\/rpc$/, (ws) => { ws.onMessage((m) => { const out = core.answer(String(m)); if (out) ws.send(out); }); });
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${ORIGIN}/remote.html`);
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });

  await page.locator('[data-test=sidebar] [data-test=open-app-settings], [data-test=settings-gear]').first().click();
  await page.locator('[data-test=settings] [data-test=open-skills]').click();
  await expect(page.locator('[data-test=skills-filter] button')).toHaveText(['All', 'Built-in', 'Team skills', 'All teams']);
  await page.locator('[data-test=skill-release-notes]').click();
  const view = page.locator('[data-test=skill-view]');
  await expect(view.locator('h1')).toHaveText('Release notes');
  await expect(view.locator('ol li')).toHaveCount(2);
  await expect(view.locator('pre code')).toContainText('git log --oneline');
  await expect(view.locator('blockquote')).toHaveCount(1);
  await expect(view.locator('a')).toHaveAttribute('target', '_blank');
  await expect(view.locator('img, b')).toHaveCount(0);
  await expect(view).toContainText('<b>raw</b>');

  const theme = (t: string) => page.evaluate((v) => document.documentElement.setAttribute('data-theme', v), t);
  for (const t of ['light', 'dark']) {
    await theme(t);
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(SHOTS, `card530-skill-markdown-${t}.png`) });
  }
  await theme('dark');
  await page.locator('[data-test=skills-filter-builtin]').click();
  await page.waitForTimeout(150);
  await page.screenshot({ path: path.join(SHOTS, 'card530-filter-builtin-dark.png') });
  await theme('light');
  await page.locator('[data-test=skills-filter-allTeams]').click();
  await page.waitForTimeout(150);
  await page.screenshot({ path: path.join(SHOTS, 'card530-filter-allteams-light.png') });
  expect(errors).toEqual([]);
  expect(await page.evaluate(() => (window as any).__x)).toBeUndefined();
  await ctx.close();
});
