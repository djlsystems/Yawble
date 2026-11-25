// Where packaging/windows-signing.cjs finds signtool.exe and Azure.CodeSigning.Dlib.dll. The new names SIGNTOOL and
// SIGNING_DLIB (which a host task can be given) win over the old YAWBLE_ names, the old names still work, and with neither
// it falls back to the Windows SDK and the Artifact Signing client tools' folder. Fakes only: empty files in a temp folder
// stand in for the tools, and ProgramFiles(x86) / LOCALAPPDATA point there. Nothing is signed, no real signtool runs.
// Run: node --test packaging/tests/
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, test } from 'node:test';

const require = createRequire(import.meta.url);
const { findSigntool, findDlib } = require('../windows-signing.cjs');

const NAMES = ['SIGNTOOL', 'YAWBLE_SIGNTOOL', 'SIGNING_DLIB', 'YAWBLE_SIGNING_DLIB', 'LOCALAPPDATA', 'ProgramFiles(x86)'];
let saved;
let root;

const touch = (...parts) => {
  const p = path.join(root, ...parts);
  mkdirSync(path.dirname(p), { recursive: true });
  writeFileSync(p, '');
  return p;
};

beforeEach(() => {
  saved = Object.fromEntries(NAMES.map((n) => [n, process.env[n]]));
  for (const n of NAMES) delete process.env[n];
  root = mkdtempSync(path.join(os.tmpdir(), 'yawble-signing-test-'));
  process.env.LOCALAPPDATA = path.join(root, 'local');
  process.env['ProgramFiles(x86)'] = path.join(root, 'pf86');
});

afterEach(() => {
  for (const n of NAMES) {
    if (saved[n] === undefined) delete process.env[n];
    else process.env[n] = saved[n];
  }
  rmSync(root, { recursive: true, force: true });
});

test('SIGNTOOL wins over YAWBLE_SIGNTOOL', () => {
  process.env.SIGNTOOL = touch('new', 'signtool.exe');
  process.env.YAWBLE_SIGNTOOL = touch('old', 'signtool.exe');
  assert.equal(findSigntool(), process.env.SIGNTOOL);
});

test('YAWBLE_SIGNTOOL still works when SIGNTOOL is not set', () => {
  process.env.YAWBLE_SIGNTOOL = touch('old', 'signtool.exe');
  assert.equal(findSigntool(), process.env.YAWBLE_SIGNTOOL);
});

test('with neither set, signtool is the newest x64 one in the Windows SDK', () => {
  touch('pf86', 'Windows Kits', '10', 'bin', '10.0.19041.0', 'x64', 'signtool.exe');
  const newest = touch('pf86', 'Windows Kits', '10', 'bin', '10.0.22621.0', 'x64', 'signtool.exe');
  assert.equal(findSigntool(), newest);
});

test('with neither set and no SDK, the error names SIGNTOOL', () => {
  assert.throws(() => findSigntool(), /set SIGNTOOL\b/);
});

test('SIGNING_DLIB wins over YAWBLE_SIGNING_DLIB and the default folder', () => {
  touch('local', 'Microsoft', 'MicrosoftArtifactSigningClientTools', 'Azure.CodeSigning.Dlib.dll');
  process.env.SIGNING_DLIB = touch('new', 'Azure.CodeSigning.Dlib.dll');
  process.env.YAWBLE_SIGNING_DLIB = touch('old', 'Azure.CodeSigning.Dlib.dll');
  assert.equal(findDlib(), process.env.SIGNING_DLIB);
});

test('YAWBLE_SIGNING_DLIB still works when SIGNING_DLIB is not set', () => {
  touch('local', 'Microsoft', 'MicrosoftArtifactSigningClientTools', 'Azure.CodeSigning.Dlib.dll');
  process.env.YAWBLE_SIGNING_DLIB = touch('old', 'Azure.CodeSigning.Dlib.dll');
  assert.equal(findDlib(), process.env.YAWBLE_SIGNING_DLIB);
});

test('with neither set, the dlib is the one in the Artifact Signing client tools folder', () => {
  const dflt = touch('local', 'Microsoft', 'MicrosoftArtifactSigningClientTools', 'Azure.CodeSigning.Dlib.dll');
  assert.equal(findDlib(), dflt);
});

test('with no dlib anywhere, the error names SIGNING_DLIB', () => {
  assert.throws(() => findDlib(), /set SIGNING_DLIB\b/);
});
