import type { CodeEntry, CodeFile, LineMarker, Marker, RemovedMarker } from './types';

/** Every line of the file view is exactly this tall (CodeLines.vue), so line n starts at (n - 1) * LINE_HEIGHT. */
export const LINE_HEIGHT = 20;

export const LIVE_MS = 60_000;
export const RECENT_MS = 30 * 60_000;

const RANK: Record<Marker, number> = { live: 3, recent: 2, branch: 1 };

/** The stronger of two markers (a folder shows the strongest of anything inside it). */
export function strongest(a: Marker | null, b: Marker | null): Marker | null {
  if (!a) return b;
  if (!b) return a;
  return RANK[a] >= RANK[b] ? a : b;
}

/**
 * The marker to draw for an entry now: the core's, except that a file's "live" turns "recent" once its last write is
 * over a minute old (the list is not fetched again every second). Hidden entries and links get none.
 */
export function shownMarker(e: CodeEntry, now: number): Marker | null {
  if (e.hidden || e.kind === 'link') return null;
  if (e.marker === 'live' && e.kind === 'file') {
    const t = Date.parse(e.mtime);
    if (Number.isFinite(t) && now - t > LIVE_MS) return 'recent';
  }
  return e.marker;
}

/** Folders first, then files and links, each by name (the core sends them so; kept here for safety). */
export function sortEntries(entries: CodeEntry[]): CodeEntry[] {
  const group = (e: CodeEntry) => (e.kind === 'dir' ? 0 : 1);
  return [...entries].sort((a, b) => group(a) - group(b) || a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
}

/** "Only changed files": the entries with a marker (folders carry the strongest inside them, so they stay). */
export function onlyChanged(entries: CodeEntry[], now: number): CodeEntry[] {
  return entries.filter((e) => shownMarker(e, now));
}

/**
 * How strong a recent line highlight is: 1 for the last minute, then fading linearly to 0.15 at 30 minutes, and 0
 * (none) after that.
 */
export function highlightStrength(at: string, now: number): number {
  const t = Date.parse(at);
  if (!Number.isFinite(t)) return 0;
  const age = Math.max(0, now - t);
  if (age <= LIVE_MS) return 1;
  if (age > RECENT_MS) return 0;
  return 1 - 0.85 * ((age - LIVE_MS) / (RECENT_MS - LIVE_MS));
}

export interface LineDecoration {
  /** 0..1, 0 = no recent highlight. */
  strength: number;
  /** Changed on the branch (a faint gutter mark). */
  branch: boolean;
}

/** The decoration of every line (1-based; index 0 unused). The newest recent marker on a line wins. */
export function lineDecorations(file: Pick<CodeFile, 'lines'>, lineCount: number, now: number): LineDecoration[] {
  const out: LineDecoration[] = Array.from({ length: lineCount + 1 }, () => ({ strength: 0, branch: false }));
  for (const m of file.lines) {
    const from = Math.max(1, m.from);
    const to = Math.min(lineCount, m.to);
    for (let n = from; n <= to; n++) {
      if (m.kind === 'branch') out[n].branch = true;
      else out[n].strength = Math.max(out[n].strength, highlightStrength(m.at, now));
    }
  }
  return out;
}

/** The recent removals still worth showing (within 30 minutes), keyed by the line they follow (0 = before line 1). */
export function removedByLine(removed: RemovedMarker[], now: number): Map<number, RemovedMarker[]> {
  const map = new Map<number, RemovedMarker[]>();
  for (const r of removed) {
    if (highlightStrength(r.at, now) <= 0) continue;
    const list = map.get(r.afterLine) ?? [];
    list.push(r);
    map.set(r.afterLine, list);
  }
  return map;
}

/** The first line of each change, in file order, without repeats: what Next and Previous step through. */
export function changeStops(file: Pick<CodeFile, 'lines' | 'removed'>, now: number): number[] {
  const stops = new Set<number>();
  for (const m of file.lines) if (m.kind === 'branch' || highlightStrength(m.at, now) > 0) stops.add(Math.max(1, m.from));
  for (const r of file.removed) if (highlightStrength(r.at, now) > 0) stops.add(Math.max(1, r.afterLine));
  return [...stops].sort((a, b) => a - b);
}

/** The line of the newest recent change (added, changed or removed), or null. */
export function latestChangeLine(file: Pick<CodeFile, 'lines' | 'removed'>): number | null {
  let best: { at: number; line: number } | null = null;
  const consider = (at: string, line: number) => {
    const t = Date.parse(at);
    if (Number.isFinite(t) && (!best || t > best.at)) best = { at: t, line };
  };
  for (const m of file.lines as LineMarker[]) if (m.kind !== 'branch') consider(m.at, Math.max(1, m.from));
  for (const r of file.removed) consider(r.at, Math.max(1, r.afterLine));
  return best ? (best as { line: number }).line : null;
}

/** The next stop after `line` (or the first), and the previous before it (or the last). */
export function nextStop(stops: number[], line: number): number | null {
  return stops.find((s) => s > line) ?? stops[0] ?? null;
}
export function previousStop(stops: number[], line: number): number | null {
  for (let i = stops.length - 1; i >= 0; i--) if (stops[i] < line) return stops[i];
  return stops[stops.length - 1] ?? null;
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1).replace(/\.0$/, '')} kB`;
  return `${(bytes / (1024 * 1024)).toFixed(1).replace(/\.0$/, '')} MB`;
}

/** The quiet "edited 2 min ago" of the listing. */
export function editedAgo(mtime: string, now: number): string {
  const t = Date.parse(mtime);
  if (!Number.isFinite(t)) return '';
  const s = Math.max(0, Math.round((now - t) / 1000));
  if (s < 60) return 'edited just now';
  const min = Math.round(s / 60);
  if (min < 60) return `edited ${min} min ago`;
  const h = Math.round(min / 60);
  if (h < 24) return `edited ${h} h ago`;
  const d = Math.round(h / 24);
  if (d < 31) return `edited ${d} day${d === 1 ? '' : 's'} ago`;
  return `edited ${new Date(t).toLocaleDateString()}`;
}

/** The folder of a clone-relative path ("" for a file at the root). */
export function folderOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

/** True when `path` is inside folder `dir` (directly or deeper); "" holds everything. */
export function isUnder(path: string, dir: string): boolean {
  return dir === '' || path === dir || path.startsWith(`${dir}/`);
}

const IMAGE_TYPES: Record<string, string> = {
  png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', svg: 'image/svg+xml',
};
/** The image's MIME type by name (an SVG is shown as an <img>, never inline, so no script in it runs). */
export function imageMime(path: string): string {
  const ext = path.slice(path.lastIndexOf('.') + 1).toLowerCase();
  return IMAGE_TYPES[ext] ?? 'application/octet-stream';
}

/** "This file isn't shown (binary / 3.4 MB)". */
export function notShownText(file: Pick<CodeFile, 'notShownReason' | 'size'>): string {
  const why = file.notShownReason === 'secrets' ? 'may hold secrets'
    : file.notShownReason === 'tooLarge' ? 'too large' : 'binary';
  return `This file isn't shown (${why} / ${formatSize(file.size)})`;
}
