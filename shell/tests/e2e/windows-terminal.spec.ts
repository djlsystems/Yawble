import { _electron as electron, expect, test } from '@playwright/test';
import { companionHome, removeCompanionHomes } from './companion-home';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

// Opt-in (YAWBLE_REAL_DEMO=1), on the data of windows-demo.spec.ts: each agent's terminal window opens its real CLI
// (Claude Code, Grok) interactively in the sandbox, and the TUI is screenshotted as the person sees it. Nothing is
// submitted: "hello" is typed into the prompt but never sent, so no model call is made.
test('real terminal windows: the Claude and Grok TUIs draw for the window they are shown in', async () => {
  test.skip(process.env.YAWBLE_REAL_DEMO !== '1', 'real agents on the person\'s machine: set YAWBLE_REAL_DEMO=1');
  test.setTimeout(5 * 60_000);
  const root = process.env.YAWBLE_DEMO_ROOT!;
  const shots = path.join(root, 'screenshots', 'terminal');
  mkdirSync(shots, { recursive: true });
  const shellDir = path.resolve(__dirname, '..', '..');
  const app = await electron.launch({ args: [shellDir], env: { ...process.env, ...companionHome(), YAWBLE_DATA_DIR: path.join(root, 'data') } as Record<string, string> });
  try {
    const main = await app.firstWindow();
    await expect(main.locator('[data-test=team-title]')).toContainText('greeter', { timeout: 60_000 });
    for (const agent of ['Ada', 'Ben']) {
      // The agent's terminal opens in the main window's terminal pane (one at a time).
      await main.locator(`[data-test=agent-${agent}] [data-test=open-terminal]`).click();
      const term = main.locator('[data-test=terminal-pane]');
      await term.waitFor();
      await main.setViewportSize({ width: 1500, height: 800 });
      await term.locator('[data-test=open-interactive]').click();
      await expect(term.locator('[data-test=terminal-state]')).toContainText('Interactive', { timeout: 60_000 });
      await main.waitForTimeout(8000);
      await main.screenshot({ path: path.join(shots, `${agent}-1-opened.png`) });
      await term.locator('.xterm-helper-textarea').focus();
      await main.keyboard.type('hello', { delay: 80 });
      await main.waitForTimeout(2000);
      await main.screenshot({ path: path.join(shots, `${agent}-2-typed-hello.png`) });
      await main.setViewportSize({ width: 1700, height: 900 });
      await main.waitForTimeout(3000);
      await main.screenshot({ path: path.join(shots, `${agent}-3-resized.png`) });
      await term.locator('[data-test=terminal-stop]').click();
      await expect(term.locator('[data-test=open-interactive]')).toBeVisible({ timeout: 30_000 });
      await term.locator('[data-test=terminal-pane-close]').click();
    }
  } finally {
    await app.close();
    removeCompanionHomes();
  }
});
