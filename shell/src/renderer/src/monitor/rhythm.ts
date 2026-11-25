import type { Effect } from './model';

/**
 * THE SCENE'S PURE RULES, kept out of scene.ts so they are tested without a canvas:
 * - density: how many effects the wall draws in one frame (calm / normal / lively), the most telling first;
 * - focus: which team the wall zooms to, and how dim the rest is;
 * - the work rhythm: how fast a member's disc pulses for its step rate, how full a command's ring is;
 * - the You node: where it sits at the wall's edge and what a click hits.
 */

export type Density = 'calm' | 'normal' | 'lively';
export const Densities: readonly Density[] = ['calm', 'normal', 'lively'];

/**
 * Per density: effects drawn per frame, step icons on one member, floating lines in all; and for the code maps
 * (codemap.ts): file nodes in all the maps together (never more than its MaxMapNodes, 240) and commit dots resting at
 * one hub.
 */
export interface DensityCaps { effects: number; stepsEach: number; floats: number; mapNodes: number; commitsEach: number }
export const DensityCaps: Record<Density, DensityCaps> = {
  calm: { effects: 12, stepsEach: 1, floats: 3, mapNodes: 72, commitsEach: 3 },
  normal: { effects: 48, stepsEach: 3, floats: 10, mapNodes: 240, commitsEach: 8 },
  lively: { effects: 140, stepsEach: 4, floats: 24, mapNodes: 240, commitsEach: 8 },
};

export function isDensity(v: unknown): v is Density {
  return typeof v === 'string' && (Densities as readonly string[]).includes(v);
}

/** Which effects tell most, kept first when a frame is over its cap: what flies between members, then what ends. */
const Rank: Record<Effect['kind'], number> = { you: 0, comet: 0, wfdone: 1, burst: 1, merge: 1, push: 1, commit: 1, ripple: 2, float: 3, step: 4 };

/** The node an effect belongs to (a comet: where it starts; a commit: its member, or its hub), for its team. */
export function effectNode(e: Effect): string {
  switch (e.kind) {
    case 'comet': return e.from;
    case 'you': return e.from;
    case 'wfdone': return e.key;
    case 'commit': return e.node ?? `t:${e.team.toLowerCase()}`;
    case 'merge': case 'push': return `t:${e.team.toLowerCase()}`;
    default: return e.node;
  }
}

/** The team (lower case) of a scene key: t:team, m:team/member, w:team, wf:team/id. */
export function teamOfKey(key: string): string | null {
  const m = /^(?:t|m|w|wf):([^/]*)/.exec(key);
  return m ? m[1]! : null;
}

/**
 * The effects drawn this frame, in their order: at most the density's cap, the most telling and then the newest kept;
 * at most `stepsEach` step icons on one member, `commitsEach` commit dots on one team and `floats` floating lines. With a
 * focused team (lower case), only its effects (its commits, merges and pushes too), and comets that touch it.
 */
export function capEffects(effects: readonly Effect[], density: Density, focus: string | null = null): Effect[] {
  const caps = DensityCaps[density];
  const inFocus = (e: Effect) => {
    if (focus === null) return true;
    if (teamOfKey(effectNode(e)) === focus) return true;
    return e.kind === 'comet' && teamOfKey(e.to) === focus;
  };
  const order = effects.map((e, i) => ({ e, i })).filter(({ e }) => inFocus(e));
  order.sort((a, b) => Rank[a.e.kind] - Rank[b.e.kind] || b.i - a.i);
  const keep = new Set<number>();
  const steps = new Map<string, number>();
  const commits = new Map<string, number>();
  let floats = 0;
  for (const { e, i } of order) {
    if (keep.size >= caps.effects) break;
    if (e.kind === 'step') {
      const n = steps.get(e.node) ?? 0;
      if (n >= caps.stepsEach) continue;
      steps.set(e.node, n + 1);
    } else if (e.kind === 'commit') {
      const team = e.team.toLowerCase();
      const n = commits.get(team) ?? 0;
      if (n >= caps.commitsEach) continue;
      commits.set(team, n + 1);
    } else if (e.kind === 'float') {
      if (floats >= caps.floats) continue;
      floats++;
    }
    keep.add(i);
  }
  return effects.filter((_, i) => keep.has(i));
}

/** How strongly a node shows with a team focused: fully in the team (or with no focus), faintly elsewhere. */
export const FocusDim = 0.16;
export function focusAlpha(key: string, focus: string | null): number {
  if (focus === null) return 1;
  return teamOfKey(key) === focus ? 1 : FocusDim;
}

// ---------- the work rhythm ----------

/** A member's disc pulse, by its steps in the last minute: none when it is quiet, faster as it works harder. */
export function pulsePeriodMs(stepsPerMinute: number): number | null {
  if (!(stepsPerMinute > 0)) return null;
  return Math.max(450, Math.min(2600, 2600 - Math.log2(1 + stepsPerMinute) * 380));
}

/** How full a command's ring is `ms` after it started, its length unknown: quickly at first, never quite full. */
export const RunFillMs = 20_000;
export function runFill(ms: number): number {
  if (!(ms > 0)) return 0;
  return Math.min(0.97, 1 - Math.exp(-ms / RunFillMs));
}

// ---------- the You node ----------

/** A rectangle on the wall (canvas CSS px) the You node keeps clear of: the Legend button, the phone's control row. */
export interface KeepOut { left: number; top: number; right: number; bottom: number }

export const YouR = 24;
/** The gap kept between the You node and a keep-out rect, and between it and the wall's edge. */
const YouGap = 6, YouEdge = 4;

/** How far a node at (x, y) is from a rect's nearest point (0 when its centre is on the rect). */
function distTo(x: number, y: number, k: KeepOut): number {
  return Math.hypot(Math.max(k.left - x, 0, x - k.right), Math.max(k.top - y, 0, y - k.bottom));
}
function onRect(x: number, y: number, k: KeepOut, slop: number): boolean {
  return x >= k.left - slop && x <= k.right + slop && y >= k.top - slop && y <= k.bottom + slop;
}
function clearOf(x: number, y: number, r: number, keepOut: readonly KeepOut[], gap = YouGap): boolean {
  return keepOut.every((k) => distTo(x, y, k) >= r + gap);
}

let lastAt: { key: string; at: { x: number; y: number; r: number } } | null = null;

/**
 * The You node's place on the wall (canvas CSS px): at the right edge, half way down below the counts (at least 120 px
 * down), always inside the wall and clear of the keep-out rects (on a short phone wall the controls row with
 * Legend covers the bottom). The rects come most telling first: the Legend button, then the controls row, then the
 * open legend. When its place is covered it moves the least it can: above, below or left of what covers it, else to
 * the nearest clear spot on the wall; then the same with no gap to the edge or the rects. When no spot on the wall is
 * clear of them all (a phone wall under ~104 px: the row takes all but its top 47 px), it never goes back to its old
 * place: it stays clear of the first rect (Legend), keeps its centre off every rect (a tap on it reaches the wall, not
 * a button) and overlaps the others the least it can. A wall too small to hold it keeps it inside, centred.
 */
export function youNodeAt(w: number, h: number, keepOut: readonly KeepOut[] = []): { x: number; y: number; r: number } {
  const r = YouR;
  const xMin = r + YouEdge, xMax = Math.max(xMin, w - r - YouEdge);
  const yMin = r + YouEdge, yMax = Math.max(yMin, h - r - YouEdge);
  const x0 = Math.min(xMax, Math.max(r + 8, w - r - 22));
  const y0 = h < 2 * yMin ? h / 2 : Math.min(yMax, Math.max(yMin, Math.max(120, h / 2)));
  if (keepOut.length === 0 || clearOf(x0, y0, r, keepOut)) return { x: x0, y: y0, r };
  const key = `${w},${h},${keepOut.map((k) => `${k.left},${k.top},${k.right},${k.bottom}`).join(';')}`;
  if (lastAt?.key === key) return lastAt.at;
  const d = (x: number, y: number) => Math.hypot(x - x0, y - y0);
  // Pass 1 with the edge and gap margins, pass 2 without them (the node may touch the wall's edge and the rects).
  let best: { x: number; y: number } | null = null;
  for (const [edge, gap] of [[YouEdge, YouGap], [0, 0]] as const) {
    if (h < 2 * (r + edge)) continue;
    const lx = r + edge, hx = Math.max(lx, w - r - edge), ly = r + edge, hy = Math.max(ly, h - r - edge);
    const consider = (x: number, y: number) => {
      if (x < lx || x > hx || y < ly || y > hy || !clearOf(x, y, r, keepOut, gap)) return;
      if (!best || d(x, y) < d(best.x, best.y)) best = { x, y };
    };
    const reach = r + gap + 0.5;
    const cy = (y: number) => Math.min(hy, Math.max(ly, y));
    for (const k of keepOut) {
      consider(x0, cy(k.top - reach));
      consider(x0, cy(k.bottom + reach));
      consider(k.left - reach, y0);
      consider(k.left - reach, cy(k.top - reach));
    }
    if (!best) for (let y = ly; y <= hy; y += 2) for (let x = hx; x >= lx; x -= 2) consider(x, y);
    if (best) break;
  }
  // No spot clear of them all: clear of the first rect (Legend) with a small gap, the centre off every rect, inside the
  // wall; the least overlap with the others (the more telling weighs more), then the nearest to its own place.
  if (!best && h >= 2 * r) {
    const hard = keepOut[0]!, soft = keepOut.slice(1);
    let bestCost = Infinity;
    const lx = r, hx = Math.max(lx, w - r), ly = r, hy = Math.max(ly, h - r);
    for (let y = ly; y <= hy; y += 1) for (let x = hx; x >= lx; x -= 2) {
      if (distTo(x, y, hard) < r + 2 || keepOut.some((k) => onRect(x, y, k, 4))) continue;
      const cost = soft.reduce((s, k, i) => s + Math.max(0, r - distTo(x, y, k)) * (soft.length - i), 0) * 1000 + d(x, y);
      if (cost < bestCost) { bestCost = cost; best = { x, y }; }
    }
  }
  // Still nothing (a wall too small, or Legend everywhere): the last (least telling) rect is given up, then the next.
  const at = best ? { x: (best as { x: number }).x, y: (best as { y: number }).y, r } : youNodeAt(w, h, keepOut.slice(0, -1));
  lastAt = { key, at };
  return at;
}

/** Whether a point (canvas CSS px) is on the You node, with a few pixels to spare. */
export function onYouNode(x: number, y: number, w: number, h: number, keepOut: readonly KeepOut[] = []): boolean {
  const n = youNodeAt(w, h, keepOut);
  return Math.hypot(x - n.x, y - n.y) <= n.r + 6;
}

/** "Card 4373 (...)", "[card 4373] ..." -> 4373; null when the text names no card. */
export function cardOf(text: string | null): number | null {
  if (!text) return null;
  const m = /\bcard\s*#?\s*(\d{1,6})\b/i.exec(text);
  return m ? Number(m[1]) : null;
}
