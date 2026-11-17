/**
 * THE MEMBERS' STEPS: what a member does between its bus messages - each file it reads or changes, each command it
 * runs, each search, each web fetch, each bus call - as the core reads them from its runs' transcripts
 * (monitor.steps). A step is drawn as a small icon that rises off the member and fades; the file it reads or
 * changes takes its node on the team's code map (codemap.ts), tied to the member by a line.
 * The icons are drawn on the canvas (icons.ts), so nothing is downloaded.
 */

export type StepKind = 'read' | 'edit' | 'create' | 'run' | 'search' | 'web' | 'bus' | 'think' | 'say' | 'agent' | 'plan' | 'skill' | 'tool';

/** One step as monitor.steps returns it. */
export interface StepRow {
  id: number; at: string; team: string; member: string; run: number; kind: StepKind; tool: string; target: string | null;
  /** A command's exit code, when the feed knows it (a finished command); absent or null when not. */
  exit?: number | null;
}

export const StepKinds: readonly StepKind[] = ['read', 'edit', 'create', 'run', 'search', 'web', 'bus', 'think', 'say', 'agent', 'plan', 'skill', 'tool'];

/** Each kind's colour on the wall and the words for it (the legend, the hover, the member's "now" line). */
export const StepLook: Record<StepKind, { color: string; doing: string; done: string }> = {
  read: { color: '#7dd3fc', doing: 'reading', done: 'read' },
  edit: { color: '#a3e635', doing: 'editing', done: 'edited' },
  create: { color: '#2dd4bf', doing: 'writing', done: 'wrote' },
  run: { color: '#e879f9', doing: 'running', done: 'ran' },
  search: { color: '#93c5fd', doing: 'searching', done: 'searched' },
  web: { color: '#818cf8', doing: 'on the web', done: 'fetched' },
  bus: { color: '#38bdf8', doing: 'on the bus', done: 'called' },
  think: { color: '#cbd5e1', doing: 'thinking', done: 'thought' },
  say: { color: '#cbd5e1', doing: 'writing a reply', done: 'replied' },
  agent: { color: '#fcd34d', doing: 'delegating', done: 'delegated' },
  plan: { color: '#fcd34d', doing: 'planning', done: 'planned' },
  skill: { color: '#fcd34d', doing: 'reading a skill', done: 'read a skill' },
  tool: { color: '#94a3b8', doing: 'using a tool', done: 'used' },
};

/** A step as the wire sent it, or null when it is not one: the Monitor never trusts the shape. */
export function readStep(raw: unknown): StepRow | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const s = raw as Record<string, unknown>;
  const text = (v: unknown) => (typeof v === 'string' ? v : '');
  if (typeof s.id !== 'number' || !text(s.team) || !text(s.member) || !text(s.at)) return null;
  const kind = (StepKinds as readonly string[]).includes(text(s.kind)) ? (s.kind as StepKind) : 'tool';
  return {
    id: s.id, at: text(s.at), team: text(s.team), member: text(s.member), run: typeof s.run === 'number' ? s.run : 0,
    kind, tool: text(s.tool), target: typeof s.target === 'string' && s.target ? s.target : null,
    ...(typeof s.exit === 'number' && Number.isInteger(s.exit) ? { exit: s.exit } : {}),
  };
}

/** Whether a step changes a file (and so leaves a node on the wall). */
export const changesFile = (s: Pick<StepRow, 'kind' | 'target'>) => (s.kind === 'edit' || s.kind === 'create') && !!s.target;

/** The last part of a path: `web/src/lib/parser.ts` -> `parser.ts`. */
export function fileName(path: string): string {
  const parts = path.split(/[\\/]/).filter((p) => p !== '');
  return parts.at(-1) ?? path;
}

/** The short note beside a step's icon: the command, the pattern, the host, who a tell went to; null for none. */
export function stepCaption(s: Pick<StepRow, 'kind' | 'tool' | 'target'>, max = 30): string | null {
  const clip = (t: string) => (t.length <= max ? t : t.slice(0, max - 1) + '…');
  switch (s.kind) {
    case 'run': return s.target ? clip(s.target) : null;
    case 'search': return s.target ? clip(s.target) : null;
    case 'read': return s.target ? clip(fileName(s.target)) : null;
    case 'web': {
      if (!s.target) return null;
      try { return clip(new URL(s.target).host); } catch { return clip(s.target); }
    }
    case 'bus': return clip(s.target ? `${s.tool} → ${s.target}` : s.tool.replace(/_/g, ' '));
    case 'agent': return s.target ? clip(s.target) : null;
    case 'skill': return s.target ? clip(s.target) : null;
    default: return null;
  }
}

/** What a member is doing, in words, for the line under its name: "editing parser.ts", "running npm test". */
export function stepDoing(s: Pick<StepRow, 'kind' | 'tool' | 'target'>): string {
  const look = StepLook[s.kind];
  const what = changesFile(s) || s.kind === 'read' ? fileName(s.target ?? '') : stepCaption(s, 26);
  return what ? `${look.doing} ${what}` : look.doing;
}
