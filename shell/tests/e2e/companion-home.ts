import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const made: string[] = [];

/** Removes every companion home this worker made (the core removes control.json on exit; the folder is ours). */
export function removeCompanionHomes(): void {
  for (const dir of made.splice(0)) {
    try { rmSync(dir, { recursive: true, force: true }); } catch { /* still in use: the temp folder keeps it */ }
  }
}

// Each launch's home is removed when its test ends (see below) and, as a backstop, when the worker exits.
process.once('exit', removeCompanionHomes);

/**
 * Every e2e run starts a real core, which opens the yawble companion's control endpoint and writes control.json into its
 * home. A fresh, empty temp folder as YAWBLE_HOME keeps it away from the person's real %APPDATA%\yawble-companion (and
 * never near %APPDATA%\yawble). Call removeCompanionHomes() after the app has closed.
 */
export function companionHome(): { YAWBLE_HOME: string } {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'yawble-e2e-yhome-'));
  made.push(dir);
  return { YAWBLE_HOME: dir };
}
