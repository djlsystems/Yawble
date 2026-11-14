import { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme, Notification, screen, session, shell } from 'electron';
import { placeWindow, WindowStateFile, type Display, type Rect } from './window-state';
import { spawn, spawnSync } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import { copyFileSync } from 'node:fs';
import { linuxUpdatePlan } from './linux-update';
import { diagnosticLog } from './diagnostic-log';
import { installScope, isAdministrator, otherSessionsRunning, updaterArgs } from './install-scope';
import { appFolder } from './app-folder';
import { Channels, isUiMethod, type Overview } from '../shared/api';
import { AppTray, applyStartAtLogin, ShellSettingsFile, startAtLoginRegistered, startedInBackground, type ShellSettings } from './background';
import { CoreClient } from './core-client';
import { locateCore, startCore, type RunningCore } from './core-process';
import { WebsiteBridge, type PageWindow, type WindowFactory } from './website-host';
import { TitleBars, TITLE_BAR } from './title-bar';

// Electron main: thin. It starts the core, relays the renderer's calls over IPC, forwards the
// core's pushes, and draws windows (the main window and one terminal window per agent). It never opens a
// network port and never reads the database.
const repoRoot = path.resolve(__dirname, '..', '..', '..', '..');
let core: RunningCore | null = null;
let client: CoreClient | null = null;
let mainWindow: BrowserWindow | null = null;
const terminalWindows = new Map<number, BrowserWindow>();
app.setPath('userData', appFolder(process.platform, process.env, os.homedir()));
const dataDir = process.env.YAWBLE_DATA_DIR ?? path.join(app.getPath('userData'), 'data');
const settingsFile = new ShellSettingsFile(path.join(dataDir, 'shell-settings.json'));
const windowStateFile = new WindowStateFile(path.join(dataDir, 'window-state.json'));
const report = diagnosticLog(dataDir);
let settings: ShellSettings = settingsFile.read();
let tray: AppTray | null = null;
let quitting = false;
let toldAboutTray = false;
let running = 0;
let needsYou: number | null = null; // the last count seen (null: not yet), for the "needs you" notification

async function connectCore(): Promise<CoreClient> {
  const launch = locateCore(repoRoot, app.isPackaged ? process.resourcesPath : undefined);
  // The app's install folder (bin/ with yawble-bridge, yawble-grok-auth, yawble-guardcheck; roles/). The agent CLIs are not in
  // it: the core runs the ones the person installed, found where they are at every launch.
  const installDir = process.env.YAWBLE_INSTALL_DIR ?? (app.isPackaged ? process.resourcesPath : undefined);
  core = await startCore(launch, { dataDir, installDir });
  core.child.once('exit', (code) => {
    report(`the core exited (${code})`);
    client = null;
    if (!quitting) void restartCore();
  });
  return CoreClient.connect(core.address, core.token);
}

// ---- Website login: pages the agents browse, and the sign-in window ----
const websiteWindows = new WeakSet<BrowserWindow>(); // hidden pages and sign-in windows: they never get the app's pushes
const preparedPartitions = new Set<string>();
/** Downloads cancelled and every permission denied, once per session. */
function prepareSession(partition: string): void {
  if (preparedPartitions.has(partition)) return;
  preparedPartitions.add(partition);
  const ses = session.fromPartition(partition);
  ses.on('will-download', (e) => e.preventDefault());
  ses.setPermissionRequestHandler((_wc, _perm, cb) => cb(false));
  ses.setPermissionCheckHandler(() => false);
}

const websiteWindow: WindowFactory = ({ partition, show, title }) => {
  prepareSession(partition);
  const win = new BrowserWindow({
    width: 1024,
    height: 768,
    show,
    title: title ?? 'Yawble',
    icon: appResource('icon.png'),
    webPreferences: { partition, sandbox: true, contextIsolation: true, nodeIntegration: false, webSecurity: true, disableDialogs: !show },
  });
  websiteWindows.add(win);
  if (title) win.on('page-title-updated', (e) => e.preventDefault()); // keep the instruction as the title
  const wc = win.webContents;
  wc.on('will-prevent-unload', (e) => e.preventDefault()); // a page cannot keep its window open
  const page: PageWindow = {
    loadURL: (url) => win.loadURL(url),
    executeJavaScript: (code) => wc.executeJavaScript(code),
    capturePage: () => wc.capturePage(),
    getURL: () => wc.getURL(),
    getTitle: () => wc.getTitle(),
    on(event, listener) {
      if (event === 'closed') win.on('closed', () => listener());
      else if (event === 'will-navigate') {
        wc.on('will-navigate', (e) => listener(e, (e as unknown as { url: string }).url, (e as unknown as { isMainFrame?: boolean }).isMainFrame));
      } else if (event === 'will-redirect') {
        wc.on('will-redirect', (e) => listener(e, (e as unknown as { url: string }).url, (e as unknown as { isMainFrame?: boolean }).isMainFrame));
      } else if (event === 'render-process-gone') wc.on('render-process-gone', () => listener());
      else if (event === 'did-start-loading') wc.on('did-start-loading', () => listener());
      else if (event === 'did-finish-load') wc.on('did-finish-load', () => listener());
      else if (event === 'did-fail-load') {
        wc.on('did-fail-load', (_e, code, desc, _url, isMainFrame) => { if (isMainFrame) listener(desc, code); });
      }
    },
    setWindowOpenHandler: (h) => wc.setWindowOpenHandler(({ url }) => h({ url })),
    focus: () => { if (win.isMinimized()) win.restore(); win.show(); win.focus(); },
    close: () => win.close(),
    destroy: () => win.destroy(),
    isDestroyed: () => win.isDestroyed(),
  };
  return page;
};

let websiteBridge: WebsiteBridge | null = null;
function startWebsiteBridge(address: string, token: string): void {
  websiteBridge?.stop();
  // its own connection to the core: replies must not queue behind the main window's calls
  websiteBridge = new WebsiteBridge(
    () => CoreClient.connect(address, token),
    websiteWindow,
    (partition) => session.fromPartition(partition).clearStorageData(),
  );
  websiteBridge.start();
}

// The core leaves on its own after ops.restore (the database file was replaced); start it again, point every window at
// the new one and reload them. A core that keeps dying (more than 3 times in a minute) is left stopped.
const coreStarts: number[] = [];
let ready: Promise<CoreClient> = Promise.reject(new Error('the core is not started'));
ready.catch(() => {}); // replaced at start; never an unhandled rejection

function startAndAttach(): Promise<CoreClient> {
  const p = connectCore().then((c) => {
    client = c;
    if (core) startWebsiteBridge(core.address, core.token); // a restarted core gets a new address and token
    // Pushes go to every window; each page keeps only its own channel.
    c.onEvent((event) => {
      for (const win of BrowserWindow.getAllWindows()) if (!win.isDestroyed() && !websiteWindows.has(win)) win.webContents.send(Channels.event, event);
    });
    return c;
  });
  return p;
}

async function restartCore(): Promise<void> {
  const now = Date.now();
  while (coreStarts.length > 0 && now - coreStarts[0]! > 60_000) coreStarts.shift();
  if (coreStarts.length >= 3) {
    report('the core keeps exiting; not starting it again');
    return;
  }
  coreStarts.push(now);
  report('starting the core again');
  ready = startAndAttach();
  try {
    await ready;
    for (const win of BrowserWindow.getAllWindows()) if (!win.isDestroyed() && !websiteWindows.has(win)) win.webContents.reload();
  } catch (e) {
    report(`the core did not start again: ${e instanceof Error ? e.message : String(e)}`);
    // Reload anyway: each page's first call then fails with the core's reason (its "fatal:" lines), which it shows.
    for (const win of BrowserWindow.getAllWindows()) if (!win.isDestroyed() && !websiteWindows.has(win)) win.webContents.reload();
  }
}

// The main window draws its own title bar (the page's top bar), so the Cloud Link indicator can sit in it next to the
// window controls. Windows and Linux: the system draws the controls over the top right (titleBarOverlay), the page
// leaves that corner free. macOS: the traffic lights stay at the top left, the page leaves room for them.
const BAR_HEIGHT = 52;
// The controls' colours follow the page's theme (title-bar.ts); a new window starts with the last theme a page said,
// else the OS's, so its overlay and background match before its page loads.
const titleBars = new TitleBars(() => (nativeTheme.shouldUseDarkColors ? 'dark' : 'light'));
const ownTitleBar = () => process.platform === 'darwin'
  ? { titleBarStyle: 'hidden' as const, trafficLightPosition: { x: 16, y: 18 } }
  : { titleBarStyle: 'hidden' as const, titleBarOverlay: { ...TITLE_BAR[titleBars.startTheme()], height: BAR_HEIGHT } };

/** A file in shell/resources: next to package.json in development, inside app.asar when installed (out/ is two levels deeper). */
function appResource(name: string): string {
  return path.join(app.getAppPath(), 'resources', name);
}

function newWindow(opts: { width: number; height: number; x?: number; y?: number; maximized?: boolean; title: string; hash?: string }): BrowserWindow {
  const win = new BrowserWindow({
    width: opts.width,
    height: opts.height,
    ...(opts.x !== undefined && opts.y !== undefined ? { x: opts.x, y: opts.y } : {}),
    title: opts.title,
    ...(opts.hash ? {} : ownTitleBar()), // terminal windows keep the system title bar
    show: false,
    backgroundColor: titleBars.startTheme() === 'dark' ? (opts.hash ? '#101418' : '#14161b') : (opts.hash ? '#fbfbfc' : '#f6f7f9'),
    icon: appResource('icon.png'), // the Yawble mark (Windows and Linux; macOS uses the bundle's)
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
    },
  });
  // Windows takes the taskbar button's icon from the program file. Installed, that is yawble.exe, which carries the
  // Yawble icon; in development it is Electron's electron.exe, so the window names its own icon (an .ico) instead.
  if (process.platform === 'win32' && !app.isPackaged) {
    win.setAppDetails({ appId: `${APP_ID}.dev`, appIconPath: appResource('icon.ico'), appIconIndex: 0, relaunchDisplayName: APP_DISPLAY_NAME });
  }
  // No navigation, no new windows: the renderer only ever shows our own page.
  win.webContents.on('will-navigate', (e) => e.preventDefault());
  // A link in the Concierge's answers (target=_blank) opens in the person's browser: only web and mail addresses.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^(https?:|mailto:)/i.test(url)) void shell.openExternal(url);
    return { action: 'deny' };
  });
  win.webContents.on('context-menu', (_e, params) => editMenu(win, params));
  win.once('ready-to-show', () => { if (opts.maximized) win.maximize(); win.show(); });
  void win.loadFile(path.join(__dirname, '..', '..', 'renderer', 'index.html'), opts.hash ? { hash: opts.hash } : undefined);
  return win;
}

/**
 * The standard right-click menu, which Electron does not give by itself: in a text field Cut, Copy, Paste and Select
 * all (with spelling suggestions first when a word is misspelled), on selected text Copy. Nothing elsewhere.
 */
function editMenu(win: BrowserWindow, p: Electron.ContextMenuParams): void {
  const items: Electron.MenuItemConstructorOptions[] = [];
  if (p.isEditable && p.misspelledWord) {
    for (const s of p.dictionarySuggestions.slice(0, 5)) items.push({ label: s, click: () => win.webContents.replaceMisspelling(s) });
    if (items.length) items.push({ type: 'separator' });
  }
  if (p.isEditable) {
    items.push(
      { role: 'undo', enabled: p.editFlags.canUndo }, { role: 'redo', enabled: p.editFlags.canRedo }, { type: 'separator' },
      { role: 'cut', enabled: p.editFlags.canCut }, { role: 'copy', enabled: p.editFlags.canCopy },
      { role: 'paste', enabled: p.editFlags.canPaste }, { type: 'separator' }, { role: 'selectAll' },
    );
  } else if (p.selectionText.trim()) {
    items.push({ role: 'copy' }, { role: 'selectAll' });
  }
  if (items.length) Menu.buildFromTemplate(items).popup({ window: win });
}

/** One window per agent: opens it, or focuses the one already open. Its page sets the title. */
function openTerminal(memberId: number): void {
  const existing = terminalWindows.get(memberId);
  if (existing && !existing.isDestroyed()) {
    existing.focus();
    return;
  }
  const win = newWindow({ width: 980, height: 620, title: 'Sandbox terminal', hash: `terminal/${memberId}` });
  terminalWindows.set(memberId, win);
  win.on('closed', () => terminalWindows.delete(memberId)); // closing detaches; the run or session goes on
}

// Windows names notifications after the app's AppUserModelID. Installed: the id the installer's Start Menu shortcut
// carries (packaging/build.mjs appId), and the toast shows that shortcut's name. Development (electron.exe, no
// shortcut): a separate id whose display name and icon are registered for the current user, or the toast says "Electron".
const APP_ID = 'com.djlsystems.yawble';
const APP_DISPLAY_NAME = 'Yawble Desktop';
if (process.platform === 'win32') {
  const id = app.isPackaged ? APP_ID : `${APP_ID}.dev`;
  if (!app.isPackaged) {
    const key = `HKCU\\Software\\Classes\\AppUserModelId\\${id}`;
    const reg = (name: string, value: string) =>
      spawnSync('reg', ['add', key, '/v', name, '/t', 'REG_SZ', '/d', value, '/f'], { windowsHide: true });
    reg('DisplayName', APP_DISPLAY_NAME);
    reg('IconUri', appResource('icon.png'));
  }
  app.setAppUserModelId(id);
}

app.whenReady().then(async () => {
  // Everything is refused except writing text to the clipboard (the copy buttons on the Concierge's code blocks).
  session.defaultSession.setPermissionRequestHandler((_wc, perm, cb) => cb(perm === 'clipboard-sanitized-write'));
  coreStarts.push(Date.now());
  ready = startAndAttach();
  const first = ready;
  const requireClient = async () => {
    await ready;
    if (!client) throw new Error('the core is not running');
    return client;
  };
  ipcMain.handle(Channels.getInfo, async () => (await requireClient()).call('core.getInfo'));
  ipcMain.handle(Channels.getAvailableBackends, async () => (await requireClient()).call('sandbox.getAvailableBackends'));
  ipcMain.handle(Channels.call, async (_e, method: unknown, params: unknown) => {
    if (!isUiMethod(method)) throw new Error(`not a method the window may call: ${String(method)}`);
    const args = params && typeof params === 'object' && !Array.isArray(params) ? (params as Record<string, unknown>) : undefined;
    return (await requireClient()).call(method, args);
  });
  // The traffic lights (macOS) are not drawn over the page; TitleBars leaves them alone.
  ipcMain.on('window:dimTitleBar', (e, dim: unknown) => titleBars.setDimmed(BrowserWindow.fromWebContents(e.sender), dim));
  ipcMain.on(Channels.setTheme, (e, theme: unknown) => titleBars.setTheme(BrowserWindow.fromWebContents(e.sender), theme));
  // The page's menu bar (the window draws its own title bar, so the native menu bar is not shown): each command acts
  // on the window that asked. Only the listed commands; anything else is ignored.
  ipcMain.on(Channels.windowCommand, (e, command: unknown) => {
    const wc = e.sender;
    const zoom = (delta: number | null) => wc.setZoomLevel(delta === null ? 0 : Math.max(-3, Math.min(5, wc.getZoomLevel() + delta)));
    switch (command) {
      case 'undo': wc.undo(); break;
      case 'redo': wc.redo(); break;
      case 'cut': wc.cut(); break;
      case 'copy': wc.copy(); break;
      case 'paste': wc.paste(); break;
      case 'selectAll': wc.selectAll(); break;
      case 'reload': wc.reload(); break;
      case 'zoomIn': zoom(0.5); break;
      case 'zoomOut': zoom(-0.5); break;
      case 'zoomReset': zoom(null); break;
      case 'devtools': wc.toggleDevTools(); break;
      case 'quit': quitting = true; app.quit(); break;
      default: break;
    }
  });
  ipcMain.handle(Channels.openTerminal, (_e, memberId: unknown) => {
    if (typeof memberId !== 'number' || !Number.isInteger(memberId) || memberId <= 0) throw new Error('bad member id');
    openTerminal(memberId);
  });
  // A team's documents folder in the file manager: the path comes from the core, never from the page.
  ipcMain.handle(Channels.openDocuments, async (_e, teamId: unknown) => {
    if (typeof teamId !== 'number' || !Number.isInteger(teamId) || teamId <= 0) throw new Error('bad team id');
    const r = await (await requireClient()).call<{ path: string }>('docs.folder', { teamId });
    const failed = await shell.openPath(r.path);
    if (failed) throw new Error(failed);
  });
  ipcMain.handle(Channels.pickFolder, async (e) => {
    const owner = BrowserWindow.fromWebContents(e.sender) ?? undefined;
    const result = owner
      ? await dialog.showOpenDialog(owner, { properties: ['openDirectory'] })
      : await dialog.showOpenDialog({ properties: ['openDirectory'] });
    return result.canceled ? null : (result.filePaths[0] ?? null);
  });
  ipcMain.handle(Channels.shellSettings, () => ({ ...settings, startAtLoginRegistered: startAtLoginRegistered(), packaged: app.isPackaged }));
  // How this copy is installed: for everyone (an administrator updates it) or for this person; who else has it open.
  ipcMain.handle(Channels.updateInfo, () => {
    const allUsers = process.platform === 'win32' && installScope(app.getPath('exe'), process.env) === 'machine';
    return {
      allUsers,
      admin: process.platform === 'win32' ? isAdministrator() : false,
      otherUsers: allUsers ? otherSessionsRunning(path.basename(app.getPath('exe')), process.pid) : 0,
    };
  });
  // Restart to update: the core hands over the downloaded installer it has just verified again (hash and publisher's
  // signature; on Linux the tarball's hash and layout, see linux-update.ts); it runs silently over this install, in the scope it was installed in (an all-users install asks Windows
  // for administrator permission itself), and starts the app again (--force-run).
  ipcMain.handle(Channels.installUpdate, async () => {
    if (!app.isPackaged) throw new Error('a development run updates from its source, not from a release');
    const { path: installer } = (await (await requireClient()).call('updates.installer')) as { path: string };
    if (process.platform === 'linux') {
      // Linux: a copy of the install's apply-update.sh replaces the install from the tarball once the app has quit.
      const plan = linuxUpdatePlan(app.getPath('exe'), installer, os.tmpdir(), dataDir, process.pid);
      copyFileSync(plan.script, plan.copy);
      report(`updating: running ${plan.program} ${plan.args.join(' ')}`);
      spawn(plan.program, plan.args, { detached: true, stdio: 'ignore' }).unref();
    } else {
      const args = updaterArgs(installScope(app.getPath('exe'), process.env));
      report(`updating: running ${installer} ${args.join(' ')}`);
      spawn(installer, args, { detached: true, stdio: 'ignore', windowsHide: true }).unref();
    }
    quitting = true;
    app.quit();
  });
  ipcMain.handle(Channels.setShellSettings, (_e, patch: unknown) => {
    changeSettings(patch);
    return { ...settings, startAtLoginRegistered: startAtLoginRegistered() };
  });

  tray = new AppTray(appResource('tray.png'), // the Yawble head (tray@2x.png for high-DPI)
   {
    open: showMain,
    quit: () => void quitAsked(),
    settings: () => settings,
    change: changeSettings,
  });
  // What the tray says: polled from the core, which keeps running while the window is closed.
  setInterval(() => {
    if (!client) return;
    client.call<Overview>('app.overview').then((o) => {
      running = o.slots.running;
      const needs = o.needsYou ?? 0;
      tray?.update({ running: o.slots.running, queued: o.slots.queued, needsYou: needs });
      // A new question for the person: a system notification when the window is not in front (clicking opens it).
      if (needsYou !== null && needs > needsYou && !(mainWindow?.isFocused() ?? false) && Notification.isSupported()) {
        const n = new Notification({ title: 'Yawble needs you', body: `${needs} thing${needs === 1 ? '' : 's'} waiting for you (questions, approvals, hosts agents ask to reach, developer tools agents ask for, connections to reconnect).` });
        n.on('click', showMain);
        n.show();
      }
      needsYou = needs;
    }, () => tray?.update(null));
  }, 5000);
  if (!startedInBackground(process.argv)) showMain();
  first.catch((e) => report(`${e.message}`));
});

/** Opens the main window, or brings it forward. The core (and the teams' work) is already running either way. */
function showMain(): void {
  if (mainWindow && !mainWindow.isDestroyed()) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
    return;
  }
  // Where it was last time, if that still fits the monitors connected now; otherwise the default size on the main one.
  const toDisplay = (d: Electron.Display): Display => ({ id: d.id, workArea: d.workArea });
  const place = placeWindow(windowStateFile.read(), screen.getAllDisplays().map(toDisplay), toDisplay(screen.getPrimaryDisplay()),
    { width: 1200, height: 800 });
  mainWindow = newWindow({ ...place.bounds, maximized: place.maximized, title: 'Yawble' });
  const win = mainWindow;
  const savePlace = (): void => {
    if (win.isDestroyed() || win.isFullScreen()) return;
    const bounds: Rect = win.getNormalBounds();
    const display = screen.getDisplayMatching(bounds);
    windowStateFile.write({ bounds, maximized: win.isMaximized(), displayId: display.id, displayWorkArea: display.workArea });
  };
  let saveTimer: NodeJS.Timeout | null = null;
  const saveSoon = (): void => { if (saveTimer) clearTimeout(saveTimer); saveTimer = setTimeout(savePlace, 500); };
  for (const e of ['resize', 'move', 'maximize', 'unmaximize'] as const) win.on(e as 'resize', saveSoon);
  win.on('close', () => { if (saveTimer) clearTimeout(saveTimer); savePlace(); });
  mainWindow.on('close', (e) => {
    if (quitting || settings.runInBackground || running === 0) return;
    // Closing the window quits (background running is off) while agents work: ask first.
    e.preventDefault();
    void quitAsked();
  });
  mainWindow.on('closed', () => {
    mainWindow = null;
    if (!quitting && !settings.runInBackground) websiteBridge?.host.closeAll(); // or hidden pages keep the app from quitting
    for (const win of terminalWindows.values()) if (!win.isDestroyed()) win.close();
    if (!quitting && settings.runInBackground && !toldAboutTray && Notification.isSupported()) {
      toldAboutTray = true;
      new Notification({ title: 'Yawble is still running', body: 'Your teams keep working. Open Yawble or quit it from its icon in the tray.' }).show();
    }
  });
}

function changeSettings(patch: unknown): void {
  const p = patch && typeof patch === 'object' ? (patch as Partial<ShellSettings>) : {};
  const next = { ...settings };
  if (typeof p.runInBackground === 'boolean') next.runInBackground = p.runInBackground;
  if (typeof p.startAtLogin === 'boolean') next.startAtLogin = p.startAtLogin;
  if (next.startAtLogin !== settings.startAtLogin) applyStartAtLogin(next.startAtLogin);
  settings = next;
  settingsFile.write(settings);
  tray?.refresh();
}

/** Quit from the tray (or by closing the window with background running off): asks first while agents are working. */
async function quitAsked(): Promise<void> {
  if (running > 0) {
    const owner = mainWindow && !mainWindow.isDestroyed() ? mainWindow : undefined;
    const options = {
      type: 'warning' as const,
      buttons: ['Quit', 'Keep running'],
      defaultId: 1,
      cancelId: 1,
      message: `${running} agent${running === 1 ? ' is' : 's are'} working`,
      detail: 'Quitting stops them now; their runs are recorded as interrupted.',
    };
    const answer = owner ? await dialog.showMessageBox(owner, options) : await dialog.showMessageBox(options);
    if (answer.response !== 0) return;
  }
  quitting = true;
  app.quit();
}

// One app per person: a second start (the shortcut, a sign-in start) opens the running app's window. A run with its
// own data folder (development and tests) is separate and does not take part.
if (!process.env.YAWBLE_DATA_DIR) {
  if (!app.requestSingleInstanceLock()) app.quit();
  else app.on('second-instance', () => { if (app.isReady()) showMain(); });
}

app.on('activate', () => { if (app.isReady()) showMain(); }); // macOS: the Dock icon
app.on('before-quit', () => { quitting = true; });
app.on('window-all-closed', () => {
  if (!quitting && settings.runInBackground) return; // keep running in the tray
  app.quit();
});
app.on('will-quit', () => {
  tray?.destroy();
  websiteBridge?.stop();
  client?.close();
  core?.child.stdin?.end(); // the core exits on stdin EOF
});
