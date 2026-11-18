import type { Effect } from './model';

/**
 * THE SCENE'S PURE RULES (B080), kept out of scene.ts so they are tested without a canvas:
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

function clearOf(x: number, y: number, r: number, keepOut: readonly KeepOut[]): boolean {
  const reach = r + YouGap;
  return keepOut.every((k) => {
    const dx = Math.max(k.left - x, 0, x - k.right), dy = Math.max(k.top - y, 0, y - k.bottom);
    return Math.hypot(dx, dy) >= reach;
  });
}

let lastAt: { key: string; at: { x: number; y: number; r: number } } | null = null;

/**
 * The You node's place on the wall (canvas CSS px): at the right edge, half way down below the counts (at least 120 px
 * down), always inside the wall and clear of the keep-out rects (card 416: on a short phone wall the controls row with
 * Legend covers the bottom). When its place is covered it moves the least it can: above, below or left of what covers
 * it, else to the nearest clear spot on the wall. The rects come most telling first: when no spot clears them all, the
 * last ones are given up in turn. A wall too small to hold it keeps it inside, centred.
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
  const inside = (x: number, y: number) => x >= xMin && x <= xMax && y >= yMin && y <= yMax;
  const d = (x: number, y: number) => Math.hypot(x - x0, y - y0);
  let best: { x: number; y: number } | null = null;
  const consider = (x: number, y: number) => {
    if (!inside(x, y) || !clearOf(x, y, r, keepOut)) return;
    if (!best || d(x, y) < d(best.x, best.y)) best = { x, y };
  };
  const reach = r + YouGap + 0.5;
  const cy = (y: number) => Math.min(yMax, Math.max(yMin, y));
  for (const k of keepOut) {
    consider(x0, cy(k.top - reach));
    consider(x0, cy(k.bottom + reach));
    consider(k.left - reach, y0);
    consider(k.left - reach, cy(k.top - reach));
  }
  if (!best) for (let y = yMin; y <= yMax; y += 4) for (let x = xMax; x >= xMin; x -= 4) consider(x, y);
  // No clear spot: the last (least telling) rect is given up, then the next, down to the node's own place.
  const at = best ? { x: (best as { x: number }).x, y: (best as { y: number }).y, r } : youNodeAt(w, h, keepOut.slice(0, -1));
  lastAt = { key, at };
  return at;
}

/** Whether a point (canvas CSS px) is on the You node, with a few pixels to spare. */
export function onYouNode(x: number, y: number, w: number, h: number, keepOut: readonly KeepOut[] = []): boolean {
  const n = youNodeAt(w, h, keepOut);
  return Math.hypot(x - n.x, y - n.y) <= n.r + 6;
}

/** "Card 373 (B080 ...)", "[card 373] ..." -> 373; null when the text names no card. */
export function cardOf(text: string | null): number | null {
  if (!text) return null;
  const m = /\bcard\s*#?\s*(\d{1,6})\b/i.exec(text);
  return m ? Number(m[1]) : null;
}
