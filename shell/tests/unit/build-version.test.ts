import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
// @ts-ignore: a plain .mjs in the repository's scripts, without types
import { buildVersion, csharpSource, isBuildVersion, readBuildVersion, UNKNOWN } from '../../../scripts/build-version.mjs';

// scripts/build-version.mjs, the one source of the build's version (core and shell). Pure cases: git's answers are
// passed in, the same cases scripts/tests/release-functions.tests.ps1 checks Get-BuildVersion with. No network.
interface Case { name: string; describe: string; sha: string; dirty: boolean; version: string; released: boolean }
const cases: Case[] = JSON.parse(readFileSync(path.join(__dirname, '../../../scripts/tests/build-version-cases.json'), 'utf8'));

describe('build version (scripts/build-version.mjs)', () => {
  it.each(cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    const v = buildVersion({ describe: c.describe, sha: c.sha, dirty: c.dirty });
    expect(v.version).toBe(c.version);
    expect(v.released).toBe(c.released);
  });

  it('the environment wins (a container build with no .git)', () => {
    expect(readBuildVersion('/nowhere', { YAWBLE_BUILD_VERSION: '2026.11.12.1', YAWBLE_BUILD_COMMIT: 'abc' }))
      .toEqual({ version: '2026.11.12.1', commit: 'abc', dirty: false, released: true });
    expect(readBuildVersion('/nowhere', { YAWBLE_BUILD_VERSION: '0.0.0+dev.ee55691' }))
      .toEqual({ version: '0.0.0+dev.ee55691', commit: 'unknown', dirty: false, released: false });
  });

  it('an environment version of another shape, or over 40 characters, is ignored with a warning', () => {
    const warn = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      for (const bad of ['1.2.3', 'v2026.11.12.1', '2026.11.12.1-rc', '2026.11.12.1+dev.XYZ', `2026.11.12.1+dev.ee55691.dirty${'x'.repeat(20)}`, '9999.99.99.999999999999999999999999999999']) {
        warn.mockClear();
        expect(readBuildVersion('/nowhere-yawble-build-version', { YAWBLE_BUILD_VERSION: bad })).toEqual(UNKNOWN);
        expect(warn).toHaveBeenCalledTimes(1);
        expect(String(warn.mock.calls[0][0])).toContain('ignoring YAWBLE_BUILD_VERSION');
      }
      expect(isBuildVersion('2026.11.12.1+dev.ee55691.dirty')).toBe(true);
      expect(isBuildVersion('0.0.0+dev.unknown')).toBe(true);
    } finally {
      warn.mockRestore();
    }
  });

  it('a folder that does not exist (no git there): 0.0.0+dev.unknown, never a failure', () => {
    const v = readBuildVersion(path.join(path.parse(process.cwd()).root, 'yawble-build-version-no-such-dir'), {});
    expect(v).toEqual(UNKNOWN);
  });

  it('writes the core attributes CoreInfo reads, quoted', () => {
    const cs = csharpSource({ version: '2026.11.12.1+dev.ee55691', commit: 'ee55691', dirty: true, released: false });
    expect(cs).toContain('[assembly: System.Reflection.AssemblyMetadata("YawbleVersion", "2026.11.12.1+dev.ee55691")]');
    expect(cs).toContain('AssemblyMetadata("YawbleDirty", "true")');
    expect(cs).toContain('AssemblyMetadata("YawbleReleased", "false")');
  });
});
