import { computed, ref } from 'vue';

/**
 * The size of every team tile in the Teams list: one width and height for all tiles (and the "New team" tile), dragged
 * by the grip on any of them. Same pattern as agentOutputHeight.ts: a reactive shared value, a preview while dragging,
 * saved on release (a blocked storage just means the default next time).
 */
export const TILE_MIN_W = 280;
/** A sanity cap for a stored width; in practice a tile is as wide as the content area allows (availableWidth). */
export const TILE_MAX_W = 4096;
export const TILE_MIN_H = 200;
export const TILE_MAX_H = 640;
export const TILE_DEFAULT = { w: 320, h: 260 } as const;
const KEY = 'yawble.teamTiles';

export interface TileSize { w: number; h: number }
const clamp = (n: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, Math.round(Number.isFinite(n) ? n : lo)));
export const clampTile = (s: TileSize): TileSize => ({ w: clamp(s.w, TILE_MIN_W, TILE_MAX_W), h: clamp(s.h, TILE_MIN_H, TILE_MAX_H) });

function load(): TileSize {
  try {
    const p = JSON.parse(localStorage.getItem(KEY) ?? 'null') as Partial<TileSize> | null;
    if (p && Number.isFinite(p.w) && Number.isFinite(p.h)) return clampTile({ w: p.w as number, h: p.h as number });
  } catch { /* default */ }
  return { ...TILE_DEFAULT };
}

/** The width the tiles can really have: the content area's (Infinity until it is measured). Set by the Teams list. */
export const availableWidth = ref(Infinity);

export const tileSize = ref<TileSize>(load());

/** Clamped, and never wider than the content area once it is measured: a tile can be widened up to the whole width. */
const fit = (s: TileSize): TileSize => {
  const c = clampTile(s);
  return Number.isFinite(availableWidth.value) && availableWidth.value >= TILE_MIN_W ? { ...c, w: Math.min(c.w, Math.round(availableWidth.value)) } : c;
};

/** Sets the shared size (clamped) without saving: used while dragging. */
export function previewTileSize(s: TileSize): void { tileSize.value = fit(s); }

/** Sets the shared size (clamped) and keeps it. */
export function setTileSize(s: TileSize): void {
  tileSize.value = fit(s);
  try { localStorage.setItem(KEY, JSON.stringify(tileSize.value)); } catch { /* not kept */ }
}

/** Forgets the saved size and goes back to the default. */
export function resetTileSize(): void {
  tileSize.value = { ...TILE_DEFAULT };
  try { localStorage.removeItem(KEY); } catch { /* ignore */ }
}

/** Re-reads the saved size (a fresh start of the page; tests use it to show the size survives a remount). */
export function reloadTileSize(): void { tileSize.value = load(); }

/** The shared width as rendered: never wider than the content area. Dragging and arrow keys start from this. */
export const renderedWidth = computed(() => Math.round(Math.min(tileSize.value.w, availableWidth.value)));
