// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { nextTick } from 'vue';
import { createTheme, REMOTE_THEME_KEY, THEME_KEY, toggleLabel, type ThemeEnv } from '../../src/renderer/src/theme';
import { TitleBars, TITLE_BAR, TITLE_BAR_DIMMED, overlayFor, type Overlay } from '../../src/main/title-bar';
import { XTERM_PALETTES } from '../../src/renderer/src/terminalPane';

// Fakes only: a stand-in for prefers-color-scheme that the test flips, and a Map for localStorage.
function fakeOs(dark: boolean) {
  const listeners = new Set<() => void>();
  const media = {
    matches: dark,
    addEventListener: (_: string, l: () => void) => listeners.add(l),
    removeEventListener: (_: string, l: () => void) => listeners.delete(l),
  };
  return {
    matchMedia: (q: string) => { expect(q).toBe('(prefers-color-scheme: dark)'); return media as any; },
    set(d: boolean) { media.matches = d; for (const l of listeners) l(); },
    listeners,
  };
}
function fakeStorage(initial: Record<string, string> = {}) {
  const m = new Map(Object.entries(initial));
  return {
    map: m,
    storage: () => ({ getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v), removeItem: (k: string) => void m.delete(k) }),
  };
}
const blocked: ThemeEnv['storage'] = () => { throw new DOMException('blocked', 'SecurityError'); };

describe('theme mode (theme.ts)', () => {
  it('first load follows the OS and keeps following it live', () => {
    const os = fakeOs(true);
    const root = document.createElement('html');
    const t = createTheme({ storage: fakeStorage().storage, matchMedia: os.matchMedia, root });
    expect(t.state).toEqual({ mode: 'system', current: 'dark' });
    expect(root.getAttribute('data-theme')).toBe('dark');
    os.set(false);
    expect(t.state.current).toBe('light');
    expect(root.getAttribute('data-theme')).toBe('light');
    os.set(true);
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('a click flips the theme, keeps it, and the OS no longer applies', () => {
    const os = fakeOs(false);
    const s = fakeStorage();
    const root = document.createElement('html');
    const t = createTheme({ storage: s.storage, matchMedia: os.matchMedia, root });
    t.toggle();
    expect(t.state).toEqual({ mode: 'dark', current: 'dark' });
    expect(s.map.get(THEME_KEY)).toBe('dark');
    os.set(false); os.set(true); os.set(false);
    expect(root.getAttribute('data-theme')).toBe('dark');
    // The next start (a reload) keeps the choice whatever the OS says.
    const again = createTheme({ storage: s.storage, matchMedia: fakeOs(false).matchMedia, root: document.createElement('html') });
    expect(again.state).toEqual({ mode: 'dark', current: 'dark' });
    again.toggle();
    expect(again.state.current).toBe('light');
    expect(s.map.get(THEME_KEY)).toBe('light');
  });

  it('"Use system setting" clears the choice and follows the OS again', () => {
    const os = fakeOs(true);
    const s = fakeStorage({ [THEME_KEY]: 'light' });
    const root = document.createElement('html');
    const t = createTheme({ storage: s.storage, matchMedia: os.matchMedia, root });
    expect(t.state.current).toBe('light');
    t.setMode('system');
    expect(s.map.has(THEME_KEY)).toBe(false);
    expect(t.state).toEqual({ mode: 'system', current: 'dark' });
    os.set(false);
    expect(root.getAttribute('data-theme')).toBe('light');
  });

  it('works when storage is blocked: follows the OS, and a click still flips it for the page', () => {
    const os = fakeOs(true);
    const root = document.createElement('html');
    const t = createTheme({ storage: blocked, matchMedia: os.matchMedia, root });
    expect(t.state.current).toBe('dark');
    expect(() => t.toggle()).not.toThrow();
    expect(root.getAttribute('data-theme')).toBe('light');
    expect(() => t.setMode('system')).not.toThrow();
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('ignores a stored value that is not a theme, and works without matchMedia (light)', () => {
    const t = createTheme({ storage: fakeStorage({ [THEME_KEY]: 'purple' }).storage, root: document.createElement('html') });
    expect(t.state).toEqual({ mode: 'system', current: 'light' });
  });

  it('tells notify each theme it applies; dispose stops following the OS', () => {
    const os = fakeOs(false);
    const told: string[] = [];
    const t = createTheme({ storage: fakeStorage().storage, matchMedia: os.matchMedia, notify: (x) => told.push(x) });
    os.set(true);
    t.setMode('light');
    expect(told).toEqual(['light', 'dark', 'light']);
    t.setMode('system');
    t.dispose();
    expect(os.listeners.size).toBe(0);
  });

  it('follows a switch made in another window of the app (a storage event on its key), and stops on dispose', () => {
    const s = fakeStorage();
    const events = new EventTarget();
    const root = document.createElement('html');
    const told: string[] = [];
    const t = createTheme({ storage: s.storage, matchMedia: fakeOs(false).matchMedia, root, events, notify: (x) => told.push(x) });
    expect(t.state).toEqual({ mode: 'system', current: 'light' });

    // The main window picks dark: its localStorage write reaches this window as a storage event.
    s.map.set(THEME_KEY, 'dark');
    events.dispatchEvent(new StorageEvent('storage', { key: THEME_KEY, newValue: 'dark' }));
    expect(t.state).toEqual({ mode: 'dark', current: 'dark' });
    expect(root.getAttribute('data-theme')).toBe('dark');
    expect(s.map.get(THEME_KEY)).toBe('dark'); // nothing written back

    // Another key (the remote view's) changes nothing; "Use system setting" there (removed) follows the OS here.
    s.map.set(REMOTE_THEME_KEY, 'light');
    events.dispatchEvent(new StorageEvent('storage', { key: REMOTE_THEME_KEY, newValue: 'light' }));
    expect(t.state.current).toBe('dark');
    s.map.delete(THEME_KEY);
    events.dispatchEvent(new StorageEvent('storage', { key: THEME_KEY, newValue: null }));
    expect(t.state).toEqual({ mode: 'system', current: 'light' });
    // The whole storage cleared (key null) counts too.
    s.map.set(THEME_KEY, 'dark');
    events.dispatchEvent(new StorageEvent('storage', { key: null }));
    expect(t.state.current).toBe('dark');
    expect(told).toEqual(['light', 'dark', 'light', 'dark']);

    t.dispose();
    s.map.set(THEME_KEY, 'light');
    events.dispatchEvent(new StorageEvent('storage', { key: THEME_KEY, newValue: 'light' }));
    expect(t.state.current).toBe('dark');
  });

  // Card 145 (tester): a storage event must never write to storage, or two windows would echo each other.
  it('never writes to storage while following storage events (own key, key null, other key, remote key)', () => {
    for (const key of [THEME_KEY, REMOTE_THEME_KEY]) {
      const s = fakeStorage();
      const writes: string[] = [];
      const storage = () => {
        const inner = s.storage();
        return {
          getItem: inner.getItem,
          setItem: (k: string, v: string) => { writes.push(`set ${k}=${v}`); inner.setItem(k, v); },
          removeItem: (k: string) => { writes.push(`remove ${k}`); inner.removeItem(k); },
        };
      };
      const events = new EventTarget();
      const root = document.createElement('html');
      const t = createTheme({ storage, matchMedia: fakeOs(false).matchMedia, root, events, key });
      const other = key === THEME_KEY ? REMOTE_THEME_KEY : THEME_KEY;

      s.map.set(key, 'dark');
      events.dispatchEvent(new StorageEvent('storage', { key, newValue: 'dark' }));
      expect(t.state).toEqual({ mode: 'dark', current: 'dark' });
      s.map.set(other, 'light');
      events.dispatchEvent(new StorageEvent('storage', { key: other, newValue: 'light' }));
      expect(t.state.current).toBe('dark'); // the other page's key is independent
      s.map.delete(key);
      events.dispatchEvent(new StorageEvent('storage', { key, newValue: null }));
      expect(t.state).toEqual({ mode: 'system', current: 'light' });
      s.map.set(key, 'dark');
      events.dispatchEvent(new StorageEvent('storage', { key: null }));
      expect(t.state.current).toBe('dark');
      expect(writes).toEqual([]);
      expect(root.getAttribute('data-theme')).toBe('dark');

      // A click after following still keeps the choice under this page's key only.
      t.toggle();
      expect(writes).toEqual([`set ${key}=light`]);
      t.dispose();
    }
  });

  it('labels the button with what one click does', () => {
    expect(toggleLabel('light')).toBe('Switch to dark theme');
    expect(toggleLabel('dark')).toBe('Switch to light theme');
  });
});

describe('the page theme (module load)', () => {
  beforeEach(() => {
    vi.resetModules();
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });
  afterEach(() => {
    vi.doUnmock('vue');
    vi.doUnmock('../../src/renderer/src/App.vue');
    vi.doUnmock('../../src/renderer/src/TerminalApp.vue');
    delete (window as any).yawble;
    vi.unstubAllGlobals();
  });

  it('sets data-theme on <html> before the window mounts anything', async () => {
    const os = fakeOs(true);
    vi.stubGlobal('matchMedia', os.matchMedia);
    (window as any).yawble = { setTheme: vi.fn() };
    let atMount: string | null | undefined;
    vi.doMock('vue', async (orig) => ({
      ...(await orig<typeof import('vue')>()),
      createApp: () => ({ mount: () => { atMount = document.documentElement.getAttribute('data-theme'); } }),
    }));
    vi.doMock('../../src/renderer/src/App.vue', () => ({ default: {} }));
    vi.doMock('../../src/renderer/src/TerminalApp.vue', () => ({ default: {} }));
    await import('../../src/renderer/src/main');
    expect(atMount).toBe('dark');
    expect((window as any).yawble.setTheme).toHaveBeenCalledWith('dark');
  });

  it('the desktop window tells main (setTheme) on load and on each switch', async () => {
    vi.stubGlobal('matchMedia', fakeOs(false).matchMedia);
    const setTheme = vi.fn();
    (window as any).yawble = { setTheme };
    const { theme } = await import('../../src/renderer/src/theme');
    theme.toggle();
    expect(setTheme.mock.calls).toEqual([['light'], ['dark']]);
    expect(localStorage.getItem(THEME_KEY)).toBe('dark');
  });

  it('a remote view keeps its own choice and has no title bar to colour', async () => {
    vi.stubGlobal('matchMedia', fakeOs(false).matchMedia);
    localStorage.setItem(THEME_KEY, 'dark'); // the desktop's choice, on the same origin
    const setTheme = vi.fn();
    (window as any).yawble = { remote: true, setTheme };
    const { theme } = await import('../../src/renderer/src/theme');
    expect(theme.state).toEqual({ mode: 'system', current: 'light' });
    theme.toggle();
    expect(localStorage.getItem(REMOTE_THEME_KEY)).toBe('dark');
    expect(localStorage.getItem(THEME_KEY)).toBe('dark');
    theme.setMode('light');
    expect(localStorage.getItem(REMOTE_THEME_KEY)).toBe('light');
    expect(localStorage.getItem(THEME_KEY)).toBe('dark');
    expect(setTheme).not.toHaveBeenCalled();
  });

  it("an open terminal window follows a switch in the main window (the page listens to window's storage event)", async () => {
    vi.stubGlobal('matchMedia', fakeOs(false).matchMedia);
    const setTheme = vi.fn();
    (window as any).yawble = { setTheme };
    const { theme } = await import('../../src/renderer/src/theme');
    expect(theme.state.current).toBe('light');
    localStorage.setItem(THEME_KEY, 'dark'); // as the main window's toggle does
    window.dispatchEvent(new StorageEvent('storage', { key: THEME_KEY, newValue: 'dark' }));
    expect(theme.state).toEqual({ mode: 'dark', current: 'dark' });
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(setTheme).toHaveBeenLastCalledWith('dark'); // its own title bar and background follow too
    theme.dispose();
  });

  it("a remote view ignores the desktop's key in storage events", async () => {
    vi.stubGlobal('matchMedia', fakeOs(false).matchMedia);
    (window as any).yawble = { remote: true };
    const { theme } = await import('../../src/renderer/src/theme');
    localStorage.setItem(THEME_KEY, 'dark');
    window.dispatchEvent(new StorageEvent('storage', { key: THEME_KEY, newValue: 'dark' }));
    expect(theme.state.current).toBe('light');
    localStorage.setItem(REMOTE_THEME_KEY, 'dark');
    window.dispatchEvent(new StorageEvent('storage', { key: REMOTE_THEME_KEY, newValue: 'dark' }));
    expect(theme.state.current).toBe('dark');
    theme.dispose();
  });
});

describe('title-bar overlay (main, title-bar.ts)', () => {
  const fakeWindow = () => { const sent: Overlay[] = []; return { sent, setTitleBarOverlay: (o: Overlay) => void sent.push(o) }; };

  it('colours the window controls for the theme the page says, dimmed under a dialog', () => {
    const bars = new TitleBars(() => 'light', 'win32');
    const win = fakeWindow();
    bars.setTheme(win, 'dark');
    bars.setDimmed(win, true);
    bars.setDimmed(win, false);
    bars.setTheme(win, 'light');
    expect(win.sent).toEqual([TITLE_BAR.dark, TITLE_BAR_DIMMED.dark, TITLE_BAR.dark, TITLE_BAR.light]);
    expect(TITLE_BAR.dark).not.toEqual(TITLE_BAR.light);
    expect(overlayFor('light', true)).toEqual(TITLE_BAR_DIMMED.light);
  });

  it('ignores anything that is not a theme, and a window that is gone', () => {
    const bars = new TitleBars(() => 'light', 'win32');
    const win = fakeWindow();
    bars.setTheme(win, 'purple');
    bars.setTheme(null, 'dark');
    expect(win.sent).toEqual([]);
  });

  it('a new window starts with the OS theme, then the last one a page said', () => {
    const bars = new TitleBars(() => 'dark', 'win32');
    expect(bars.startTheme()).toBe('dark');
    const win = fakeWindow();
    bars.setDimmed(win, true);
    expect(win.sent).toEqual([TITLE_BAR_DIMMED.dark]);
    bars.setTheme(win, 'light');
    expect(bars.startTheme()).toBe('light');
  });

  it('starts the next launch with the theme kept from the last one, and keeps each change (once)', () => {
    const kept: string[] = [];
    let stored: 'light' | 'dark' | null = 'dark';
    const memory = { read: () => stored, write: (t: 'light' | 'dark') => { kept.push(t); stored = t; } };
    const bars = new TitleBars(() => 'light', 'win32', memory);
    expect(bars.startTheme()).toBe('dark'); // the person chose dark on a light OS last time
    const win = fakeWindow();
    bars.setTheme(win, 'dark');
    bars.setTheme(fakeWindow(), 'dark'); // a second window saying the same: not written again
    expect(kept).toEqual([]);
    bars.setTheme(win, 'light');
    expect(kept).toEqual(['light']);
    expect(new TitleBars(() => 'dark', 'win32', memory).startTheme()).toBe('light');
    // Nothing kept, or a memory that throws: the OS theme.
    expect(new TitleBars(() => 'dark', 'win32', { read: () => null, write: () => {} }).startTheme()).toBe('dark');
    const broken = { read: () => { throw new Error('io'); }, write: () => { throw new Error('io'); } };
    const b = new TitleBars(() => 'light', 'win32', broken);
    expect(b.startTheme()).toBe('light');
    expect(() => b.setTheme(fakeWindow(), 'dark')).not.toThrow();
    expect(b.startTheme()).toBe('dark');
  });

  it('does nothing on macOS, and survives a window without an overlay (a terminal window)', () => {
    const mac = new TitleBars(() => 'light', 'darwin');
    const win = fakeWindow();
    mac.setTheme(win, 'dark');
    expect(win.sent).toEqual([]);
    const bars = new TitleBars(() => 'light', 'win32');
    expect(() => bars.setTheme({ setTitleBarOverlay: () => { throw new Error('no overlay'); } }, 'dark')).not.toThrow();
  });

  it('the preload sends the theme to main on window:setTheme', async () => {
    vi.resetModules();
    const send = vi.fn();
    let exposed: any;
    vi.doMock('electron', () => ({
      contextBridge: { exposeInMainWorld: (_: string, api: unknown) => { exposed = api; } },
      ipcRenderer: { send, invoke: vi.fn(), on: vi.fn(), off: vi.fn() },
    }));
    await import('../../src/preload/index');
    exposed.setTheme('dark');
    expect(send).toHaveBeenCalledWith('window:setTheme', 'dark');
    vi.doUnmock('electron');
  });
});

describe('title-bar button (ThemeToggle.vue) and the View menu', () => {
  it('shows a moon in light ("Switch to dark theme"), a sun in dark; one click flips it', async () => {
    const ThemeToggle = (await import('../../src/renderer/src/components/ThemeToggle.vue')).default;
    const t = createTheme({ storage: fakeStorage().storage, matchMedia: fakeOs(false).matchMedia, root: document.createElement('html') });
    const w = mount(ThemeToggle, { props: { theme: t } });
    const btn = w.get('[data-test="theme-toggle"]');
    expect(btn.attributes('aria-label')).toBe('Switch to dark theme');
    expect(btn.attributes('title')).toBe('Switch to dark theme');
    expect(w.find('[data-test="theme-icon-moon"]').exists()).toBe(true);
    await btn.trigger('click');
    expect(t.state.current).toBe('dark');
    expect(btn.attributes('aria-label')).toBe('Switch to light theme');
    expect(w.find('[data-test="theme-icon-sun"]').exists()).toBe(true);
    await btn.trigger('click');
    expect(t.state.current).toBe('light');
  });

  it('the menu shows radio items as menuitemradio with aria-checked', async () => {
    const MenuBar = (await import('../../src/renderer/src/components/MenuBar.vue')).default;
    const w = mount(MenuBar, { props: { menus: [{ label: 'View', items: [
      { id: 'theme:system', label: 'Theme: Use system setting', radio: true, checked: true },
      { id: 'theme:dark', label: 'Theme: Dark', radio: true, checked: false },
      { id: 'toggleTheme', label: 'Toggle theme' },
    ] }] }, attachTo: document.body });
    await w.get('[data-test="menu-view"]').trigger('click');
    expect(w.get('[data-test="menuitem-theme:system"]').attributes()).toMatchObject({ role: 'menuitemradio', 'aria-checked': 'true' });
    expect(w.get('[data-test="menuitem-theme:dark"]').attributes()).toMatchObject({ role: 'menuitemradio', 'aria-checked': 'false' });
    expect(w.get('[data-test="menuitem-toggleTheme"]').attributes('role')).toBe('menuitem');
    expect(w.get('[data-test="menuitem-toggleTheme"]').attributes('aria-checked')).toBeUndefined();
    await w.get('[data-test="menuitem-theme:dark"]').trigger('click');
    expect(w.emitted('command')).toEqual([['theme:dark']]);
    w.unmount();
  });
});

describe('terminals follow the theme live', () => {
  it('TerminalView switches the xterm palette when the theme changes, without a new terminal', async () => {
    vi.resetModules();
    localStorage.clear();
    vi.stubGlobal('matchMedia', fakeOs(false).matchMedia);
    const made: { options: Record<string, unknown> }[] = [];
    vi.doMock('@xterm/xterm', () => ({
      Terminal: class {
        cols = 80; rows = 24; options: Record<string, unknown>;
        constructor(o: Record<string, unknown>) { this.options = { ...o }; made.push(this); }
        open() {} write() {} reset() {} focus() {} dispose() {} loadAddon() {}
        onData() { return { dispose() {} }; }
        onResize() { return { dispose() {} }; }
      },
    }));
    vi.doMock('@xterm/addon-fit', () => ({ FitAddon: class { fit() {} } }));
    (globalThis as any).ResizeObserver ??= class { observe() {} disconnect() {} };
    const { theme } = await import('../../src/renderer/src/theme');
    const TerminalView = (await import('../../src/renderer/src/components/TerminalView.vue')).default;
    const api: any = {
      call: async (m: string) => (m === 'terminal.attach' ? { replay: '', end: 0, session: null } : {}),
      onEvent: () => () => {},
    };
    const w = mount(TerminalView, { props: { channel: 'member:1', api } });
    await flushPromises();
    expect(made).toHaveLength(1);
    expect(made[0].options.theme).toEqual(XTERM_PALETTES.light);
    theme.setMode('dark');
    await nextTick();
    expect(made[0].options.theme).toEqual(XTERM_PALETTES.dark);
    theme.setMode('light');
    await nextTick();
    expect(made[0].options.theme).toEqual(XTERM_PALETTES.light);
    expect(made).toHaveLength(1);
    w.unmount();
    vi.doUnmock('@xterm/xterm');
    vi.doUnmock('@xterm/addon-fit');
    vi.unstubAllGlobals();
  });
});
