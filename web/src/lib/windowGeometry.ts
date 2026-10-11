/**
 * WHERE A MOVABLE WINDOW SITS AND HOW BIG IT IS, as a per-browser preference.
 *
 * Shared by every window a person can drag and resize - the Concierge terminal and the Documents
 * explorer - so "what is a legal size", "does it still fit this screen" and "was it ever placed"
 * have one answer each. Each window brings its own storage key, bounds and default; the rules are
 * these.
 *
 * Storage is read and written in try/catch: it throws in a private window with cookies blocked,
 * and a remembered window position is not worth a crash. A value is CLAMPED ON READ, not only on
 * write, because storage is editable by hand and shared with older builds.
 */

export interface Bounds {
  min: number;
  max: number;
}

export interface WindowBounds {
  width: Bounds;
  height: Bounds;
}

export interface WindowGeometry {
  left: number;
  top: number;
  width: number;
  height: number;
}

/** What a window keeps in storage: where it is, and whether it was last left maximised. */
export interface StoredWindow extends WindowGeometry {
  maximised: boolean;
}

/**
 * A size inside its bounds, or the fallback.
 *
 * NOT `typeof value === 'number'` alone. A number input yields NaN for an empty box, and NaN
 * survives every comparison silently - `Math.min(NaN, x)` is NaN - so an unguarded clamp writes
 * NaN into storage and the element takes `NaNpx`, which the browser DROPS. The element then has no
 * size at all, which looks like a layout bug rather than a bad value.
 */
export function clampNumber(value: unknown, bounds: Bounds, fallback: number): number {
  if (typeof value !== 'number' || Number.isNaN(value)) return fallback;

  return Math.round(Math.min(bounds.max, Math.max(bounds.min, value)));
}

/** A position: any finite number, rounded, else the fallback. */
export function finiteOr(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? Math.round(value) : fallback;
}

/** Every field clamped on its own, so one bad field does not cost the others. */
export function clampGeometry(
  partial: Partial<WindowGeometry>,
  bounds: WindowBounds,
  fallback: WindowGeometry,
): WindowGeometry {
  return {
    left: finiteOr(partial.left, fallback.left),
    top: finiteOr(partial.top, fallback.top),
    width: clampNumber(partial.width, bounds.width, fallback.width),
    height: clampNumber(partial.height, bounds.height, fallback.height),
  };
}

/**
 * The window shrunk to the viewport and moved back on to it: no wider or taller than the screen
 * less a margin, and its left and top edges kept where the window can still be grabbed.
 */
export function clampToViewport<G extends WindowGeometry>(
  geometry: G,
  bounds: WindowBounds,
  viewportWidth: number,
  viewportHeight: number,
): G {
  const width = Math.min(geometry.width, Math.max(bounds.width.min, viewportWidth - 20));
  const height = Math.min(geometry.height, Math.max(bounds.height.min, viewportHeight - 20));

  const left = Math.max(0, Math.min(geometry.left, viewportWidth - width - 10));
  const top = Math.max(0, Math.min(geometry.top, viewportHeight - height - 10));

  return { ...geometry, width, height, left, top };
}

/**
 * Where a window goes the first time, or when what was stored no longer means anything: the
 * preferred size if the screen has room for it, inside its bounds, placed bottom-right (clear of
 * the floating button, as the Concierge sits) or centred.
 */
export function initialGeometry(
  viewportWidth: number,
  viewportHeight: number,
  bounds: WindowBounds,
  preferred: { width: number; height: number },
  place: 'bottom-right' | 'centre',
): WindowGeometry {
  const width = clampNumber(Math.min(preferred.width, viewportWidth - 40), bounds.width, preferred.width);
  const height = clampNumber(Math.min(preferred.height, viewportHeight - 40), bounds.height, preferred.height);

  if (place === 'centre') {
    return {
      width,
      height,
      left: Math.max(0, Math.round((viewportWidth - width) / 2)),
      top: Math.max(0, Math.round((viewportHeight - height) / 2)),
    };
  }

  return {
    width,
    height,
    left: Math.max(0, viewportWidth - width - 20),
    top: Math.max(0, viewportHeight - height - 20),
  };
}

/**
 * Whether a stored value says the person ever placed the window: an object with `left` and `top`
 * as its OWN properties. `left: 0, top: 0` is placed - somebody may have dragged it to the corner.
 * Read from the raw stored value, never from a clamped one, which always has both.
 */
export function isPlaced(stored: unknown): boolean {
  return (
    typeof stored === 'object' &&
    stored !== null &&
    !Array.isArray(stored) &&
    Object.prototype.hasOwnProperty.call(stored, 'left') &&
    Object.prototype.hasOwnProperty.call(stored, 'top')
  );
}

/**
 * What is stored under a key, parsed, or null when there is nothing, it does not parse, or storage
 * refuses to be read at all.
 */
export function readStored(key: string): unknown {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) return null;

    return JSON.parse(raw) as unknown;
  } catch {
    return null;
  }
}

/** Stored, or silently not: the value still applies for this session. */
export function writeStored(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Nothing to do and nothing worth saying.
  }
}

/**
 * A remembered window, or null for "unplaced" - the caller then shows it at its default.
 *
 * Unplaced when nothing usable is stored (absent, unreadable, not an object, an array, no `left`
 * and `top` of its own). Otherwise clamped to its bounds and then to the viewport, so a window
 * remembered on a bigger screen still opens on this one. If even its minimum size does not fit the
 * viewport, it is unplaced rather than drawn off the edge.
 */
export function readWindowGeometry(
  key: string,
  bounds: WindowBounds,
  viewportWidth: number,
  viewportHeight: number,
): StoredWindow | null {
  const stored = readStored(key);
  if (!isPlaced(stored)) return null;

  const raw = stored as Partial<StoredWindow>;
  const fallback = { left: 0, top: 0, width: bounds.width.min, height: bounds.height.min };
  const clamped = clampToViewport(clampGeometry(raw, bounds, fallback), bounds, viewportWidth, viewportHeight);

  if (clamped.width > viewportWidth || clamped.height > viewportHeight) return null;

  return { ...clamped, maximised: raw.maximised === true };
}

export function writeWindowGeometry(key: string, bounds: WindowBounds, window: StoredWindow): void {
  const fallback = { left: 0, top: 0, width: bounds.width.min, height: bounds.height.min };

  writeStored(key, { ...clampGeometry(window, bounds, fallback), maximised: window.maximised });
}
