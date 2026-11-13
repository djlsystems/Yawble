import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

// background.ts uses Electron's tray and login-item APIs; the parts tested here do not.
vi.mock('electron', () => ({ app: {}, Menu: {}, nativeImage: {}, Tray: class {} }));
const { DEFAULT_SETTINGS, ShellSettingsFile, statusLine, startedInBackground } = await import('../../src/main/background');

describe('running in the background', () => {
  const dirs: string[] = [];
  afterEach(() => { for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true }); });
  const tempFile = () => {
    const dir = mkdtempSync(path.join(os.tmpdir(), 'yawble-shell-'));
    dirs.push(dir);
    return path.join(dir, 'data', 'shell-settings.json');
  };

  it('keeps running by default and does not start at sign-in until asked', () => {
    expect(DEFAULT_SETTINGS).toEqual({ runInBackground: true, startAtLogin: false });
    expect(new ShellSettingsFile(tempFile()).read()).toEqual(DEFAULT_SETTINGS);
  });

  it('saves the settings, and a damaged or partial file falls back per setting', () => {
    const file = tempFile();
    const settings = new ShellSettingsFile(file);
    settings.write({ runInBackground: false, startAtLogin: true });
    expect(settings.read()).toEqual({ runInBackground: false, startAtLogin: true });
    expect(JSON.parse(readFileSync(file, 'utf8'))).toEqual({ runInBackground: false, startAtLogin: true });
    writeFileSync(file, '{"startAtLogin": true, "runInBackground": "yes"}');
    expect(settings.read()).toEqual({ runInBackground: true, startAtLogin: true });
    writeFileSync(file, 'not json');
    expect(settings.read()).toEqual(DEFAULT_SETTINGS);
  });

  it('says what is going on in a few words', () => {
    expect(statusLine(null)).toBe('Starting…');
    expect(statusLine({ running: 0, queued: 0, needsYou: 0 })).toBe('No agents running');
    expect(statusLine({ running: 1, queued: 0, needsYou: 1 })).toBe('1 agent running · 1 needs you');
    expect(statusLine({ running: 3, queued: 2, needsYou: 2 })).toBe('3 agents running · 2 waiting · 2 need you');
  });

  it('starts in the tray only when started with the background flag', () => {
    if (process.platform === 'darwin') return; // macOS asks the system instead
    expect(startedInBackground(['yawble.exe', '--background'])).toBe(true);
    expect(startedInBackground(['yawble.exe'])).toBe(false);
  });
});
