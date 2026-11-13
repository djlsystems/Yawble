import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

/**
 * Where the main window was, so the next start puts it back: the same monitor, place and size, maximized or not.
 * The screens may have changed since (a monitor unplugged, a new resolution, a laptop off its dock), so the saved
 * place is only used when it still fits what is connected now; otherwise the window opens at its default size,
 * centred on the main monitor. Pure apart from the file, so it is tested without Electron.
 */
export interface Rect { x: number; y: number; width: number; height: number }

/** One connected monitor: its id and the part of it windows may use (without the taskbar). */
export interface Display { id: number; workArea: Rect }

export interface SavedWindow {
  /** The window's size and place when not maximized (Electron's normal bounds). */
  bounds: Rect;
  maximized: boolean;
  /** The monitor it was on, and that monitor's work area then, to notice a monitor that changed size. */
  displayId: number;
  displayWorkArea: Rect;
}

export interface Placement {
  bounds: Rect;
  maximized: boolean;
  /** 'saved': exactly as it was; 'fitted': on its monitor but moved or shrunk to fit; 'default': the saved place no longer fits. */
  how: 'saved' | 'fitted' | 'default';
}

/** The window's smallest useful size; below this the app's layout does not work. */
export const MIN_SIZE = { width: 720, height: 480 };
/** How much of the window's top strip (its title bar) must be on a monitor for the saved place to count as visible. */
const GRAB = { width: 160, height: 40 };

/** Where the main window opens, from what was saved (if anything) and the monitors connected now. */
export function placeWindow(saved: SavedWindow | null, displays: readonly Display[], primary: Display,
  defaults: { width: number; height: number }): Placement {
  const fallback = (maximized: boolean): Placement => ({ bounds: centred(primary.workArea, defaults), maximized, how: 'default' });
  if (!saved || !validRect(saved.bounds)) return fallback(false);

  // The monitor it was on, if still connected; else whichever monitor shows its title bar.
  const same = displays.find((d) => d.id === saved.displayId);
  const host = same && grabbable(saved.bounds, same.workArea) ? same : displays.find((d) => grabbable(saved.bounds, d.workArea));
  if (!host) {
    // Its monitor is gone (or the place is off every screen). A maximized window stays maximized, on the main monitor.
    return fallback(saved.maximized);
  }

  const unchanged = host.id === saved.displayId && sameRect(host.workArea, saved.displayWorkArea);
  const fitted = fit(saved.bounds, host.workArea);
  if (unchanged && sameRect(fitted, saved.bounds)) return { bounds: saved.bounds, maximized: saved.maximized, how: 'saved' };
  return { bounds: fitted, maximized: saved.maximized, how: 'fitted' };
}

/** The window's size, clamped to the work area (and at least MIN_SIZE where the screen allows), then moved inside it. */
export function fit(r: Rect, area: Rect): Rect {
  const width = Math.min(Math.max(r.width, Math.min(MIN_SIZE.width, area.width)), area.width);
  const height = Math.min(Math.max(r.height, Math.min(MIN_SIZE.height, area.height)), area.height);
  const x = Math.min(Math.max(r.x, area.x), area.x + area.width - width);
  const y = Math.min(Math.max(r.y, area.y), area.y + area.height - height);
  return { x: Math.round(x), y: Math.round(y), width: Math.round(width), height: Math.round(height) };
}

function centred(area: Rect, size: { width: number; height: number }): Rect {
  // The default size, but never larger than 90% of a small screen.
  const width = Math.min(size.width, Math.floor(area.width * 0.9));
  const height = Math.min(size.height, Math.floor(area.height * 0.9));
  return { x: Math.round(area.x + (area.width - width) / 2), y: Math.round(area.y + (area.height - height) / 2), width, height };
}

/** Whether enough of the window's top strip is on this monitor to grab it with the mouse. */
function grabbable(r: Rect, area: Rect): boolean {
  const top: Rect = { x: r.x, y: r.y, width: r.width, height: Math.min(r.height, GRAB.height) };
  const ix = Math.min(top.x + top.width, area.x + area.width) - Math.max(top.x, area.x);
  const iy = Math.min(top.y + top.height, area.y + area.height) - Math.max(top.y, area.y);
  return ix >= Math.min(GRAB.width, r.width) && iy >= Math.min(GRAB.height, r.height) / 2;
}

function sameRect(a: Rect, b: Rect): boolean {
  return a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;
}

function validRect(r: unknown): r is Rect {
  if (!r || typeof r !== 'object') return false;
  const o = r as Record<string, unknown>;
  return ['x', 'y', 'width', 'height'].every((k) => typeof o[k] === 'number' && Number.isFinite(o[k] as number))
    && (o.width as number) > 0 && (o.height as number) > 0;
}

/** window-state.json in the shell's data folder. A missing or damaged file means "nothing saved". */
export class WindowStateFile {
  constructor(private readonly file: string) {}

  read(): SavedWindow | null {
    try {
      const raw = JSON.parse(readFileSync(this.file, 'utf8')) as Partial<SavedWindow>;
      if (!validRect(raw.bounds) || !validRect(raw.displayWorkArea) || typeof raw.displayId !== 'number') return null;
      return { bounds: raw.bounds, maximized: raw.maximized === true, displayId: raw.displayId, displayWorkArea: raw.displayWorkArea };
    } catch {
      return null;
    }
  }

  write(state: SavedWindow): void {
    try {
      mkdirSync(path.dirname(this.file), { recursive: true });
      writeFileSync(this.file, JSON.stringify(state, null, 2));
    } catch { /* a place not saved is not worth a failure */ }
  }
}
