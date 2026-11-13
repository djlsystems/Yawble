import { _electron as electron, expect, test } from '@playwright/test';
import { companionHome, removeCompanionHomes } from './companion-home';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';

// Opt-in (YAWBLE_REAL_DEMO=1): the person's flow with real agents on this machine. A new team with only a Manager is
// created in the UI, and the Manager is told to hire a team that builds and tests a console app. Real model calls on
// the person's accounts. The run is left going: the caller watches the database for the workflow to complete.
//   YAWBLE_HELLO_ROOT  the app root to use (its data/ is created); YAWBLE_INSTALL_DIR as for the demo.
//   YAWBLE_HELLO_TEAM  the team name (default "hello").
//   YAWBLE_HELLO_MANAGER_CLI  the Manager's CLI (default claude).
//   YAWBLE_HELLO_REPO  a git repository folder for the team (default: the app makes one); with an origin, the host
//                    pushes the team's branches there after each run.
//   YAWBLE_HELLO_INSTRUCTION  the instruction to give the Manager instead of the default one.
const instruction = process.env.YAWBLE_HELLO_INSTRUCTION ?? [
  'Hire a small team (a developer and a tester) to build and test a C# console app.',
  'The app prints "Hello World", then prints "hit a key to continue", and any key ends it.',
  'The developer builds it with the .NET SDK; the tester fetches the developer\'s branch, builds it, runs it and checks the output and that a key ends it.',
  'When you are done, tell me the FULL PATH of the built .exe so I can run it myself in a terminal window.',
].join(' ');

test('real hello team: a new team, its Manager hires and runs a build-and-test team', async () => {
  test.skip(process.env.YAWBLE_REAL_DEMO !== '1', 'real agents on the person\'s machine: set YAWBLE_REAL_DEMO=1');
  test.setTimeout(80 * 60_000);
  const root = process.env.YAWBLE_HELLO_ROOT!;
  const team = process.env.YAWBLE_HELLO_TEAM ?? 'hello';
  const shots = path.join(root, 'screenshots');
  mkdirSync(shots, { recursive: true });
  const shellDir = path.resolve(__dirname, '..', '..');
  const app = await electron.launch({ args: [shellDir], env: { ...process.env, ...companionHome(), YAWBLE_DATA_DIR: path.join(root, 'data') } as Record<string, string> });
  try {
    const main = await app.firstWindow();
    await main.setViewportSize({ width: 1300, height: 860 });
    await expect(main.locator('[data-test=new-team]').first()).toBeVisible({ timeout: 60_000 });
    await main.locator('[data-test=new-team]').first().click();
    await main.locator('[data-test=team-name]').fill(team);
    if (process.env.YAWBLE_HELLO_REPO) await main.locator('[data-test=team-repo]').fill(process.env.YAWBLE_HELLO_REPO);
    await main.locator('[data-test=manager-cli]').selectOption(process.env.YAWBLE_HELLO_MANAGER_CLI ?? 'claude');
    await main.screenshot({ path: path.join(shots, '1-new-team.png') });
    await main.locator('[data-test=create-team]').click();
    await expect(main.locator('[data-test=team-title]')).toContainText(team, { timeout: 30_000 });
    await main.locator('[data-test=tell-text]').fill(instruction);
    await main.locator('[data-test=tell-send]').click();
    await expect(main.locator('[data-test=tell-sent]')).toBeVisible({ timeout: 30_000 });
    await main.waitForTimeout(5_000);
    await main.screenshot({ path: path.join(shots, '2-told.png') });
    // The app stays open while the team works (closing it would interrupt the runs). The caller ends the wait by
    // creating <root>/done once the workflow is complete; a screenshot every two minutes records the progress.
    const done = path.join(root, 'done');
    const deadline = Date.now() + 75 * 60_000;
    for (let i = 3; Date.now() < deadline && !existsSync(done); i++) {
      for (let s = 0; s < 24 && !existsSync(done); s++) await main.waitForTimeout(5_000);
      await main.screenshot({ path: path.join(shots, `${i}-progress.png`) });
    }
    await main.screenshot({ path: path.join(shots, 'final.png') });
  } finally {
    await app.close();
    removeCompanionHomes();
  }
});
