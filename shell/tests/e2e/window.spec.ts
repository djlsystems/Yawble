import { _electron as electron, expect, test } from '@playwright/test';
import { companionHome, removeCompanionHomes } from './companion-home';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// The real app opens a window that shows the core's version and
// getAvailableBackends(), fetched from the real .NET core over the local socket / named pipe.
// Needs: `npm run build` here and `dotnet build` of the core (or YAWBLE_CORE_PATH). On Linux without a
// display, run under xvfb-run.
test('window shows core version and getAvailableBackends() over IPC', async () => {
  const dataDir = mkdtempSync(path.join(os.tmpdir(), 'yawble-e2e-'));
  const shellDir = path.resolve(__dirname, '..', '..');
  // Test-harness switch only: Chromium's own sandbox needs user namespaces that containers and
  // Ubuntu 24.04's AppArmor default refuse. The app itself never disables it.
  const extra = process.env.YAWBLE_E2E_NO_SANDBOX === '1' ? ['--no-sandbox'] : [];
  const app = await electron.launch({
    args: [...extra, shellDir],
    env: { ...process.env, ...companionHome(), YAWBLE_DATA_DIR: dataDir } as Record<string, string>,
  });
  try {
    const window = await app.firstWindow();
    await expect(window).toHaveTitle('Yawble');
    // The core's identity and the backend probe are on the System panel.
    await window.locator('[data-test=open-app-settings]').click();
    await window.locator('[data-test=open-system]').click();
    const version = window.locator('[data-test=core-version]');
    await expect(version).toHaveText(/^\d+\.\d+\.\d+/, { timeout: 60_000 });
    await expect(window.locator('[data-test=sandbox-contract]')).toContainText('1.0.0 · telemetry off');
    await expect(window.locator('[data-test=sandbox-sdk]')).toContainText('1.0.1');

    const backendsJson = await window.locator('[data-test=backends-json]').textContent();
    const backends = JSON.parse(backendsJson ?? 'null');
    expect(Array.isArray(backends)).toBe(true);
    const rows = await window.locator('[data-test=backend-row]').count();
    expect(rows === backends.length || (backends.length === 0 && (await window.locator('[data-test=no-backends]').count()) === 1)).toBe(true);

    console.log(`core version shown: ${await version.textContent()}`);
    console.log(`getAvailableBackends() shown: ${backendsJson}`);
    const shot = process.env.YAWBLE_E2E_SCREENSHOT ?? path.join(shellDir, 'test-results', 'window.png');
    // The window is created hidden and shown on 'ready-to-show'; under Xvfb (no window manager) the DOM can be
    // asserted before the window is mapped and painted, and Page.captureScreenshot then fails with "Unable to
    // capture screenshot" (seen intermittently on the ubuntu-24.04 runner at one SHA). Wait for it to be visible,
    // then allow a few paint intervals. Still required: it fails if no capture succeeds.
    await expect
      .poll(() => app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().some((w) => w.isVisible())), { timeout: 15_000 })
      .toBe(true);
    await expect(async () => {
      await window.screenshot({ path: shot, timeout: 5_000 });
    }).toPass({ intervals: [250, 500, 1_000], timeout: 15_000 });
    console.log(`screenshot: ${shot}`);
  } finally {
    await app.close();
    removeCompanionHomes();
    rmSync(dataDir, { recursive: true, force: true });
  }
});
