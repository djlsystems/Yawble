// The window controls the system draws over the page's own title bar (Windows, Linux: titleBarOverlay). Their
// background and symbols follow the page's theme (the renderer says it over window:setTheme) and dim while a dialog
// dims the page (window:dimTitleBar). macOS draws its traffic lights apart from the page: nothing to colour there.

export type Theme = 'light' | 'dark';
export interface Overlay { color: string; symbolColor: string }

/** The bar's background is the page's --surface in each theme; dimmed is that under the dialogs' backdrop, rgba(20, 26, 36, .35). */
export const TITLE_BAR: Record<Theme, Overlay> = {
  light: { color: '#ffffff', symbolColor: '#3c4048' },
  dark: { color: '#1a1d24', symbolColor: '#d6dae2' },
};
export const TITLE_BAR_DIMMED: Record<Theme, Overlay> = {
  light: { color: '#adafb2', symbolColor: '#2a2d33' },
  dark: { color: '#18191f', symbolColor: '#9a9ea6' },
};

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
