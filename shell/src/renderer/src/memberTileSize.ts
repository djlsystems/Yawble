import { computed, ref } from 'vue';
import type { GripStore } from './gripStore';

/**
 * The size of every member tile on a team page (B076): one width and height for all of them (and the "New member"
 * tile), dragged by the width and height handles in the gaps. The team page's own size, kept apart from the Teams
 * list's (teamTileSize.ts) the same way: a reactive shared value, a preview while dragging, saved on release in this
 * viewer's storage (the desktop app and every Cloud Link browser keep their own; a blocked storage just means the
 * default next time).
 *
 * The default is two tiles per row at a 1280 px window: the content area is 1280 - 268 (sidebar) - 48 (padding) = 964
 * px, and 2 x 460 + 14 (gap) = 934 fits beside a scrollbar where three (1408) do not.
 */
export const MEMBER_TILE_MIN_W = 280;
export const MEMBER_TILE_MAX_W = 1200;
export const MEMBER_TILE_MIN_H = 200;
export const MEMBER_TILE_MAX_H = 900;
export const MEMBER_TILE_DEFAULT = { w: 460, h: 340 } as const;
const KEY = 'yawble.memberTiles';

export interface MemberTileSize { w: number; h: number }
const clamp = (n: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, Math.round(Number.isFinite(n) ? n : lo)));
export const clampMemberTile = (s: MemberTileSize): MemberTileSize => ({
  w: clamp(s.w, MEMBER_TILE_MIN_W, MEMBER_TILE_MAX_W), h: clamp(s.h, MEMBER_TILE_MIN_H, MEMBER_TILE_MAX_H),
});

function load(): MemberTileSize {
  try {
    const p = JSON.parse(localStorage.getItem(KEY) ?? 'null') as Partial<MemberTileSize> | null;
    if (p && Number.isFinite(p.w) && Number.isFinite(p.h)) return clampMemberTile({ w: p.w as number, h: p.h as number });
  } catch { /* default */ }
  return { ...MEMBER_TILE_DEFAULT };
}

export const memberTileSize = ref<MemberTileSize>(load());

/** Sets the shared size (clamped) without saving: used while dragging. */
export function previewMemberTileSize(s: MemberTileSize): void { memberTileSize.value = clampMemberTile(s); }

/** Sets the shared size (clamped) and keeps it. */
export function setMemberTileSize(s: MemberTileSize): void {
  memberTileSize.value = clampMemberTile(s);
  try { localStorage.setItem(KEY, JSON.stringify(memberTileSize.value)); } catch { /* not kept */ }
}

/** Forgets the saved size and goes back to the default. */
export function resetMemberTileSize(): void {
  memberTileSize.value = { ...MEMBER_TILE_DEFAULT };
  try { localStorage.removeItem(KEY); } catch { /* ignore */ }
}

/** Re-reads the saved size (a fresh start of the page; tests use it to show the size survives a remount). */
export function reloadMemberTileSize(): void { memberTileSize.value = load(); }

/** The width the tiles can really have: the member grid's (Infinity until it is measured). Set by the team page. */
export const memberAvailableWidth = ref(Infinity);
/** The shared width as rendered: never wider than the grid. Dragging and arrow keys start from this. */
export const memberRenderedWidth = computed(() => Math.round(Math.min(memberTileSize.value.w, memberAvailableWidth.value)));

/** What the team page's TileGrip handles resize (data-test member-tile-handle-w / -h). */
export const memberTilesStore: GripStore = {
  noun: 'member tiles', testId: 'member-tile', size: memberTileSize, width: memberRenderedWidth,
  limits: { minW: MEMBER_TILE_MIN_W, maxW: MEMBER_TILE_MAX_W, minH: MEMBER_TILE_MIN_H, maxH: MEMBER_TILE_MAX_H },
  preview: previewMemberTileSize, commit: () => setMemberTileSize(memberTileSize.value), reset: resetMemberTileSize, set: setMemberTileSize,
};
