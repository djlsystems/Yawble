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
