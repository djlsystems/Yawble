/**
 * THE KANBAN LANE WIDTH: one width every lane shares, set by dragging a divider between two lanes,
 * and remembered by this browser.
 *
 * One width rather than one per lane, because the lanes are the same kind of thing and a board whose
 * columns differ reads as if some mattered more. Remembered per browser, not per person: it is a
 * matter of this screen's size.
 *
 * Storage can throw (private mode, blocked site data) or hold something this code did not write, so
 * every read falls back to the default and every write is best effort.
 */

/** The width a lane starts at, in CSS pixels. */
export const DefaultLaneWidth = 300;

/** The narrowest a lane may be dragged to: a card's title and its badges still fit. */
export const MinLaneWidth = 220;

/** The widest a lane may be dragged to. */
export const MaxLaneWidth = 640;

/** One keyboard step on the divider, in CSS pixels. */
export const LaneWidthStep = 16;

/** The `localStorage` key the board view's width is kept under (the key it has always had). */
export const LaneWidthStorageKey = 'kanban.laneWidth';

/** The `localStorage` key the swimlanes view's width is kept under: each view keeps its own width. */
export const SwimlaneWidthStorageKey = 'kanban.swimlanes.laneWidth';

/** The key a view's width is kept under: the board's as it always was, the swimlanes' its own. */
export function laneWidthStorageKey(view: 'board' | 'swimlanes'): string {
  return view === 'swimlanes' ? SwimlaneWidthStorageKey : LaneWidthStorageKey;
}

/** A width brought inside the allowed range, rounded to whole pixels; not a number reads as the default. */
export function clampLaneWidth(width: number): number {
  if (!Number.isFinite(width)) return DefaultLaneWidth;
  return Math.round(Math.min(MaxLaneWidth, Math.max(MinLaneWidth, width)));
}

/** The remembered width, or the default when there is none or it cannot be read. */
export function loadLaneWidth(
  storage: Pick<Storage, 'getItem'> | undefined = safeStorage(),
  key: string = LaneWidthStorageKey,
): number {
  try {
    const raw = storage?.getItem(key);
    if (raw === null || raw === undefined || raw.trim() === '') return DefaultLaneWidth;
    return clampLaneWidth(Number(raw));
  } catch {
    return DefaultLaneWidth;
  }
}

/** Remembers `width` (clamped); nothing happens when storage is not available. */
export function saveLaneWidth(
  width: number,
  storage: Pick<Storage, 'setItem'> | undefined = safeStorage(),
  key: string = LaneWidthStorageKey,
): void {
  try {
    storage?.setItem(key, String(clampLaneWidth(width)));
  } catch {
    // Blocked or full storage: the width lasts until the page is reloaded.
  }
}

function safeStorage(): Storage | undefined {
  try {
    return typeof window === 'undefined' ? undefined : window.localStorage;
  } catch {
    return undefined;
  }
}
