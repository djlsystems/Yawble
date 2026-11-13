import { _electron as electron, expect, test, type Page } from '@playwright/test';
import { companionHome, removeCompanionHomes } from './companion-home';
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

// The real demo on THIS machine (opt-in: YAWBLE_REAL_DEMO=1). It uses the person's own installed Claude Code and Grok
// and their own sign-ins (the app only detects them), real sandboxes, and real model calls on the person's
// accounts. It creates one team on a sample repository, tells the Manager a small task, and waits for the workflow
// to finish, taking screenshots. The data folder is kept so the app can be reopened on it afterwards.
//
//   YAWBLE_REAL_DEMO=1 YAWBLE_DEMO_ROOT=<folder> YAWBLE_DEMO_REPO=<git repo> YAWBLE_INSTALL_DIR=<install> npx playwright test windows-demo
test('real demo: a Claude Manager splits a task between a Claude member and a Grok member', async () => {
  test.skip(process.env.YAWBLE_REAL_DEMO !== '1', 'real agents on the person\'s accounts: set YAWBLE_REAL_DEMO=1');
  test.setTimeout(20 * 60_000);
  const root = process.env.YAWBLE_DEMO_ROOT!;
  const repo = process.env.YAWBLE_DEMO_REPO!;
  const shots = path.join(root, 'screenshots');
  mkdirSync(shots, { recursive: true });
  const shellDir = path.resolve(__dirname, '..', '..');
  const app = await electron.launch({
    args: [shellDir],
    env: { ...process.env, ...companionHome(), YAWBLE_DATA_DIR: path.join(root, 'data') } as Record<string, string>,
  });
  let n = 0;
  const shot = async (page: Page, name: string) => {
    n += 1;
    await page.screenshot({ path: path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`) });
  };
  try {
    const main = await app.firstWindow();
    main.on('dialog', (d) => void d.accept());
    await main.setViewportSize({ width: 1400, height: 900 });
    await expect(main.locator('[data-test=slots]')).toContainText('Run slots', { timeout: 60_000 });
    await shot(main, 'first-start');

    // The person's own CLIs, detected.
    await main.locator('[data-test=open-app-settings]').click();
    await main.locator('[data-test=open-agents]').click();
    for (const cli of ['claude', 'grok'])
      await expect(main.locator(`[data-test=agent-cli-${cli}] [data-test=cli-found]`)).toHaveText('Found', { timeout: 60_000 });
    await shot(main, 'agents-detected');
    await main.locator('[data-test=nav-teams]').click();

    // One team: Manager (Claude Code), Ada (Claude Code), Ben (Grok).
    await main.locator('[data-test=new-team]').first().click();
    await main.locator('[data-test=team-name]').fill('greeter');
    await main.locator('[data-test=team-repo]').fill(repo);
    await main.locator('[data-test=member-name-0]').fill('Ada');
    await main.locator('[data-test=add-member-row]').click();
    await main.locator('[data-test=member-name-1]').fill('Ben');
    await main.locator('[data-test=member-cli-1]').selectOption('grok');
    await shot(main, 'new-team');
    await main.locator('[data-test=create-team]').click();
    await expect(main.locator('[data-test=team-title]')).toContainText('Team greeter · 3 agents');
    await shot(main, 'team-created');

    // The task.
    await main.locator('[data-test=tell-text]').fill(
      'Small task for the greeter repo. Ada: add a farewell(name) function to greet.js that returns "Goodbye, <name>!" ' +
      'and export it next to greet. Ben: add farewell(name) to the API list in README.md. Each of you commits your change ' +
      'in your own clone and hands back. Keep it to those two edits.');
    await main.locator('[data-test=tell-send]').click();
    await expect(main.locator('[data-test=tell-sent]')).toBeVisible({ timeout: 30_000 });

    // Watch the agents work: a screenshot whenever the cards change, until all three are idle again.
    const states = main.locator('[data-test=agent-state]');
    let last = '';
    const deadline = Date.now() + 18 * 60_000;
    let sawRunning = false;
    while (Date.now() < deadline) {
      const now = (await states.allTextContents()).join(' | ');
      if (now !== last) {
        last = now;
        console.log(`[${new Date().toISOString()}] ${now}`);
        await shot(main, 'cards');
      }
      if (/Running|Queued/.test(now)) sawRunning = true;
      if (sawRunning && !/Running|Queued/.test(now)) break;
      await main.waitForTimeout(2000);
    }
    await shot(main, 'workflow-done');

    // Ben's terminal in the terminal pane: his last run (replay).
    await main.locator('[data-test=agent-Ben] [data-test=open-terminal]').click();
    await main.locator('[data-test=terminal-pane]').waitFor();
    await main.waitForTimeout(3000);
    await main.screenshot({ path: path.join(shots, `${String(++n).padStart(2, '0')}-terminal-ben.png`) });

    // What the agents committed (branches fetched back into the team's repository).
    console.log(execFileSync('git', ['-C', repo, 'branch', '-a', '-v'], { encoding: 'utf8' }));
    expect(sawRunning).toBe(true);
  } finally {
    await app.close();
    removeCompanionHomes();
  }
});
