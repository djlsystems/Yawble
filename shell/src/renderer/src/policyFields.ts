import type { MemberPolicyView } from '../../shared/api';

// The policy fields the screens edit. "override": may appear in a per-agent override (tighten
// only); "loosen": may appear in the loosen block.
export interface FieldDef {
  path: string;
  label: string;
  kind: 'list' | 'number' | 'clipboard';
  hint?: string;
  loosen: boolean;
}

export const POLICY_FIELDS: FieldDef[] = [
  { path: 'network.allowHosts', label: 'Allowed hosts', kind: 'list', hint: 'beyond the model API and the bus', loosen: true },
  { path: 'filesystem.readonly', label: 'Read-only folders', kind: 'list', loosen: true },
  { path: 'filesystem.readwrite', label: 'Read/write folders', kind: 'list', loosen: true },
  { path: 'filesystem.denied', label: 'Denied folders', kind: 'list', hint: 'a union: never removed', loosen: false },
  { path: 'compute.wallTimeoutSec', label: 'Wall-clock timeout', kind: 'number', hint: 'seconds', loosen: true },
  { path: 'compute.idleTimeoutSec', label: 'Idle timeout', kind: 'number', hint: 'seconds', loosen: true },
  { path: 'compute.memoryMb', label: 'Memory cap', kind: 'number', hint: 'MB · not enforced in the MVP', loosen: true },
  { path: 'compute.cpuPercent', label: 'CPU cap', kind: 'number', hint: '% · not enforced in the MVP', loosen: true },
  { path: 'compute.maxProcesses', label: 'Process cap', kind: 'number', hint: 'not enforced in the MVP', loosen: true },
  { path: 'ui.clipboard', label: 'Clipboard', kind: 'clipboard', loosen: false },
];

export const CLIPBOARD = ['none', 'read', 'write', 'all'];

export function testId(path: string): string {
  return path.replace('.', '-');
}

export function getPath(doc: any, path: string): any {
  const [a, b] = path.split('.');
  return doc?.[a]?.[b];
}

export function setPath(doc: any, path: string, value: unknown): void {
  const [a, b] = path.split('.');
  if (value === undefined) {
    if (doc[a]) {
      delete doc[a][b];
      if (Object.keys(doc[a]).length === 0) delete doc[a];
    }
    return;
  }
  doc[a] ??= {};
  doc[a][b] = value;
}

export function effectiveOf(view: MemberPolicyView, path: string): unknown {
  const e = view.effective;
  switch (path) {
    case 'network.allowHosts': return e.allowHosts;
    case 'filesystem.readonly': return e.readonly;
    case 'filesystem.readwrite': return e.readwrite;
    case 'filesystem.denied': return e.denied;
    case 'ui.clipboard': return e.clipboard;
    default: return (e.compute as any)[path.split('.')[1]];
  }
}

export function show(value: unknown): string {
  if (value === undefined || value === null) return '—';
  if (Array.isArray(value)) return value.length ? value.join(', ') : '(none)';
  return String(value);
}

/** The text an input shows for a value, and back. */
export function toText(value: unknown): string {
  if (value === undefined || value === null) return '';
  return Array.isArray(value) ? value.join('\n') : String(value);
}

// ---------------------------------------------------------------- The Folder access block, honest save results

export type FolderList = 'readwrite' | 'readonly' | 'denied';
export const FOLDER_LISTS: { list: FolderList; label: string }[] = [
  { list: 'readwrite', label: 'Read/write' },
  { list: 'readonly', label: 'Read-only' },
  { list: 'denied', label: 'Denied' },
];
/** Where an effective folder comes from: the global policy, the app's implicit folders, this member's loosening or override, or its team's folder list. */
export type FolderSource = 'global' | 'implicit' | 'loosen' | 'override' | 'team';
export const SOURCE_LABEL: Record<FolderSource, string> = {
  global: 'global', implicit: 'implicit', loosen: 'this member (loosened)', override: 'this member (override)',
  team: 'the team (its Settings > Folders)',
};

export interface FolderRow {
  /** The path as the effective policy has it (resolved by the core), or the member's own entry as typed when not in effect. */
  path: string;
  source: FolderSource;
  /** The member's own entry as stored (loosen or override), which Remove takes out; null for global / implicit rows. */
  own: string | null;
  /** A loosened entry the core does not add, because other access already covers it. */
  covered?: boolean;
}

/** "." and ".." folded as the core's PolicyPaths.Resolve does, below the root (`C:`, `//server/share`, `~` or `/`). */
function foldDots(s: string): string {
  const root = /^(\/\/[^/]+\/[^/]+|[a-z]:|~)?/i.exec(s)![0];
  const rest = s.slice(root.length);
  const absolute = rest.startsWith('/');
  const segs: string[] = [];
  for (const seg of rest.split('/')) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..') {
      if (segs.length && segs[segs.length - 1] !== '..') segs.pop();
      else if (!absolute && !root) segs.push('..');
      continue;
    }
    segs.push(seg);
  }
  return root + (absolute ? '/' : '') + segs.join('/');
}

/** A path compared as the core resolves it, roughly: forward slashes, "." and ".." folded, no trailing slash, Windows paths case-insensitive. */
function norm(p: string): string {
  let s = foldDots(p.trim().replace(/\\/g, '/'));
  if (s.length > 1) s = s.replace(/\/+$/, '');
  return /^[a-z]:/i.test(s) || s.startsWith('//') ? s.toLowerCase() : s;
}

/** Whether an entry as typed (`~/src`, `C:\work`) names the resolved path the core shows (`/home/pat/src`, `C:\work`). */
export function samePath(typed: string, resolved: string): boolean {
  const t = norm(typed);
  const r = norm(resolved);
  if (t === r) return true;
  return t.startsWith('~/') && r.endsWith(t.slice(1));
}

/** An absolute path: `C:\..`, `\\server\share`, `/..` or `~/..` (the core expands `~`). */
export function isAbsolutePath(p: string): boolean {
  const s = p.trim();
  return /^[a-z]:[\\/]/i.test(s) || /^[\\/]/.test(s) || s === '~' || /^~[\\/]/.test(s);
}

function listOf(doc: any, list: FolderList): string[] {
  const v = doc?.filesystem?.[list];
  return Array.isArray(v) ? v : [];
}

/**
 * What the core resolved one loosened entry to (its `policy.previewLoosen` of that entry alone): the effective path, null
 * when the core adds nothing for it (already covered), undefined when not known yet (then the entry is matched by samePath).
 */
export type ResolvedEntry = (list: 'readwrite' | 'readonly', entry: string) => string | null | undefined;

/** The rows of one folder list: each effective folder with its source, then the member's loosened entries the core skips. */
export function folderRows(view: MemberPolicyView, list: FolderList, resolved?: ResolvedEntry): FolderRow[] {
  const effective: string[] = (view.effective as any)[list] ?? [];
  const implicit: string[] = (view.implicit as any)[list] ?? [];
  const loosened = new Set(view.effective.loosened ?? []);
  const loosen = list === 'denied' ? [] : listOf(view.loosen, list);
  const override = listOf(view.override, list);
  const global = listOf(view.global, list);
  const ownIs = (l: string, p: string) => {
    const r = list === 'denied' ? undefined : resolved?.(list, l);
    return r === undefined ? samePath(l, p) : r === p;
  };
  const rows: FolderRow[] = effective.map((p) => {
    if (implicit.some((i) => samePath(i, p))) return { path: p, source: 'implicit', own: null };
    if (loosened.has(`filesystem.${list} + ${p}`)) return { path: p, source: 'loosen', own: loosen.find((l) => ownIs(l, p)) ?? p };
    // The team's folder list: changed in the team's settings, so the row has no Remove here.
    if (loosened.has(`filesystem.${list} + ${p} (team)`)) return { path: p, source: 'team', own: null };
    const o = override.find((x) => samePath(x, p));
    if (o !== undefined && !global.some((g) => samePath(g, p))) return { path: p, source: 'override', own: o };
    return { path: p, source: 'global', own: null };
  });
  for (const l of loosen) {
    if (rows.some((r) => r.own === l)) continue;
    rows.push({ path: l, source: 'loosen', own: l, covered: true });
  }
  return rows;
}

/**
 * What the Folder access block has typed and not saved: the path, its mode and the step waiting for Confirm. The Member
 * settings dialog keeps it, so leaving the Policy tab (which unmounts the block) drops nothing.
 */
export interface FolderPending {
  kind: 'add' | 'remove';
  lines: string[];
  title: string;
  warnings: string[];
  save: () => Promise<MemberPolicyView>;
}
export interface FolderDraft { path: string; mode: 'readwrite' | 'readonly'; pending: FolderPending | null }
export const newFolderDraft = (): FolderDraft => ({ path: '', mode: 'readwrite', pending: null });
export const folderDraftDirty = (d: FolderDraft) => d.path.trim().length > 0 || d.pending !== null;

/** A document with its keys sorted and empty objects dropped, as one string: two that equal mean the same policy. */
export function canon(doc: unknown): string {
  const walk = (v: any): any => {
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === 'object') {
      const out: any = {};
      for (const k of Object.keys(v).sort()) {
        const w = walk(v[k]);
        if (w === undefined || w === null || (typeof w === 'object' && !Array.isArray(w) && Object.keys(w).length === 0)) continue;
        out[k] = w;
      }
      return out;
    }
    return v;
  };
  return JSON.stringify(walk(doc ?? {}));
}

const SHORT: Record<string, string> = {
  'filesystem.readwrite': 'Read/write', 'filesystem.readonly': 'Read-only', 'filesystem.denied': 'Denied', 'network.allowHosts': 'Allowed hosts',
};

/** The fields whose effective value differs between two views (the rows the Effective view highlights after a save). */
export function changedFields(before: MemberPolicyView, after: MemberPolicyView): string[] {
  return POLICY_FIELDS.filter((f) => canon({ v: effectiveOf(before, f.path) }) !== canon({ v: effectiveOf(after, f.path) })).map((f) => f.path);
}

/** The folders of a list in `after` that `before` did not have (highlighted in the Folder access block). */
export function addedFolders(before: MemberPolicyView, after: MemberPolicyView, list: FolderList): string[] {
  const old: string[] = (before.effective as any)[list] ?? [];
  return ((after.effective as any)[list] ?? []).filter((p: string) => !old.includes(p));
}

/**
 * What a save changed, in words: one line per changed field ("Read/write now: C:\a, Z:\b"), or "Nothing changed" when the
 * stored override and loosening and the effective policy are all unchanged. Never "saved" for a no-op.
 */
export function describeChange(before: MemberPolicyView, after: MemberPolicyView): { changed: boolean; text: string } {
  const fields = changedFields(before, after);
  if (fields.length === 0) {
    const stored = canon(before.override) !== canon(after.override) || canon(before.loosen) !== canon(after.loosen);
    return stored
      ? { changed: true, text: 'Saved. The effective policy is the same as before.' }
      : { changed: false, text: 'Nothing changed.' };
  }
  const lines = fields.map((path) => {
    const def = POLICY_FIELDS.find((f) => f.path === path)!;
    const label = SHORT[path] ?? def.label;
    const now = effectiveOf(after, path);
    if (Array.isArray(now)) return `${label} now: ${show(now)}.`;
    return `${label} now: ${show(now)} (was ${show(effectiveOf(before, path))}).`;
  });
  return { changed: true, text: `${lines.join(' ')} It applies from the next run; a running sandbox keeps its policy.` };
}

export function fromText(def: FieldDef, text: string): unknown {
  if (def.kind === 'list') return text.split(/[\n,]/).map((s) => s.trim()).filter((s) => s.length > 0);
  if (def.kind === 'number') {
    const t = text.trim();
    if (t === '') return undefined;
    const n = Number(t);
    if (!Number.isInteger(n)) throw new Error(`${def.label}: a whole number`);
    return n;
  }
  return text;
}
