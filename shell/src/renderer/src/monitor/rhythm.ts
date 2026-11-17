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

/** The You node's place on the wall (canvas CSS px): at the right edge, half way down below the counts. */
export const YouR = 24;
export function youNodeAt(w: number, h: number): { x: number; y: number; r: number } {
  return { x: Math.max(YouR + 8, w - YouR - 22), y: Math.max(120, h / 2), r: YouR };
}

/** Whether a point (canvas CSS px) is on the You node, with a few pixels to spare. */
export function onYouNode(x: number, y: number, w: number, h: number): boolean {
  const n = youNodeAt(w, h);
  return Math.hypot(x - n.x, y - n.y) <= n.r + 6;
}

/** "Card 373 (B080 ...)", "[card 373] ..." -> 373; null when the text names no card. */
export function cardOf(text: string | null): number | null {
  if (!text) return null;
  const m = /\bcard\s*#?\s*(\d{1,6})\b/i.exec(text);
  return m ? Number(m[1]) : null;
}
