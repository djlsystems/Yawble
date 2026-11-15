// The Windows installer's NSIS hooks (packaging/nsis-include.mjs), run in a fake NSIS (fake-nsis.mjs) with the command
// line, environment, running processes and the person's answers faked. No makensis, no installer, nothing removed.
// Run: node --test packaging/tests/   (scripts/test-all.sh runs it)
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { chosenFolderVariables, dataFolders, keepDataFlag, nsisInclude, runningImages } from '../nsis-include.mjs';
import { macros, run } from './fake-nsis.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const text = nsisInclude({ buildVersion: '2026.11.14.3' });
const uninstall = (fake = {}) => run(text, 'customUnInstall', fake);
const app = 'C:\\Users\\ana\\AppData\\Local\\Yawble';
const companion = 'C:\\Users\\ana\\AppData\\Roaming\\yawble-companion';

test('a real uninstall asks, and Yes removes the app folder and the companion folder', () => {
  const r = uninstall({ answers: ['IDYES'] });
  assert.equal(r.prompts.length, 1);
  assert.match(r.prompts[0], /Also remove your Yawble data\?/);
  assert.match(r.prompts[0], /No: keep my data/);
  assert.deepEqual(r.removed, [app, companion]);
  assert.equal(r.aborted, null);
});

test('Keep my data (No) removes nothing', () => {
  const r = uninstall({ answers: ['IDNO'] });
  assert.deepEqual(r.removed, []);
  assert.equal(r.exec.length, 0, 'no running check when nothing is removed');
});

test('a silent uninstall removes the data without asking', () => {
  const r = uninstall({ silent: true, params: '/S /allusers' });
  assert.deepEqual(r.prompts, []);
  assert.deepEqual(r.removed, [app, companion]);
});

test(`${keepDataFlag} keeps the data, silent or not, without asking`, () => {
  for (const silent of [true, false]) {
    const r = uninstall({ silent, params: `/S ${keepDataFlag}` });
    assert.deepEqual(r.removed, []);
    assert.deepEqual(r.prompts, []);
    assert.ok(r.details.some((d) => d.includes('kept')));
  }
});

test('an update (${isUpdated}) never removes the data, even with no flag that keeps it', () => {
  for (const silent of [true, false]) {
    const r = uninstall({ updated: true, silent, params: '/S --updated' });
    assert.deepEqual(r.removed, []);
    assert.deepEqual(r.prompts, []);
    assert.equal(r.exec.length, 0);
  }
});

test('an installer running the old uninstaller (/KEEP_APP_DATA, e.g. --delete-app-data without --updated) keeps the data', () => {
  const r = uninstall({ silent: true, params: '/S /KEEP_APP_DATA /currentuser --delete-app-data' });
  assert.deepEqual(r.removed, []);
});

test('a per-machine uninstall removes the data of the account that runs it, not ProgramData', () => {
  const r = uninstall({ silent: true, installMode: 'all', params: '/S /allusers' });
  assert.deepEqual(r.removed, [app, companion]);
  assert.ok(!r.removed.some((p) => p.includes('ProgramData')));
});

for (const variable of chosenFolderVariables) {
  test(`${variable} inside the app folder keeps the app folder, the companion folder still goes`, () => {
    const r = uninstall({ silent: true, env: { [variable]: `${app}\\mine` } });
    assert.deepEqual(r.removed, [companion]);
    assert.ok(r.details.some((d) => d.includes(`Kept ${app}: ${variable}`)));
  });

  test(`${variable} naming the folder itself, a parent, or with / and another case keeps it`, () => {
    for (const chosen of [app, `${app}\\`, 'C:\\Users\\ana\\AppData\\Local', 'c:/users/ana/appdata/local/yawble/x']) {
      const r = uninstall({ silent: true, env: { [variable]: chosen } });
      assert.ok(!r.removed.includes(app), `${variable}=${chosen} removed ${app}`);
    }
  });

  test(`${variable} elsewhere is left alone and does not stop the removal`, () => {
    const r = uninstall({ silent: true, env: { [variable]: 'D:\\yawble-data' } });
    assert.deepEqual(r.removed, [app, companion]);
    assert.ok(!r.removed.includes('D:\\yawble-data'));
  });
}

test('a folder name that only starts like the app folder (YawbleOld) does not keep it', () => {
  const r = uninstall({ silent: true, env: { YAWBLE_DATA_DIR: `${app}Old` } });
  assert.deepEqual(r.removed, [app, companion]);
});

test('a chosen folder that cannot be resolved keeps both folders', () => {
  const r = uninstall({ silent: true, env: { YAWBLE_HOME: 'whatever' }, fullPathFails: true });
  assert.deepEqual(r.removed, []);
});

test('a relative chosen folder is resolved against where the uninstaller runs', () => {
  const r = uninstall({ silent: true, env: { YAWBLE_DATA_DIR: 'data' }, cwd: app });
  assert.deepEqual(r.removed, [companion]);
});

test('Yawble still running: it waits, and goes on once it has quit', () => {
  const r = uninstall({ silent: true, running: (n) => (n < 4 ? new Set(['yawble-core.exe']) : new Set()) });
  assert.equal(r.aborted, null);
  assert.equal(r.sleeps, 2);
  assert.deepEqual(r.removed, [app, companion]);
});

for (const image of runningImages) {
  test(`${image} still running: a silent uninstall stops with nothing removed`, () => {
    const r = uninstall({ silent: true, running: new Set([image]) });
    assert.match(r.aborted, new RegExp(`Yawble is running \\(${image.replace('.', '\\.')}\\)`));
    assert.deepEqual(r.removed, []);
  });
}

test('still running, interactive: Cancel stops with nothing removed', () => {
  const r = uninstall({ answers: ['IDYES', 'IDCANCEL'], running: new Set(['yawble.exe']) });
  assert.equal(r.prompts.length, 2);
  assert.match(r.prompts[1], /Yawble is still running \(yawble\.exe\)/);
  assert.match(r.prompts[1], /Cancel stops the uninstall: nothing is removed/);
  assert.ok(r.aborted);
  assert.deepEqual(r.removed, []);
});

test('still running, interactive: Retry after quitting goes on', () => {
  let asked = 0;
  const r = uninstall({
    answers: ['IDYES', 'IDRETRY'],
    // Running through the five waits and the first question, quit before the Retry.
    running: (n) => { asked = n; return n < 12 ? new Set(['yawble.exe']) : new Set(); },
  });
  assert.equal(r.aborted, null);
  assert.equal(r.prompts.length, 2);
  assert.ok(asked >= 12);
  assert.deepEqual(r.removed, [app, companion]);
});

test('the running check is for this user, and for both the window and the core', () => {
  const r = uninstall({ silent: true });
  assert.equal(r.exec.length, runningImages.length);
  for (const [i, image] of runningImages.entries()) {
    assert.match(r.exec[i], /tasklist \/FI "USERNAME eq %USERNAME%"/);
    assert.ok(r.exec[i].includes(`/FI "IMAGENAME eq ${image}"`));
    assert.ok(r.exec[i].startsWith('"C:\\Windows\\System32\\cmd.exe" /C '));
  }
});

test('customInstall still writes the build version as DisplayVersion', () => {
  const r = run(text, 'customInstall');
  assert.deepEqual(r.details, ['reg SHELL_CONTEXT ${UNINSTALL_REGISTRY_KEY} DisplayVersion 2026.11.14.3']);
  assert.ok(!nsisInclude({ buildVersion: '2026.11.14.3+dev.abc"x' }).includes('abc"x'));
});

test('the include is CRLF, its macros close, and it defines only electron-builder hooks and its own helper', () => {
  assert.ok(!/[^\r]\n/.test(text));
  assert.deepEqual([...macros(text).keys()], ['customInstall', '_yawbleOverlaps', 'customUnInstall']);
});

test('the folders are where the app and the companion keep them', () => {
  assert.deepEqual(dataFolders, { app: '$LOCALAPPDATA\\Yawble', companion: '$APPDATA\\yawble-companion' });
  const appFolder = readFileSync(path.join(repo, 'shell', 'src', 'main', 'app-folder.ts'), 'utf8');
  assert.match(appFolder, /win32'\) \{\s*return path\.win32\.join\(env\.LOCALAPPDATA \|\| .+?\), 'Yawble'\)/);
  const discovery = readFileSync(path.join(repo, 'core', 'src', 'Yawble.Core', 'Companion', 'ControlDiscovery.cs'), 'utf8');
  assert.match(discovery, /DefaultFolderName = "yawble-companion"/);
  assert.match(discovery, /IsWindows\(\)\)\s*return Path\.Combine\(Var\("APPDATA"\)/);
  assert.match(discovery, /HomeVariable = "YAWBLE_HOME"/);
  assert.match(readFileSync(path.join(repo, 'shell', 'src', 'main', 'index.ts'), 'utf8'), /process\.env\.YAWBLE_DATA_DIR/);
});

test('build.mjs writes this include and keeps electron-builder\'s own app-data removal off', () => {
  const build = readFileSync(path.join(repo, 'packaging', 'build.mjs'), 'utf8');
  assert.match(build, /import\('\.\/nsis-include\.mjs'\)/);
  assert.match(build, /writeFileSync\(nsisInclude, nsisIncludeText\(\{ buildVersion \}\)\)/);
  assert.match(build, /include: nsisInclude,/);
  assert.match(build, /deleteAppDataOnUninstall: false,/);
});
