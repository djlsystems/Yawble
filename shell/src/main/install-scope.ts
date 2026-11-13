import { spawnSync } from 'node:child_process';
import path from 'node:path';

/**
 * How this copy of the app is installed, for its updates. "machine": for everyone on the computer (Program Files,
 * the installer's "Anyone who uses this computer", or an IT deployment with /allusers); updating it needs an
 * administrator. "user": for this person only (%LOCALAPPDATA%\Programs), updated without any prompt.
 */
export type InstallScope = 'machine' | 'user';

export function installScope(exePath: string, env: NodeJS.ProcessEnv): InstallScope {
  const exe = path.win32.resolve(exePath).toLowerCase();
  for (const key of ['ProgramW6432', 'ProgramFiles', 'ProgramFiles(x86)']) {
    const dir = env[key];
    if (dir && exe.startsWith(path.win32.resolve(dir).toLowerCase() + '\\')) return 'machine';
  }
  return 'user';
}

/**
 * The installer's arguments for a silent update in place: the same scope as installed (an all-users install asks
 * Windows for administrator permission itself), and the app started again afterwards.
 */
export function updaterArgs(scope: InstallScope): string[] {
  return ['--updated', '/S', '--force-run', scope === 'machine' ? '/allusers' : '/currentuser'];
}

/** Whether this person is an administrator of the computer (in the Administrators group, elevated or not). */
export function isAdministrator(run: (cmd: string, args: string[]) => string = runText): boolean {
  return /S-1-5-32-544/.test(run('whoami', ['/groups', '/fo', 'csv', '/nh']));
}

/**
 * How many other people signed in to this computer have the app open: copies of the program in other Windows
 * sessions. An all-users update replaces files they are running, so they are closed by it.
 */
export function otherSessionsRunning(exeName: string, ownPid: number, run: (cmd: string, args: string[]) => string = runText): number {
  const rows = run('tasklist', ['/fo', 'csv', '/nh', '/fi', `imagename eq ${exeName}`])
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.startsWith('"'))
    .map((l) => l.slice(1, -1).split('","'));
  const own = rows.find((r) => Number(r[1]) === ownPid)?.[3];
  const sessions = new Set(rows.filter((r) => r[3] !== undefined && r[3] !== own).map((r) => r[3]));
  return own === undefined ? 0 : sessions.size;
}

function runText(cmd: string, args: string[]): string {
  const r = spawnSync(cmd, args, { encoding: 'utf8', windowsHide: true });
  return r.status === 0 ? r.stdout : '';
}
