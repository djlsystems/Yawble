#!/usr/bin/env node
// Starts an INSTALLED Yawble (the executable an installer put on disk) and checks that its window shows
// the bundled core's version, the sandbox contract and getAvailableBackends(), as the window test does for
// the development build. Prints the startup probe's host checks. Exits non-zero if the window never shows them.
// Also checks the guardrail check: resources/bin/yawble-guardcheck is installed and runs from there, and the
// Guardrails panel says the check is available.
//
//   node packaging/smoke-installed.mjs [--host-ready] <path to the installed executable> [screenshot.png]
//
// --host-ready: also fail if the window shows any failed host check (Linux: the AppArmor profile, bwrap, slirp4netns).
//
// Uses Playwright from shell/node_modules (cd shell && npm ci). YAWBLE_E2E_NO_SANDBOX=1 adds --no-sandbox, for
// hosts where Chromium's sandbox cannot start (containers); the app itself never disables it.
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(path.join(here, '..', 'shell', 'package.json'));
const { _electron: electron } = require('playwright');

const argv = process.argv.slice(2);
const hostReady = argv[0] === '--host-ready';
const [executablePath, shot] = hostReady ? argv.slice(1) : argv;
if (!executablePath) throw new Error('usage: smoke-installed.mjs <installed executable> [screenshot.png]');
// The core's launcher needs <app root>/data (every run's deny list covers the app root), so the data dir is named data.
const appRoot = mkdtempSync(path.join(os.tmpdir(), 'yawble-smoke-'));
const dataDir = path.join(appRoot, 'data');
const env = { ...process.env, YAWBLE_DATA_DIR: dataDir };
// The installed app must use the core it bundles, not a development core from this environment.
delete env.YAWBLE_CORE_PATH;
const app = await electron.launch({
  executablePath,
  args: process.env.YAWBLE_E2E_NO_SANDBOX === '1' ? ['--no-sandbox'] : [],
  env,
  timeout: 60_000,
});
// The shell forwards the core's stderr as "[core] ...". The core's launcher logs that it is off, and why, when the
// install layout cannot run agents.
let coreLog = '';
app.process().stderr?.on('data', (d) => { coreLog += d.toString(); });
let ok = false;
try {
  const window = await app.firstWindow({ timeout: 60_000 });
  // The startup probe's warning is on the main window; the core's identity is on the System panel.
  await window.locator('[data-test=host-checks-probe][data-state=read]').waitFor({ state: 'attached', timeout: 60_000 });
  const hostChecks = (await window.locator('[data-test=host-checks] li').allTextContents()).map((t) => t.trim());
  // The Guardrails panel is off with "Not available: ..." when the core cannot find <install>/bin/yawble-guardcheck
  // or its launcher is off. The global policy's Guardrails tab on the Settings page holds the panel; read whether it is available.
  await window.locator('[data-test=open-app-settings]').click();
  await window.locator('[data-test=open-global-policy]').click();
  const guardrailAvailable = window.locator('[data-test=guardrail-none]');
  const guardrailOff = window.locator('[data-test=guardrail-unavailable]');
  await guardrailAvailable.or(guardrailOff).waitFor({ state: 'attached', timeout: 60_000 });
  const guardrail = (await guardrailOff.count()) > 0 ? (await guardrailOff.textContent())?.trim() : 'available';
  await window.locator('[data-test=open-system]').click();
  const version = window.locator('[data-test=core-version]');
  await version.waitFor({ timeout: 60_000 });
  const info = {
    title: await window.title(),
    coreVersion: await version.textContent(),
    sdk: await window.locator('[data-test=sandbox-sdk]').textContent(),
    contract: await window.locator('[data-test=sandbox-contract]').textContent(),
    backends: await window.locator('[data-test=backends-json]').textContent(),
    hostChecks,
    guardrail,
    resourcesPath: await app.evaluate(() => process.resourcesPath),
    packaged: await app.evaluate(({ app }) => app.isPackaged),
  };
  console.log(JSON.stringify(info, null, 2));
  // The title names the open page: "Yawble", or "<page> · Yawble".
  if (!/(^| · )Yawble$/.test(info.title)) throw new Error(`window title is ${info.title}`);
  if (!/^\d+\.\d+\.\d+/.test(info.coreVersion ?? '')) throw new Error(`no core version in the window: ${info.coreVersion}`);
  if (!info.contract?.includes('1.0.0 · telemetry off')) throw new Error(`unexpected sandbox contract line: ${info.contract}`);
  if (hostReady && info.hostChecks.length > 0) throw new Error(`the startup probe says this host is not ready: ${info.hostChecks.join(' | ')}`);
  if (!info.packaged) throw new Error('the app does not think it is packaged, so it is not using its bundled core');
  const bridge = path.join(info.resourcesPath, 'bin', process.platform === 'win32' ? 'yawble-bridge.exe' : 'yawble-bridge');
  if (!existsSync(bridge)) throw new Error(`the installed app has no bridge at ${bridge}`);
  // The guardrail check's probe, beside the bridge: it must be there and run from the installed location. An empty plan
  // (no reads, writes or network attempts) makes it report only what it sees of its context, so this is safe on the host.
  const probe = path.join(info.resourcesPath, 'bin', process.platform === 'win32' ? 'yawble-guardcheck.exe' : 'yawble-guardcheck');
  if (!existsSync(probe)) throw new Error(`the installed app has no guardrail probe at ${probe}`);
  const plan = Buffer.from(JSON.stringify({ v: 1, nonce: 'installer-smoke' })).toString('base64url');
  const ran = spawnSync(probe, ['--plan', plan], { encoding: 'utf8', timeout: 60_000 });
  const context = (ran.stdout ?? '').split('\n').filter(Boolean).map((l) => JSON.parse(l)).find((r) => r.id === 'sandbox.context');
  if (ran.status !== 0 || !context) throw new Error(`the installed guardrail probe did not run (exit ${ran.status}, ${ran.error?.message ?? ran.stderr}): ${ran.stdout}`);
  console.log(`installed guardrail probe ran: ${context.evidence}`);
  if (info.guardrail !== 'available') throw new Error(`the installed app's guardrail check is off: ${info.guardrail}`);
  // Every run grants roles/, so it must exist. No toolchain/ ships: the app runs the person's own installed CLIs.
  if (!existsSync(path.join(info.resourcesPath, 'roles'))) throw new Error('the installed app has no roles folder, which every agent run grants');
  if (existsSync(path.join(info.resourcesPath, 'toolchain'))) throw new Error('the installed app still ships a toolchain folder; agent CLIs are the person\'s own');
  const off = /the sandbox launcher is off: [^\n]*/.exec(coreLog);
  if (off) throw new Error(`the installed core cannot launch agents: ${off[0]}`);
  if (shot) {
    await new Promise((r) => setTimeout(r, 1_000));
    await window.screenshot({ path: shot, timeout: 10_000 }).catch((e) => console.log(`screenshot skipped: ${e.message}`));
  }
  ok = true;
} finally {
  await app.close().catch(() => {});
  rmSync(appRoot, { recursive: true, force: true });
}
console.log(ok ? 'installed app: OK' : 'installed app: FAILED');
process.exit(ok ? 0 : 1);
