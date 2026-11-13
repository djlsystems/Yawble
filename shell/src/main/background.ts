import { app, Menu, nativeImage, Tray } from 'electron';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

/**
 * Running in the background: the app stays in the system tray when its window is closed, so the teams' work, schedules
 * and the Cloud Link carry on; it can start with the person's sign-in to the computer, in the tray with no window.
 * The core is the app's child either way: there is one app process, and the window attaches to it when opened.
 */
export interface ShellSettings {
  /** Closing the window leaves the app running in the tray (default on). */
  runInBackground: boolean;
  /** Start the app, in the tray, when the person signs in to this computer (default off). */
  startAtLogin: boolean;
}

export const DEFAULT_SETTINGS: ShellSettings = { runInBackground: true, startAtLogin: false };

/** The flag a sign-in start passes: start in the tray, without a window. */
export const BACKGROUND_ARG = '--background';

export class ShellSettingsFile {
  constructor(private readonly file: string) {}

  read(): ShellSettings {
    try {
      const raw = JSON.parse(readFileSync(this.file, 'utf8')) as Partial<ShellSettings>;
      return {
        runInBackground: typeof raw.runInBackground === 'boolean' ? raw.runInBackground : DEFAULT_SETTINGS.runInBackground,
        startAtLogin: typeof raw.startAtLogin === 'boolean' ? raw.startAtLogin : DEFAULT_SETTINGS.startAtLogin,
      };
    } catch {
      return { ...DEFAULT_SETTINGS };
    }
  }

  write(settings: ShellSettings): void {
    mkdirSync(path.dirname(this.file), { recursive: true });
    writeFileSync(this.file, JSON.stringify(settings, null, 2));
  }
}

/** Whether this launch should start in the tray only (a sign-in start). */
export function startedInBackground(argv: readonly string[]): boolean {
  if (argv.includes(BACKGROUND_ARG)) return true;
  // macOS cannot pass arguments to a login item; it says whether the app was opened at login instead.
  return process.platform === 'darwin' && app.getLoginItemSettings().wasOpenedAtLogin === true;
}

/** The command a sign-in start runs: the app (in development, Electron with the app folder) and the flag. */
function loginCommand(): { exe: string; args: string[] } {
  const exe = process.env.APPIMAGE ?? process.execPath;
  return { exe, args: app.isPackaged ? [BACKGROUND_ARG] : [app.getAppPath(), BACKGROUND_ARG] };
}

const linuxAutostartFile = () => path.join(process.env.XDG_CONFIG_HOME ?? path.join(os.homedir(), '.config'), 'autostart', 'yawble.desktop');

/** Registers or removes the start at sign-in with the system: a login item (Windows, macOS) or an autostart entry (Linux). */
export function applyStartAtLogin(on: boolean): void {
  const { exe, args } = loginCommand();
  if (process.platform === 'linux') {
    const file = linuxAutostartFile();
    if (!on) {
      rmSync(file, { force: true });
      return;
    }
    mkdirSync(path.dirname(file), { recursive: true });
    const quote = (s: string) => (/[\s"'\\]/.test(s) ? `"${s.replace(/(["\\$`])/g, '\\$1')}"` : s);
    writeFileSync(file, [
      '[Desktop Entry]', 'Type=Application', 'Name=Yawble', 'Comment=Yawble in the background',
      `Exec=${[exe, ...args].map(quote).join(' ')}`, 'X-GNOME-Autostart-enabled=true', 'NoDisplay=true', '',
    ].join('\n'));
    return;
  }
  app.setLoginItemSettings({ openAtLogin: on, path: exe, args });
}

/** What the system has registered (the setting can drift if the person removed the entry in the system's own settings). */
export function startAtLoginRegistered(): boolean {
  if (process.platform === 'linux') return existsSync(linuxAutostartFile());
  const { exe, args } = loginCommand();
  return app.getLoginItemSettings({ path: exe, args }).openAtLogin;
}

/** What the tray says about the work: the overview's run slots and the questions waiting. */
export interface TrayStatus { running: number; queued: number; needsYou: number }

export function statusLine(s: TrayStatus | null): string {
  if (!s) return 'Starting…';
  const parts = [s.running === 0 ? 'No agents running' : `${s.running} agent${s.running === 1 ? '' : 's'} running`];
  if (s.queued) parts.push(`${s.queued} waiting`);
  if (s.needsYou) parts.push(`${s.needsYou} need${s.needsYou === 1 ? 's' : ''} you`);
  return parts.join(' · ');
}

export interface TrayActions {
  open(): void;
  quit(): void;
  settings(): ShellSettings;
  change(patch: Partial<ShellSettings>): void;
}

/** The tray icon and its menu: open the window, what is running, the two background settings, quit. */
export class AppTray {
  private readonly tray: Tray;
  private status: TrayStatus | null = null;

  constructor(iconFile: string, private readonly actions: TrayActions) {
    const size = process.platform === 'darwin' ? 18 : 16;
    const image = nativeImage.createFromPath(iconFile);
    // tray.png is drawn at 16px (with an @2x beside it); resizing would drop the high-DPI version.
    const icon = image.getSize().width === size ? image : image.resize({ width: size, height: size });
    this.tray = new Tray(icon);
    this.tray.on('click', () => actions.open()); // Windows and Linux; on macOS a click opens the menu
    this.refresh();
  }

  update(status: TrayStatus | null): void {
    this.status = status;
    this.refresh();
  }

  refresh(): void {
    const s = this.actions.settings();
    const line = statusLine(this.status);
    this.tray.setToolTip(`Yawble · ${line}`);
    this.tray.setContextMenu(Menu.buildFromTemplate([
      { label: 'Open Yawble', click: () => this.actions.open() },
      { label: line, enabled: false },
      { type: 'separator' },
      { label: 'Keep running when the window is closed', type: 'checkbox', checked: s.runInBackground, click: (i) => this.actions.change({ runInBackground: i.checked }) },
      { label: 'Start when I sign in', type: 'checkbox', checked: s.startAtLogin, click: (i) => this.actions.change({ startAtLogin: i.checked }) },
      { type: 'separator' },
      { label: 'Quit Yawble', click: () => this.actions.quit() },
    ]));
  }

  destroy(): void {
    this.tray.destroy();
  }
}
