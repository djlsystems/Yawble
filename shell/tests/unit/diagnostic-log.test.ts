import { EventEmitter } from 'node:events';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { captureMainOutput, diagnosticLog, processGoneLine, shellLogFile, shellStartLine } from '../../src/main/diagnostic-log';

// Temp folders and a fake console and process only: nothing here touches the app's own data or this process's handlers.
let dir = '';
beforeEach(() => { dir = fs.mkdtempSync(path.join(os.tmpdir(), 'shell-log-')); });
afterEach(() => { fs.rmSync(dir, { recursive: true, force: true }); vi.restoreAllMocks(); });

const read = (day: string) => fs.readFileSync(path.join(dir, 'logs', `shell-${day}.log`), 'utf8');

function fakeTarget() {
  const seen: string[] = [];
  const proc = new EventEmitter();
  const target = {
    console: { error: (...a: unknown[]) => { seen.push('E ' + a.join(' ')); }, warn: (...a: unknown[]) => { seen.push('W ' + a.join(' ')); } },
    process: proc as unknown as NodeJS.Process,
  };
  return { seen, proc, target };
}

describe('the shell log file', () => {
  it('IsDataLogsShellDay_TheSameOnEveryOs_TheUtcDay', () => {
    for (const platform of ['win32', 'darwin', 'linux']) {
      // the path depends on the data folder only, never on the platform
      expect(shellLogFile(dir, new Date('2026-11-14T23:59:59.999Z')), platform).toBe(path.join(dir, 'logs', 'shell-2026-11-14.log'));
    }
    expect(shellLogFile(dir, new Date('2026-11-15T00:00:00.000Z'))).toBe(path.join(dir, 'logs', 'shell-2026-11-15.log'));
  });

  it('StartsANewFileEachDay_SoTheCoresRetentionDropsWholeDays', () => {
    let at = new Date('2026-11-14T23:59:00Z');
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const log = diagnosticLog(dir, () => at);
    log('late');
    at = new Date('2026-11-15T00:01:00Z');
    log('early');
    expect(read('2026-11-14')).toBe('2026-11-14T23:59:00.000Z\tshell\tlate\n');
    expect(read('2026-11-15')).toBe('2026-11-15T00:01:00.000Z\tshell\tearly\n');
    expect(log.path()).toBe(path.join(dir, 'logs', 'shell-2026-11-15.log'));
  });

  it('CreatesTheLogsFolder_AndATabInTheTextNeverAddsAColumn', () => {
    const log = diagnosticLog(path.join(dir, 'fresh'), () => new Date('2026-11-14T10:00:00Z'));
    log.file('a\tb');
    expect(fs.readFileSync(path.join(dir, 'fresh', 'logs', 'shell-2026-11-14.log'), 'utf8')).toBe('2026-11-14T10:00:00.000Z\tshell\ta b\n');
  });

  it('AWriteThatFails_IsSkipped_NeverThrown', () => {
    fs.writeFileSync(path.join(dir, 'logs'), 'a file where the folder should be');
    const log = diagnosticLog(dir);
    expect(() => log.file('nowhere to go')).not.toThrow();
  });
});

describe('captureMainOutput', () => {
  it('CopiesConsoleErrorsAndWarnings_IntoTheFile_AndStillPrintsThem', () => {
    const { seen, target } = fakeTarget();
    const log = diagnosticLog(dir, () => new Date('2026-11-14T10:00:00Z'));
    const undo = captureMainOutput(log, target);
    target.console.error('could not %s', 'connect', 42);
    target.console.warn('slow start');
    expect(seen).toEqual(['E could not %s connect 42', 'W slow start']);
    expect(read('2026-11-14')).toBe('2026-11-14T10:00:00.000Z\tshell\tcould not connect 42\n2026-11-14T10:00:00.000Z\tshell\twarning: slow start\n');
    undo();
  });

  it('AReportGoesToTheFileOnce_NotAgainThroughTheCapturedConsole', () => {
    const { target } = fakeTarget();
    const real = vi.spyOn(console, 'error').mockImplementation((...a: unknown[]) => target.console.error(...a));
    const log = diagnosticLog(dir, () => new Date('2026-11-14T10:00:00Z'));
    const undo = captureMainOutput(log, target);
    log('the core exited (1)');
    expect(real).toHaveBeenCalledWith('[shell] the core exited (1)');
    expect(read('2026-11-14')).toBe('2026-11-14T10:00:00.000Z\tshell\tthe core exited (1)\n');
    undo();
  });

  it('RecordsUncaughtErrorsAndRejections_ThroughTheMonitorOnly', () => {
    const { proc, target } = fakeTarget();
    const log = diagnosticLog(dir, () => new Date('2026-11-14T10:00:00Z'));
    const undo = captureMainOutput(log, target);
    // only the monitor: an 'uncaughtException' or 'unhandledRejection' listener would change how they are handled
    expect(proc.listenerCount('uncaughtExceptionMonitor')).toBe(1);
    expect(proc.listenerCount('uncaughtException')).toBe(0);
    expect(proc.listenerCount('unhandledRejection')).toBe(0);
    const e = new Error('boom');
    e.stack = 'Error: boom\n    at main.js:1';
    proc.emit('uncaughtExceptionMonitor', e, 'uncaughtException');
    proc.emit('uncaughtExceptionMonitor', 'nope', 'unhandledRejection');
    expect(read('2026-11-14')).toBe(
      '2026-11-14T10:00:00.000Z\tshell\tuncaught error: Error: boom\n2026-11-14T10:00:00.000Z\tshell\t    at main.js:1\n' +
      '2026-11-14T10:00:00.000Z\tshell\tunhandled rejection: nope\n');
    undo();
    expect(proc.listenerCount('uncaughtExceptionMonitor')).toBe(0);
  });

  it('TheUndo_PutsTheConsoleBack', () => {
    const { target } = fakeTarget();
    const { error, warn } = target.console;
    const undo = captureMainOutput(diagnosticLog(dir), target);
    expect(target.console.error).not.toBe(error);
    undo();
    expect(target.console.error).toBe(error);
    expect(target.console.warn).toBe(warn);
  });
});

describe('the lines the shell writes', () => {
  it('StartLine_NamesTheAppTheOsAndTheDataFolder', () => {
    expect(shellStartLine({ version: '1.4.0', electron: '39.0.0', platform: 'darwin', arch: 'arm64', packaged: true, dataDir: '/Users/a/Library/Application Support/Yawble/data' }))
      .toBe('shell starting: Yawble 1.4.0 on darwin arm64, Electron 39.0.0, data /Users/a/Library/Application Support/Yawble/data');
    expect(shellStartLine({ version: '1.4.0', platform: 'linux', arch: 'x64', packaged: false, dataDir: '/d' })).toContain('(development)');
  });

  it('AProcessThatStopped_IsALine_ACleanExitIsNot', () => {
    expect(processGoneLine("a window's page", { reason: 'crashed', exitCode: 139 })).toBe("a window's page stopped: crashed (exit code 139)");
    expect(processGoneLine('the GPU process', { reason: 'clean-exit', exitCode: 0 })).toBeNull();
  });
});
