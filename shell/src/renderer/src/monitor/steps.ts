/**
 * THE MEMBERS' STEPS: what a member does between its bus messages - each file it reads or changes, each command it
 * runs, each search, each web fetch, each bus call - as the core reads them from its runs' transcripts
 * (monitor.steps). A step is drawn as a small icon that rises off the member and fades; the file it changes stays
 * as a labelled node beside the member, tied to it by a line, for FileTtlMs after its last touch (FileTrail).
 * The icons are drawn on the canvas (icons.ts), so nothing is downloaded.
 */

export type StepKind = 'read' | 'edit' | 'create' | 'run' | 'search' | 'web' | 'bus' | 'think' | 'say' | 'agent' | 'plan' | 'skill' | 'tool';

/** One step as monitor.steps returns it. */
export interface StepRow {
  id: number; at: string; team: string; member: string; run: number; kind: StepKind; tool: string; target: string | null;
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

// --- the files a member changes, in display time ---------------------------------------------------------------

/** How long a changed file stays beside its member after its last touch, how it fades in and out, and its pulse. */
export const FileTtlMs = 30_000;
export const FileInMs = 300;
export const FileFadeMs = 8_000;
export const FilePulseMs = 1_200;
/** At most this many files are nodes beside one member; the rest are counted on a "+N" node. */
export const MaxFileNodes = 6;

export interface FileMark {
  member: string; path: string; name: string; kind: 'edit' | 'create';
  /** Display times (performance.now()) of its first and latest touch. */
  first: number; last: number; touches: number;
  /** Its place beside the member (0..MaxFileNodes-1), or -1 while it is only counted on the "+N" node. */
  slot: number;
}

/** How strongly a file shows at `now`: in quickly, out over its last seconds; under reduced motion simply there. */
export function fileAlpha(f: Pick<FileMark, 'first' | 'last'>, now: number, reduced = false): number {
  const left = f.last + FileTtlMs - now;
  if (left <= 0) return 0;
  if (reduced) return 1;
  return Math.max(0, Math.min(1, (now - f.first) / FileInMs, left / FileFadeMs));
}

/**
 * THE FILES BESIDE EACH MEMBER. A file keeps its place while it lives, so a file touched again pulses where it is
 * rather than moving; a member that changes many files at once (a rename, an install) shows the first few and
 * counts the rest on one node, and a counted file takes a place as soon as one is free.
 */
export class FileTrail {
  private byMember = new Map<string, Map<string, FileMark>>();

  touch(member: string, path: string, kind: 'edit' | 'create', now: number): FileMark {
    let files = this.byMember.get(member);
    if (!files) this.byMember.set(member, (files = new Map()));
    const known = files.get(path);
    if (known) {
      known.last = now;
      known.touches++;
      if (kind === 'create') known.kind = 'create';
      if (known.slot < 0) known.slot = this.freeSlot(files);
      return known;
    }
    const mark: FileMark = { member, path, name: fileName(path), kind, first: now, last: now, touches: 1, slot: this.freeSlot(files) };
    files.set(path, mark);
    return mark;
  }

  /** Forgets what has run out by `now`, and gives the freed places to the counted files touched most lately. */
  tidy(now: number): void {
    for (const [member, files] of this.byMember) {
      for (const [path, f] of files) if (f.last + FileTtlMs <= now) files.delete(path);
      if (!files.size) { this.byMember.delete(member); continue; }
      const waiting = [...files.values()].filter((f) => f.slot < 0).sort((a, b) => b.last - a.last);
      for (const f of waiting) {
        const slot = this.freeSlot(files);
        if (slot < 0) break;
        f.slot = slot;
      }
    }
  }

  /** A member's files at `now`: those with a place, and those only counted. */
  of(member: string): { placed: FileMark[]; counted: FileMark[] } {
    const files = [...(this.byMember.get(member)?.values() ?? [])];
    return { placed: files.filter((f) => f.slot >= 0), counted: files.filter((f) => f.slot < 0) };
  }

  members(): string[] { return [...this.byMember.keys()]; }

  get size(): number {
    let n = 0;
    for (const files of this.byMember.values()) n += files.size;
    return n;
  }

  clear(): void { this.byMember.clear(); }

  private freeSlot(files: Map<string, FileMark>): number {
    const used = new Set<number>();
    for (const f of files.values()) if (f.slot >= 0) used.add(f.slot);
    for (let i = 0; i < MaxFileNodes; i++) if (!used.has(i)) return i;
    return -1;
  }
}
