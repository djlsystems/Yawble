import path from 'node:path';

/**
 * The app's own folder (Electron's userData): the data folder, website sign-in sessions and window state live under
 * it. Windows keeps it in local, not roaming, app data because it holds databases and repository clones.
 */
export function appFolder(platform: NodeJS.Platform, env: NodeJS.ProcessEnv, home: string): string {
  if (platform === 'win32') {
    return path.win32.join(env.LOCALAPPDATA || path.win32.join(home, 'AppData', 'Local'), 'Yawble');
  }
  if (platform === 'darwin') return path.posix.join(home, 'Library', 'Application Support', 'Yawble');
  return path.posix.join(env.XDG_DATA_HOME || path.posix.join(home, '.local', 'share'), 'yawble');
}
