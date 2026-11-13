import { reactive } from 'vue';
import type { AgentCard } from '../../../shared/api';
import sheetUrl from '../assets/avatars.jpg';

/**
 * THE MEMBERS' FACES: one sheet of square tiles (assets/avatars.jpg, 10 x 10), each member shown as one tile of it on
 * the team page, the Board and the Monitor. A member's tile is derived, never stored: a hash of its team's name and
 * its own name, so it is the same on every view and after a restart. Replacing the sheet with another image of the
 * same tile size deals the members out again; the columns and rows are read from the image's own size.
 */
export const AvatarSheetUrl = sheetUrl;

/** The side of one tile in the sheet, in the image's own pixels. */
export const AvatarTilePx = 102.4;

/** How far in from each edge of its tile a picture is cut, as a fraction of the tile's side (hides the gutters). */
export const AvatarInset = 0.05;

export interface AvatarSheet { image: HTMLImageElement | null; cols: number; rows: number }

/** The sheet once it has loaded; cols 0 until then, and for a sheet that failed to load (everyone shows initials). */
export const avatarSheet = reactive<AvatarSheet>({ image: null, cols: 0, rows: 0 });

let loading: Promise<void> | null = null;

export function loadAvatarSheet(url = AvatarSheetUrl): Promise<void> {
  if (loading) return loading;
  loading = new Promise<void>((resolve) => {
    if (typeof Image === 'undefined') return resolve();
    const image = new Image();
    image.onload = () => { setAvatarSheet(image, image.naturalWidth, image.naturalHeight); resolve(); };
    image.onerror = () => resolve();
    image.src = url;
  });
  return loading;
}

/** Takes a loaded sheet: its columns and rows from its size. Exposed for tests. */
export function setAvatarSheet(image: HTMLImageElement | null, width: number, height: number): void {
  avatarSheet.image = image;
  avatarSheet.cols = Math.max(0, Math.round(width / AvatarTilePx));
  avatarSheet.rows = Math.max(0, Math.round(height / AvatarTilePx));
}

/** FNV-1a over the text: small, fast and spread well enough for dealing out tiles. */
function hash(text: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

/** The tile a member wears among `count` tiles; names are matched without case. -1 when there are none. */
export function avatarIndex(team: string, member: string, count: number): number {
  if (count <= 0) return -1;
  return hash(`${team.toLowerCase()}/${member.toLowerCase()}`) % count;
}

export function tileOf(index: number, cols: number): { col: number; row: number } {
  return { col: index % cols, row: Math.floor(index / cols) };
}

/** The CSS that shows one tile, cut in by the inset, filling an element of any size. */
export function tileStyle(index: number, cols: number, rows: number, url = AvatarSheetUrl): Record<string, string> {
  const { col, row } = tileOf(index, cols);
  const tile = 1 / (1 - 2 * AvatarInset);
  const at = (place: number, count: number) => {
    const room = count * tile - 1;
    return room > 0 ? ((place + AvatarInset) * tile / room) * 100 : 0;
  };
  return {
    backgroundImage: `url("${url}")`,
    backgroundSize: `${cols * tile * 100}% ${rows * tile * 100}%`,
    backgroundPosition: `${at(col, cols)}% ${at(row, rows)}%`,
  };
}

/** One tile's picture in the sheet's own pixels, for a canvas to draw. */
export function tileSource(index: number, sheet: { cols: number; rows: number; width: number; height: number }) {
  const { col, row } = tileOf(index, sheet.cols);
  const tw = sheet.width / sheet.cols;
  const th = sheet.height / sheet.rows;
  return { sx: (col + AvatarInset) * tw, sy: (row + AvatarInset) * th, sw: tw * (1 - 2 * AvatarInset), sh: th * (1 - 2 * AvatarInset) };
}

/** The tile a member wears on the sheet as loaded, or -1 while there is none. */
export function memberAvatar(team: string, member: string): number {
  return avatarIndex(team, member, avatarSheet.cols * avatarSheet.rows);
}

/**
 * A name's initials: the first letter of the first and last words ("Developer Priya" is DP), a camel-case name split
 * at its capitals ("DeveloperPriya" is DP), one letter for one word, ? for an empty name.
 */
export function initials(name: string): string {
  const words = name.replace(/([a-z])([A-Z])/g, '$1 $2').split(/[\s_\-.]+/).filter((w) => w !== '');
  if (words.length === 0) return '?';
  if (words.length === 1) return words[0]!.slice(0, 1).toUpperCase();
  return (words[0]!.slice(0, 1) + words[words.length - 1]!.slice(0, 1)).toUpperCase();
}

/** A member's state as its ring shows it, loudest first. */
export type MemberState = 'decision' | 'blocked' | 'failed' | 'running' | 'waiting' | 'idle';

export function memberStateOf(card: Pick<AgentCard, 'state' | 'attention'>): MemberState {
  if (card.attention === 'decision') return 'decision';
  if (card.attention === 'blocked') return 'blocked';
  if (card.attention === 'failed') return 'failed';
  if (card.state === 'running') return 'running';
  if (card.state === 'queued') return 'waiting';
  return 'idle';
}

export const MemberStateLabel: Record<MemberState, string> = {
  decision: 'Needs you', blocked: 'Blocked', failed: 'Last run failed', running: 'Running', waiting: 'Waiting to start', idle: 'Idle',
};

/**
 * A member's display name from its id: the words of a camel-case id split apart ("DeveloperPriya" is "Developer
 * Priya"); "Manager" stays as it is.
 */
export function displayName(member: string): string {
  return member.replace(/([a-z])([A-Z])/g, '$1 $2');
}
