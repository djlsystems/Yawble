// The window controls the system draws over the page's own title bar (Windows, Linux: titleBarOverlay). Their
// background and symbols follow the page's theme (the renderer says it over window:setTheme) and dim while a dialog
// dims the page (window:dimTitleBar). macOS draws its traffic lights apart from the page: nothing to colour there.

export type Theme = 'light' | 'dark';
export interface Overlay { color: string; symbolColor: string }

// These must equal the palette in renderer/src/styles.css (theme.test.ts parses it and pins them):
// the bar is painted with --surface (App.vue .app) and its menu text is --ink (MenuBar.vue .top), so the controls use
// those; dimmed is each under the dialogs' --backdrop (light rgba(20, 26, 36, .35), dark rgba(0, 0, 0, .55)).
export const TITLE_BAR: Record<Theme, Overlay> = {
  light: { color: '#ffffff', symbolColor: '#1d2330' },
  dark: { color: '#1b1e24', symbolColor: '#e4e7ec' },
};
export const TITLE_BAR_DIMMED: Record<Theme, Overlay> = {
  light: { color: '#adafb2', symbolColor: '#1a202c' },
  dark: { color: '#0c0d10', symbolColor: '#67686a' },
};

/**
 * A new window's background until its page paints: the main window's page is --surface (styles.css body, App.vue
 * .app), a terminal window's is --term-bg (terminal-theme.css, TerminalApp.vue .term-window).
 */
export const WINDOW_BACKGROUND: Record<Theme, { main: string; terminal: string }> = {
  light: { main: '#ffffff', terminal: '#fbfbfc' },
  dark: { main: '#1b1e24', terminal: '#101418' },
};
export const windowBackground = (theme: Theme, terminal: boolean): string =>
  WINDOW_BACKGROUND[theme][terminal ? 'terminal' : 'main'];

export const isTheme = (v: unknown): v is Theme => v === 'light' || v === 'dark';

export function overlayFor(theme: Theme, dimmed: boolean): Overlay {
  return (dimmed ? TITLE_BAR_DIMMED : TITLE_BAR)[theme];
}

/** A window whose title-bar controls can be coloured (BrowserWindow). */
export interface OverlayWindow { setTitleBarOverlay(options: Overlay): void }

/**
 * Each window's theme and dimmed state, and the overlay they make. A window that has not said its theme yet uses the
 * starting theme (the last one any window said, else the OS's).
 */
export class TitleBars {
  private readonly state = new WeakMap<OverlayWindow, { theme: Theme; dimmed: boolean }>();
  private last: Theme | null = null;

  constructor(private readonly osTheme: () => Theme, private readonly platform: string = process.platform) {}

  /** The theme a new window starts with, for its titleBarOverlay and background before its page says. */
  startTheme(): Theme { return this.last ?? this.osTheme(); }

  setTheme(win: OverlayWindow | null | undefined, theme: unknown): void {
    if (!win || !isTheme(theme)) return;
    this.last = theme;
    const s = this.state.get(win) ?? { theme, dimmed: false };
    s.theme = theme;
    this.state.set(win, s);
    this.apply(win, s);
  }

  setDimmed(win: OverlayWindow | null | undefined, dimmed: unknown): void {
    if (!win) return;
    const s = this.state.get(win) ?? { theme: this.startTheme(), dimmed: false };
    s.dimmed = dimmed === true;
    this.state.set(win, s);
    this.apply(win, s);
  }

  private apply(win: OverlayWindow, s: { theme: Theme; dimmed: boolean }): void {
    if (this.platform === 'darwin') return;
    // A terminal window keeps the system title bar and has no overlay to colour: Electron refuses, which is fine.
    try { win.setTitleBarOverlay(overlayFor(s.theme, s.dimmed)); } catch { /* no overlay on this window */ }
  }
}
