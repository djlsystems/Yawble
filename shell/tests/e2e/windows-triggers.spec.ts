import { _electron as electron, expect, test } from '@playwright/test';
import { companionHome, removeCompanionHomes } from './companion-home';
import path from 'node:path';

// Opt-in (YAWBLE_REAL_DEMO=1): the Triggers dialog against the real core and a real agent. On an existing team
// (YAWBLE_TRIGGER_ROOT: the app root with data/, YAWBLE_TRIGGER_TEAM, YAWBLE_TRIGGER_MEMBER), it creates a daily trigger
// for the member, presses Run now and waits until the member's card shows it running or it ran. One small model run.
test('real triggers: create a trigger in the dialog and run it now', async () => {
  test.skip(process.env.YAWBLE_REAL_DEMO !== '1', 'real agents on the person\'s machine: set YAWBLE_REAL_DEMO=1');
  test.setTimeout(15 * 60_000);
  const root = process.env.YAWBLE_TRIGGER_ROOT!;
  const team = process.env.YAWBLE_TRIGGER_TEAM!;
  const member = process.env.YAWBLE_TRIGGER_MEMBER!;
  // Optional: the trigger's name and instruction (default: a ping that hands back), and its Manager-wake choice.
  const name = process.env.YAWBLE_TRIGGER_NAME ?? 'pong-check';
  const instruction = process.env.YAWBLE_TRIGGER_INSTRUCTION ?? 'Reply with the single word pong, then hand back with delivered: pong.';
  const wake = process.env.YAWBLE_TRIGGER_WAKE;
  const shellDir = path.resolve(__dirname, '..', '..');
  const app = await electron.launch({ args: [shellDir], env: { ...process.env, ...companionHome(), YAWBLE_DATA_DIR: path.join(root, 'data') } as Record<string, string> });
  try {
    const main = await app.firstWindow();
    await main.setViewportSize({ width: 1300, height: 860 });
    await main.locator('[data-test=team-item]', { hasText: team }).first().click();
    await expect(main.locator('[data-test=team-title]')).toContainText(team, { timeout: 30_000 });
    await main.locator('[data-test=open-triggers]').click();
    await main.locator('[data-test=trigger-new]').click();
    await main.locator('[data-test=trigger-name]').fill(name);
    await main.locator('[data-test=trigger-member]').selectOption({ label: `${member} (member)` });
    await main.locator('[data-test=trigger-every]').fill('1');
    await main.locator('[data-test=trigger-unit]').selectOption('86400');
    await expect(main.locator('[data-test=trigger-preview]')).toContainText('Next:', { timeout: 10_000 });
    await main.locator('[data-test=trigger-instruction]').fill(instruction);
    if (wake) await main.locator('[data-test=trigger-wake-manager]').selectOption(wake);
    await main.locator('[data-test=trigger-save]').click();
    await expect(main.locator('[data-test=triggers-note]')).toContainText(`Saved ${name}`, { timeout: 10_000 });
    await main.screenshot({ path: path.join(root, 'trigger-1-saved.png') });
    await main.locator(`[data-test="trigger-${name}"] [data-test=trigger-run-now]`).click();
    await expect(main.locator('[data-test=triggers-note]')).toContainText(`told ${member} now`, { timeout: 10_000 });
    await expect(main.locator(`[data-test="trigger-${name}"]`)).toContainText('fired');
    await main.screenshot({ path: path.join(root, 'trigger-2-fired.png') });
    await main.locator('[data-test=modal-close]').click();
    // The member's run starts, then ends: the team shows it running, then nobody running.
    await expect(main.locator('[data-test=team-title]')).toContainText('1 running', { timeout: 60_000 });
    await expect(main.locator('[data-test=team-title]')).toContainText('0 running', { timeout: 10 * 60_000 });
    // The run's end may wake the Manager: give the pump time to admit it, then wait for it to end too.
    await main.waitForTimeout(15_000);
    await expect(main.locator('[data-test=team-title]')).toContainText('0 running', { timeout: 10 * 60_000 });
    await main.screenshot({ path: path.join(root, 'trigger-3-done.png') });
  } finally {
    await app.close();
    removeCompanionHomes();
  }
});
