/**
 * The modes for what the shell writes in the data folder: folders 0700 and files 0600, as the core's Secure leaves
 * them, so a folder or file the shell creates before the core starts (or later, a new day's log) is private from the
 * start. A mode only applies when the folder or file is created; Node ignores it on Windows, where the data folder's
 * access list does this.
 */
export const PRIVATE_DIR = { recursive: true, mode: 0o700 } as const;
export const PRIVATE_FILE = { mode: 0o600 } as const;
