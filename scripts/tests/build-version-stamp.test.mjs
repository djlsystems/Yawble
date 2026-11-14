// The version packaging/build.mjs stamps into installed files (scripts/build-version.mjs numericVersion,
// dotnetVersionProperties, electronBuilderVersion). Pure: no git, no dotnet, no electron-builder, no installer.
// Run: node --test scripts/tests/build-version-stamp.test.mjs   (scripts/test-all.sh runs it)
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { buildVersion, dotnetVersionProperties, electronBuilderVersion, numericVersion } from '../build-version.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));

test('a release is its own four-part number', () => {
  assert.equal(numericVersion('2026.11.14.3'), '2026.11.14.3');
  assert.equal(numericVersion('2026.11.14.10'), '2026.11.14.10');
});

test('leading zeros of month and day go (a file version is numbers, not text)', () => {
  assert.equal(numericVersion('2027.01.05.2'), '2027.1.5.2');
});

test('a dev build carries its last release numbers, without +dev.<commit>[.dirty]', () => {
  assert.equal(numericVersion('2026.11.14.3+dev.ee55691'), '2026.11.14.3');
  assert.equal(numericVersion('2026.11.14.3+dev.ee55691.dirty'), '2026.11.14.3');
});

test('before the first release, and with no git, it is 0.0.0.0', () => {
  assert.equal(numericVersion('0.0.0+dev.ee55691'), '0.0.0.0');
  assert.equal(numericVersion('0.0.0+dev.unknown'), '0.0.0.0');
});

test('every build-version case gives four numbers from its release part', () => {
  const cases = JSON.parse(readFileSync(path.join(here, 'build-version-cases.json'), 'utf8'));
  for (const c of cases) {
    const v = buildVersion({ describe: c.describe, sha: c.sha, dirty: c.dirty }).version;
    assert.equal(v, c.version, c.name);
    const n = numericVersion(v);
    assert.match(n, /^\d+\.\d+\.\d+\.\d+$/, c.name);
    assert.deepEqual(n.split('.').map(Number), v.split('+')[0].split('.').concat(['0']).slice(0, 4).map(Number), c.name);
  }
});

test('a part over 65534 (the most a file version holds) fails the build instead of wrapping', () => {
  assert.equal(numericVersion('2026.11.14.65534'), '2026.11.14.65534');
  assert.throws(() => numericVersion('2026.11.14.65535'), /65535 is over 65534/);
  assert.throws(() => numericVersion('2026.11.14.99999+dev.ee55691'), /over 65534/);
});

test('anything that is not a build version is refused', () => {
  for (const bad of ['', '1.2.3', 'v2026.11.14.3', '2026.11.14', '2026.11.14.3-rc', '2026.11.14.3+dev.XYZ', '2026.11.14.x', undefined, null, 20261114]) {
    assert.throws(() => numericVersion(bad), /not a build version/, String(bad));
  }
});

test('dotnet publish gets Version and FileVersion numeric, InformationalVersion the full version, no +<commit> added', () => {
  assert.deepEqual(dotnetVersionProperties('2026.11.14.3'), [
    '-p:Version=2026.11.14.3', '-p:FileVersion=2026.11.14.3', '-p:InformationalVersion=2026.11.14.3',
    '-p:IncludeSourceRevisionInInformationalVersion=false',
  ]);
  assert.deepEqual(dotnetVersionProperties('2027.01.05.2+dev.ee55691.dirty'), [
    '-p:Version=2027.1.5.2', '-p:FileVersion=2027.1.5.2', '-p:InformationalVersion=2027.01.05.2+dev.ee55691.dirty',
    '-p:IncludeSourceRevisionInInformationalVersion=false',
  ]);
  // Passed to dotnet as separate arguments without a shell: nothing in them a shell or MSBuild would split.
  for (const a of dotnetVersionProperties('2026.11.14.3+dev.ee55691.dirty')) assert.doesNotMatch(a, /[\s;"'%$]/);
});

test('electron-builder gets the numeric version as the app version, the file version and the build number', () => {
  assert.deepEqual(electronBuilderVersion('2026.11.14.3'), { extraMetadata: { version: '2026.11.14.3' }, buildVersion: '2026.11.14.3', buildNumber: '3' });
  assert.deepEqual(electronBuilderVersion('2026.11.14.3+dev.ee55691'), { extraMetadata: { version: '2026.11.14.3' }, buildVersion: '2026.11.14.3', buildNumber: '3' });
  assert.deepEqual(electronBuilderVersion('0.0.0+dev.unknown'), { extraMetadata: { version: '0.0.0.0' }, buildVersion: '0.0.0.0', buildNumber: '0' });
});

test('electron-builder product version: its three parts of the version, then buildNumber, give the same four numbers', () => {
  // app-builder-lib AppInfo.getVersionInWeirdWindowsForm: major.minor.patch from version (parseInt), then buildNumber.
  const { extraMetadata, buildNumber, buildVersion: fileVersion } = electronBuilderVersion('2027.01.05.12');
  const [major, minor, patch] = extraMetadata.version.split('.').map((p) => parseInt(p, 10));
  assert.equal(`${major}.${minor}.${patch}.${buildNumber}`, fileVersion);
  assert.equal(fileVersion, '2027.1.5.12');
});

test('packaging/build.mjs stamps every dotnet publish and the electron-builder config with these', () => {
  const src = readFileSync(path.join(here, '..', '..', 'packaging', 'build.mjs'), 'utf8');
  assert.match(src, /dotnetVersionProperties\(buildVersion\)/);
  assert.match(src, /electronBuilderVersion\(buildVersion\)/);
  // The one publish helper every project goes through carries the version properties.
  const publish = src.match(/const publish = \([^)]*\) =>\s*\n\s*run\('dotnet', \[([^\n]*)\]\);/);
  assert.ok(publish, 'the publish helper is where it was');
  assert.match(publish[1], /\.\.\.dotnetVersion\b/);
  assert.equal((src.match(/run\('dotnet', \['publish'/g) ?? []).length, 1, 'no publish bypasses the helper');
  assert.match(src.slice(src.indexOf('const config = {')), /^\s*\.\.\.appVersion,/m);
  // The staged core must say this build's version before anything is packaged.
  assert.match(src, /said !== buildVersion/);
});
