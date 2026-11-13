import { _electron as electron, expect, test } from '@playwright/test';
import { companionHome, removeCompanionHomes } from './companion-home';
import path from 'node:path';

// Opt-in (YAWBLE_REAL_DEMO=1): the Git dialog on a real team repository with an origin. On an existing team
// (YAWBLE_GIT_ROOT: the app root with data/, YAWBLE_GIT_TEAM) whose team branch is ahead of the base branch, it opens the
// dialog, merges the team branch to the base after the confirmation, and checks the dialog says it landed.
// Starts no agent.
test('real git dialog: merge the team branch to main', async () => {
  test.skip(process.env.YAWBLE_REAL_DEMO !== '1', 'changes a real repository on the person\'s machine: set YAWBLE_REAL_DEMO=1');
  test.setTimeout(5 * 60_000);
  const root = process.env.YAWBLE_GIT_ROOT!;
  const team = process.env.YAWBLE_GIT_TEAM!;
  const shellDir = path.resolve(__dirname, '..', '..');
  const app = await electron.launch({ args: [shellDir], env: { ...process.env, ...companionHome(), YAWBLE_DATA_DIR: path.join(root, 'data') } as Record<string, string> });
  try {
    const main = await app.firstWindow();
    await main.setViewportSize({ width: 1300, height: 860 });
    await main.locator('[data-test=team-item]', { hasText: team }).first().click();
    await expect(main.locator('[data-test=team-title]')).toContainText(team, { timeout: 30_000 });
    await main.locator('[data-test=open-git]').click();
    await expect(main.locator('[data-test=git-merge-state]')).toContainText('fast-forward', { timeout: 30_000 });
    await main.screenshot({ path: path.join(root, 'git-1-status.png') });
    await main.locator('[data-test=git-merge]').click();
    await main.locator('[data-test=git-merge-yes]').click();
    await expect(main.locator('[data-test=git-result]')).toContainText('fast-forwarded', { timeout: 60_000 });
    await expect(main.locator('[data-test=git-merge-state]')).toContainText('already has everything');
    await main.screenshot({ path: path.join(root, 'git-2-merged.png') });
  } finally {
    await app.close();
    removeCompanionHomes();
  }
});
