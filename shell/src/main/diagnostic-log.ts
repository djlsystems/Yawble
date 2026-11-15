import fs from 'node:fs';
import path from 'node:path';
import { format } from 'node:util';
import { PRIVATE_DIR, PRIVATE_FILE } from './private-files';

/**
 * The window process's diagnostic log, the same on every OS: <data>/logs/shell-<yyyy-mm-dd>.log (the UTC day), one
 * "time<TAB>shell<TAB>text" line per message, the format of the core's own diagnostic log beside it. The core lists
 * every .log in that folder in the Logs dialog's Diagnostics and keeps it to the same retention (a day past it goes
 * whole). Launched from Finder or `open` on macOS (or a desktop entry on Linux) the process's own output goes nowhere,
 * so this file is the only record: {@link captureMainOutput} copies console errors and warnings and uncaught errors
 * into it too. A write that fails is skipped; it never throws.
 */
export interface ShellLog {
  /** Writes a message to standard error and to the file. */
  (text: string): void;
  /** Writes a message to the file only (output that already went to standard error). */
  file(text: string): void;
  /** The day's file. */
  path(at?: Date): string;
}

export function shellLogFile(dataDir: string, at: Date): string {
  return path.join(dataDir, 'logs', `shell-${at.toISOString().slice(0, 10)}.log`);
}

export function diagnosticLog(dataDir: string, now: () => Date = () => new Date()): ShellLog {
  let reporting = false; // our own console.error below, seen by a captured console: written once, not twice
  const file = (text: string) => {
    const at = now();
    const lines = text.split(/\r?\n/).filter((l) => l.trim()).map((l) => `${at.toISOString()}\tshell\t${l.replace(/\t/g, ' ')}\n`).join('');
    if (!lines) return;
    try {
      const target = shellLogFile(dataDir, at);
      fs.mkdirSync(path.dirname(target), PRIVATE_DIR);
      fs.appendFileSync(target, lines, PRIVATE_FILE);
    } catch { /* the diagnostic log is best effort */ }
  };
  const log = ((text: string) => {
    reporting = true;
    try { console.error(`[shell] ${text}`); } finally { reporting = false; }
    file(text);
  }) as ShellLog;
  log.file = (text: string) => { if (!reporting) file(text); };
  log.path = (at?: Date) => shellLogFile(dataDir, at ?? now());
  return log;
}

/** What {@link captureMainOutput} hooks: the process's console and its error events (fakes in tests). */
export interface Capturable {
  console: Pick<Console, 'error' | 'warn'>;
  process: Pick<NodeJS.Process, 'on' | 'off'>;
}

/**
 * Copies the main process's console.error / console.warn and its uncaught errors into the shell log (they still go
 * where they went: standard error, and Electron's own handling of an uncaught error is unchanged: the monitor event
 * only watches). Returns the undo.
 */
export function captureMainOutput(log: ShellLog, target: Capturable = { console, process }): () => void {
  const { error, warn } = target.console;
  target.console.error = (...args: unknown[]) => { error.apply(target.console, args); log.file(format(...args)); };
  target.console.warn = (...args: unknown[]) => { warn.apply(target.console, args); log.file('warning: ' + format(...args)); };
  // The monitor sees an unhandled rejection too (origin "unhandledRejection") when it becomes an uncaught error, and,
  // unlike an 'unhandledRejection' or 'uncaughtException' listener, changes nothing about how it is handled.
  const uncaught = (e: unknown, origin?: string) => log.file(`${origin === 'unhandledRejection' ? 'unhandled rejection' : 'uncaught error'}: ${describe(e)}`);
  target.process.on('uncaughtExceptionMonitor', uncaught);
  return () => {
    target.console.error = error;
    target.console.warn = warn;
    target.process.off('uncaughtExceptionMonitor', uncaught);
  };
}

/** The shell log's first line of a launch: which app, on what, with which data folder. */
export function shellStartLine(i: { version: string; electron?: string; platform: string; arch: string; packaged: boolean; dataDir: string }): string {
  return `shell starting: Yawble ${i.version}${i.packaged ? '' : ' (development)'} on ${i.platform} ${i.arch}, Electron ${i.electron ?? '?'}, data ${i.dataDir}`;
}

/** A line for a page or helper process that stopped (null for one that simply ended, a clean exit). */
export function processGoneLine(what: string, details: { reason: string; exitCode: number }): string | null {
  return details.reason === 'clean-exit' ? null : `${what} stopped: ${details.reason} (exit code ${details.exitCode})`;
}

function describe(e: unknown): string {
  return e instanceof Error ? (e.stack ?? `${e.name}: ${e.message}`) : String(e);
}
