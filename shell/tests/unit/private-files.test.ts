// B031 (card 147, finding 8a): what the shell writes in the data folder (its log, shell-settings.json,
// window-state.json, theme.json) is created private: folders 0700, files 0600, so it does not wait for the core's
// Secure at the next start. node:fs is mocked (the real calls still run, in a temp folder), so the modes passed are
// checked on Windows too, where Node ignores them.
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('node:fs', async (importOriginal) => {
  const real = await importOriginal<typeof import('node:fs')>();
  const spied = {
    ...real,
    mkdirSync: vi.fn(real.mkdirSync),
    appendFileSync: vi.fn(real.appendFileSync),
    writeFileSync: vi.fn(real.writeFileSync),
  };
  return { ...spied, default: spied };
});
vi.mock('electron', () => ({ app: {}, Menu: {}, nativeImage: {}, Tray: class {} }));

const fs = await import('node:fs');
const { diagnosticLog } = await import('../../src/main/diagnostic-log');
const { ShellSettingsFile } = await import('../../src/main/background');
const { WindowStateFile } = await import('../../src/main/window-state');
const { ThemeFile } = await import('../../src/main/title-bar');

const mkdir = vi.mocked(fs.mkdirSync);
const append = vi.mocked(fs.appendFileSync);
const write = vi.mocked(fs.writeFileSync);

let dir = '';
beforeEach(() => { dir = fs.mkdtempSync(path.join(os.tmpdir(), 'shell-modes-')); vi.clearAllMocks(); });
afterEach(() => { fs.rmSync(dir, { recursive: true, force: true }); vi.restoreAllMocks(); });

const privateDir = expect.objectContaining({ recursive: true, mode: 0o700 });
const privateFile = expect.objectContaining({ mode: 0o600 });

describe('the shell creates its data private (0700 folders, 0600 files)', () => {
  it('TheShellLog_ItsFolder0700_ItsFile0600_AlsoTheNewFileOfANewUtcDay', () => {
    let at = new Date('2026-11-14T23:59:00Z');
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const log = diagnosticLog(path.join(dir, 'data'), () => at);
    log('shell starting');
    at = new Date('2026-11-15T00:01:00Z');
    log.file('next day');
    expect(mkdir.mock.calls).toEqual([
      [path.join(dir, 'data', 'logs'), privateDir],
      [path.join(dir, 'data', 'logs'), privateDir],
    ]);
    expect(append.mock.calls).toEqual([
      [path.join(dir, 'data', 'logs', 'shell-2026-11-14.log'), expect.any(String), privateFile],
      [path.join(dir, 'data', 'logs', 'shell-2026-11-15.log'), expect.any(String), privateFile],
    ]);
  });

  it('ShellSettingsJson_0600_InA0700Folder', () => {
    const file = path.join(dir, 'data', 'shell-settings.json');
    new ShellSettingsFile(file).write({ runInBackground: true, startAtLogin: false });
    expect(mkdir).toHaveBeenCalledWith(path.dirname(file), privateDir);
    expect(write).toHaveBeenCalledWith(file, expect.any(String), privateFile);
  });

  it('WindowStateJson_0600_InA0700Folder', () => {
    const file = path.join(dir, 'data', 'window-state.json');
    const area = { x: 0, y: 0, width: 1920, height: 1080 };
    new WindowStateFile(file).write({ bounds: area, maximized: false, displayId: 1, displayWorkArea: area });
    expect(mkdir).toHaveBeenCalledWith(path.dirname(file), privateDir);
    expect(write).toHaveBeenCalledWith(file, expect.any(String), privateFile);
  });

  it('ThemeJson_0600_InA0700Folder', () => {
    const file = path.join(dir, 'data', 'theme.json');
    new ThemeFile(file).write('dark');
    expect(mkdir).toHaveBeenCalledWith(path.dirname(file), privateDir);
    expect(write).toHaveBeenCalledWith(file, expect.any(String), privateFile);
  });

  it.skipIf(process.platform === 'win32')('OnPosix_TheFilesOnDiskAre0600_AndTheFolder0700', () => {
    const data = path.join(dir, 'data');
    vi.spyOn(console, 'error').mockImplementation(() => {});
    diagnosticLog(data, () => new Date('2026-11-14T10:00:00Z'))('shell starting');
    new ShellSettingsFile(path.join(data, 'shell-settings.json')).write({ runInBackground: true, startAtLogin: false });
    const mode = (p: string) => fs.statSync(p).mode & 0o777;
    expect(mode(path.join(data, 'logs'))).toBe(0o700);
    expect(mode(path.join(data, 'logs', 'shell-2026-11-14.log'))).toBe(0o600);
    expect(mode(path.join(data, 'shell-settings.json'))).toBe(0o600);
  });
});