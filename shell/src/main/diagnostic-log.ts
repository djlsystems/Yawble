import fs from 'node:fs';
import path from 'node:path';

/**
 * The window process's diagnostic messages: each goes to standard error and is appended to
 * <data>/logs/shell-<yyyy-mm-dd>.log as "time<TAB>shell<TAB>text", the format of the core's own diagnostic log
 * (the Logs dialog's Diagnostics). A write that fails is skipped; it never throws.
 */
export function diagnosticLog(dataDir: string, now: () => Date = () => new Date()): (text: string) => void {
  const folder = path.join(dataDir, 'logs');
  return (text: string) => {
    console.error(`[shell] ${text}`);
    const at = now();
    const lines = text.split(/\r?\n/).filter((l) => l.trim()).map((l) => `${at.toISOString()}\tshell\t${l.replace(/\t/g, ' ')}\n`).join('');
    if (!lines) return;
    try {
      fs.mkdirSync(folder, { recursive: true });
      fs.appendFileSync(path.join(folder, `shell-${at.toISOString().slice(0, 10)}.log`), lines);
    } catch { /* the diagnostic log is best effort */ }
  };
}
