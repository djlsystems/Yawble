import { _electron as electron, expect, test, type Page } from '@playwright/test';
import { companionHome, removeCompanionHomes } from './companion-home';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

// Opt-in (YAWBLE_REAL_DEMO=1), on the data of windows-demo.spec.ts: each agent's terminal window (its pane's ☰ > Open
// interactive CLI) opens its real CLI (Claude Code, Grok) interactively in the sandbox, and the TUI is screenshotted as
// the person sees it. Nothing is submitted: "hello" is typed into the prompt but never sent, so no model call is made.

/**
 * The TUI has drawn: the terminal's rows hold text (with `has` in it, when given) and read the same at two samples
 * half a second apart (the CLI finished drawing for this size). A TUI that keeps animating never reads the same; then
 * the screenshot is taken after the timeout anyway (these are screenshots for the person, not a check).
 */
async function drawn(win: Page, has?: string, timeout = 20_000) {
  let last = '';
  await expect.poll(async () => {
    const now = await win.locator('.xterm-rows').innerText().catch(() => '');
    const ok = now.trim() !== '' && now === last && (!has || now.includes(has));
    last = now;
    return ok;
  }, { timeout, intervals: [500] }).toBe(true).catch(() => { /* still animating: take the screenshot as it is */ });
}

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
      // The interactive CLI runs in the agent's own terminal window (the pane's terminal is never interactive).
      const pane = main.locator(`[data-test=agent-${agent}]`);
      await pane.waitFor();
      const opened = app.waitForEvent('window');
      await pane.locator('[data-test=member-more]').click();
      await pane.locator('[data-test=member-more-menu] [data-test=open-interactive-cli]').click();
      const win = await opened;
      await win.setViewportSize({ width: 1500, height: 800 });
      await win.locator('[data-test=open-interactive]').click();
      await expect(win.locator('[data-test=terminal-state]')).toContainText('Interactive', { timeout: 60_000 });
      await drawn(win, undefined, 30_000);
      await win.screenshot({ path: path.join(shots, `${agent}-1-opened.png`) });
      await win.locator('.xterm-helper-textarea').focus();
      await win.keyboard.type('hello', { delay: 80 });
      await drawn(win, 'hello');
      await win.screenshot({ path: path.join(shots, `${agent}-2-typed-hello.png`) });
      await win.setViewportSize({ width: 1700, height: 900 });
      await drawn(win, 'hello'); // redrawn for the new size
      await win.screenshot({ path: path.join(shots, `${agent}-3-resized.png`) });
      await win.locator('[data-test=terminal-stop]').click();
      await expect(win.locator('[data-test=open-interactive]')).toBeVisible({ timeout: 30_000 });
      await win.close();
    }
  } finally {
    await app.close();
    removeCompanionHomes();
  }
});
