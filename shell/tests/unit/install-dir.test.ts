import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterAll, describe, expect, it } from 'vitest';
import { locateInstallDir } from '../../src/main/core-process';

// The install folder the shell passes to the core (--install-dir): a dev app started at sign-in has no
// YAWBLE_INSTALL_DIR (run-dev.cmd is not in the way), so it falls back to <repo>/.devinstall/resources.
const root = mkdtempSync(path.join(os.tmpdir(), 'yawble-installdir-'));
afterAll(() => rmSync(root, { recursive: true, force: true }));
const bridge = process.platform === 'win32' ? 'yawble-bridge.exe' : 'yawble-bridge';

function repoWithDevInstall(): string {
  const repo = mkdtempSync(path.join(root, 'repo-'));
  mkdirSync(path.join(repo, '.devinstall', 'resources', 'bin'), { recursive: true });
  writeFileSync(path.join(repo, '.devinstall', 'resources', 'bin', bridge), '');
  return repo;
}

describe('locateInstallDir', () => {
  it('takes YAWBLE_INSTALL_DIR first', () => {
    expect(locateInstallDir(repoWithDevInstall(), '/res', { YAWBLE_INSTALL_DIR: '/env' })).toBe('/env');
  });

  it('takes the packaged resources folder next', () => {
    expect(locateInstallDir(repoWithDevInstall(), '/res', {})).toBe('/res');
  });

  it('falls back to <repo>/.devinstall/resources in development when it holds bin/yawble-bridge', () => {
    const repo = repoWithDevInstall();
    expect(locateInstallDir(repo, undefined, {})).toBe(path.join(repo, '.devinstall', 'resources'));
  });

  it('gives none in development without a dev install', () => {
    const repo = mkdtempSync(path.join(root, 'bare-'));
    mkdirSync(path.join(repo, '.devinstall', 'resources', 'bin'), { recursive: true });
    expect(locateInstallDir(repo, undefined, {})).toBeUndefined();
  });
});
