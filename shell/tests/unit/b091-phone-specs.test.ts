// B091 (card 484): the phone project is chosen by what a spec imports (tests/e2e/phone/specs.ts), and
// playwright.config.ts refuses to load when a phone spec falls outside it or skips phone/fixtures.
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { checkPhoneSpecs, isPhoneSpec, listSpecs, phoneSpecs, specMatcher } from '../e2e/phone/specs';

const E2E = path.resolve(__dirname, '..', 'e2e');

// The hand-kept list playwright.config.ts had before B091.
const OLD_PHONE_SPECS = [
  /[\\/]phone[\\/][^\\/]+\.spec\.ts$/,
  /[\\/](card320|card332|card347|card367|card372|card375|card430|card444|card477|member-pane-phone|phone-remote|team-settings-phone|tell-icons|workflows-visitor)[^\\/]*\.spec\.ts$/,
];

const GOOD = `import { expect, test } from './phone/fixtures';\nimport { buildRenderer, fakeRemote } from './phone/server';\n`;
const STRAY = `import { expect, test, type Browser } from '@playwright/test';\nimport { buildRenderer, builtDir, fakeRemote } from './phone/server';\n`;
const ELECTRON = `import { _electron as electron, expect, test } from '@playwright/test';\n`;

let tmp: string | undefined;
afterEach(() => { if (tmp) rmSync(tmp, { recursive: true, force: true }); tmp = undefined; });

function tree(files: Record<string, string>) {
  tmp = mkdtempSync(path.join(os.tmpdir(), 'b091-'));
  for (const [rel, src] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(tmp, rel)), { recursive: true });
    writeFileSync(path.join(tmp, rel), src);
  }
  return tmp;
}

describe('which specs are phone specs', () => {
  it('a spec importing phone/server, phone/fake-core or calling buildRenderer is one; every spec in phone/ is one; an Electron spec is not', () => {
    expect(isPhoneSpec('a.spec.ts', STRAY)).toBe(true);
    expect(isPhoneSpec('a.spec.ts', `import { answers } from './phone/fake-core';`)).toBe(true);
    expect(isPhoneSpec('a.spec.ts', `import { buildRenderer } from '../somewhere/else';`)).toBe(true);
    expect(isPhoneSpec('phone/rotate.spec.ts', `import { builtDir } from './server';`)).toBe(true);
    expect(isPhoneSpec('window.spec.ts', ELECTRON)).toBe(false);
    expect(isPhoneSpec('a.spec.ts', `import { x } from './telephone/thing';`)).toBe(false);
  });

  it('specMatcher matches exactly the listed files, with either separator, and not a name that only ends the same', () => {
    const m = specMatcher(['card355-confirm-dialog.spec.ts', 'phone/rotate.spec.ts']);
    expect(m.test('C:\\r\\shell\\tests\\e2e\\card355-confirm-dialog.spec.ts')).toBe(true);
    expect(m.test('/r/shell/tests/e2e/card355-confirm-dialog.spec.ts')).toBe(true);
    expect(m.test('C:\\r\\shell\\tests\\e2e\\phone\\rotate.spec.ts')).toBe(true);
    expect(m.test('C:\\r\\shell\\tests\\e2e\\xcard355-confirm-dialog.spec.ts')).toBe(false);
    expect(m.test('C:\\r\\shell\\tests\\e2e\\rotate.spec.ts')).toBe(false);
    expect(specMatcher([]).test('anything.spec.ts')).toBe(false);
  });
});

// B108 (card 500): B091's verify found two ways in that the static `from` test missed.
describe('a spec that loads phone/ by a dynamic import() or through a helper module (B108)', () => {
  const DYNAMIC = `import { expect, test } from '@playwright/test';\ntest('x', async () => { const { fakeRemote } = await import('./phone/server'); });\n`;

  it('a dynamic import(), require() or bare import of a phone/ module makes a phone spec, without reading other files', () => {
    expect(isPhoneSpec('a.spec.ts', DYNAMIC)).toBe(true);
    expect(isPhoneSpec('a.spec.ts', `const s = await import( "../e2e/phone/fake-core" );`)).toBe(true);
    expect(isPhoneSpec('a.spec.ts', 'const s = await import(`./phone/server`);')).toBe(true);
    expect(isPhoneSpec('a.spec.ts', `const { builtDir } = require('./phone/server');`)).toBe(true);
    expect(isPhoneSpec('a.spec.ts', `import './phone/server';`)).toBe(true);
    expect(isPhoneSpec('a.spec.ts', `export { fakeRemote } from './phone/server';`)).toBe(true);
    expect(isPhoneSpec('a.spec.ts', `const fs = await import('node:fs'); import('./telephone/x');`)).toBe(false);
  });

  it('a helper that imports phone/server makes the spec using it a phone spec, followed transitively; a plain helper does not', () => {
    const dir = tree({
      'via-helper.spec.ts': `import { expect, test } from '@playwright/test';\nimport { openPhone } from './helpers/open';\n`,
      'via-two.spec.ts': `import { expect, test } from '@playwright/test';\nimport { more } from './more';\n`,
      'via-dynamic-helper.spec.ts': `import { test } from '@playwright/test';\ntest('x', async () => { await import('./helpers/lazy.ts'); });\n`,
      'plain.spec.ts': `import { test } from '@playwright/test';\nimport { home } from './companion-home';\n`,
      'cycle.spec.ts': `import { test } from '@playwright/test';\nimport { a } from './cycle-a';\n`,
      'helpers/open.ts': `export { fakeRemote as openPhone } from '../phone/server';\n`,
      'helpers/lazy.ts': `export async function lazy() { return import('../phone/fake-core'); }\n`,
      'more.ts': `import { openPhone } from './helpers/open';\nexport const more = openPhone;\n`,
      'companion-home.ts': `import path from 'node:path';\nexport const home = path.join('a', 'b');\n`,
      'cycle-a.ts': `import { b } from './cycle-b';\nexport const a = 1;\n`,
      'cycle-b.ts': `import { a } from './cycle-a';\nexport const b = 2;\n`,
      'phone/server.ts': 'export const fakeRemote = 1;\n',
      'phone/fake-core.ts': 'export const answers = 1;\n',
    });
    // Without the folder the helper is not read, so only the spec's own source counts.
    expect(isPhoneSpec('via-helper.spec.ts', `import { openPhone } from './helpers/open';`)).toBe(false);
    expect(phoneSpecs(dir)).toEqual(['via-dynamic-helper.spec.ts', 'via-helper.spec.ts', 'via-two.spec.ts']);
    // All three take test from @playwright/test, so the config check refuses each of them.
    const problems = checkPhoneSpecs(dir, specMatcher(phoneSpecs(dir)));
    expect(problems.map((p) => p.split(' ')[0])).toEqual(['via-dynamic-helper.spec.ts', 'via-helper.spec.ts', 'via-two.spec.ts']);
    for (const p of problems) expect(p).toMatch(/phone\/fixtures/);
  });

  it('checkPhoneSpecs against the old list reports a dynamic-import spec and a helper spec as outside the phone project', () => {
    const dir = tree({
      'zz-dynamic.spec.ts': DYNAMIC,
      'zz-helper.spec.ts': `import { expect, test } from './phone/fixtures';\nimport { openPhone } from './helpers/open';\n`,
      'helpers/open.ts': `export { fakeRemote as openPhone } from '../phone/server';\n`,
      'phone/server.ts': 'export const fakeRemote = 1;\n',
    });
    const outside = checkPhoneSpecs(dir, OLD_PHONE_SPECS).filter((p) => /not in the phone project/.test(p)).map((p) => p.split(' ')[0]);
    expect(outside).toEqual(['zz-dynamic.spec.ts', 'zz-helper.spec.ts']);
  });
});

describe('checkPhoneSpecs: the check playwright.config.ts runs at load', () => {
  it('a stray spec (imports phone/server, not on a hand-kept list, test from @playwright/test) fails it on both counts', () => {
    const dir = tree({ 'zz-stray.spec.ts': STRAY, 'card367-team-grid.spec.ts': GOOD, 'window.spec.ts': ELECTRON });
    const problems = checkPhoneSpecs(dir, OLD_PHONE_SPECS);
    expect(problems).toHaveLength(2);
    expect(problems[0]).toMatch(/^zz-stray\.spec\.ts .*not in the phone project.*no worker cap/);
    expect(problems[1]).toMatch(/^zz-stray\.spec\.ts .*does not take `test` from phone\/fixtures/);
  });

  it('with the matcher built from the imports the stray is in the phone project, and only the fixtures problem is left', () => {
    const dir = tree({ 'zz-stray.spec.ts': STRAY, 'window.spec.ts': ELECTRON });
    const m = specMatcher(phoneSpecs(dir));
    expect(m.test(path.join(dir, 'zz-stray.spec.ts'))).toBe(true);
    expect(m.test(path.join(dir, 'window.spec.ts'))).toBe(false);
    expect(checkPhoneSpecs(dir, m)).toEqual([expect.stringMatching(/^zz-stray\.spec\.ts .*phone\/fixtures/)]);
  });

  it('a phone spec that takes test from phone/fixtures (./fixtures inside phone/) passes', () => {
    const dir = tree({ 'a.spec.ts': GOOD, 'phone/b.spec.ts': `import { devices, test } from './fixtures';\nimport { builtDir } from './server';\n`, 'window.spec.ts': ELECTRON });
    expect(listSpecs(dir)).toEqual(['a.spec.ts', 'phone/b.spec.ts', 'window.spec.ts']);
    expect(checkPhoneSpecs(dir, specMatcher(phoneSpecs(dir)))).toEqual([]);
  });
});

describe('the real tests/e2e', () => {
  // The specs that imported phone/server but were not on the old list, so they ran uncapped in `default`.
  const STRAYS = ['card355', 'card356', 'card362', 'card363', 'card390', 'card394', 'card403', 'card420', 'card431', 'card448', 'card465', 'card468', 'card478'];

  it('the old hand-kept list missed these phone specs', () => {
    const missed = checkPhoneSpecs(E2E, OLD_PHONE_SPECS).filter((p) => /not in the phone project/.test(p)).map((p) => p.split('-')[0]);
    expect(missed).toEqual(STRAYS);
  });

  it('every phone spec is in the project built from the imports and uses phone/fixtures; the Electron specs stay in default', () => {
    const specs = phoneSpecs(E2E);
    for (const s of STRAYS) expect(specs.some((f) => f.startsWith(`${s}-`))).toBe(true);
    expect(specs).toContain('phone-remote.spec.ts');
    expect(specs).toContain('phone/rotate.spec.ts');
    for (const f of listSpecs(E2E).filter((f) => /^(first-run|window|windows-)/.test(f))) expect(specs).not.toContain(f);
    expect(checkPhoneSpecs(E2E, specMatcher(specs))).toEqual([]);
  });
});
