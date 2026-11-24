import type { ApiRow, Connection, YawbleApi } from '../../../shared/api';
import type {
  Check, ExportResult, Run, RunDetail, SchemaSaveResult, SchemaSummary, SchemaVersion, ScriptDeleteResult, ScriptDocument,
  ScriptSaveResult, ScriptStep, ScriptSummary, ScriptVersion, ScriptVersionRow, Step, TestRunParams, TestRunResult,
} from './types';

// B134 (SC-4): typed wrappers over the core's scripts.* and schemas.* methods (team docs scripts-rpc.md), and the pure
// helpers the Scripts place draws with (highlighting, the outline, the jump to a step, the files a run wrote).

type Ref = number | string;

export const scriptsApi = (api: YawbleApi) => ({
  list: () => api.call<ScriptSummary[]>('scripts.list', {}),
  get: (script: Ref, version?: number) => api.call<ScriptVersion>('scripts.get', version === undefined ? { script } : { script, version }),
  save: (document: ScriptDocument | string) => api.call<ScriptSaveResult>('scripts.save', { document }),
  check: (document: ScriptDocument | string) => api.call<Check>('scripts.check', { document }),
  versions: (script: Ref) => api.call<ScriptVersionRow[]>('scripts.versions', { script }),
  export: (script: Ref, version?: number) => api.call<ExportResult>('scripts.export', version === undefined ? { script } : { script, version }),
  import: (json: string) => api.call<ScriptSaveResult>('scripts.import', { json }),
  delete: (script: Ref) => api.call<ScriptDeleteResult>('scripts.delete', { script }),
  testRun: (p: TestRunParams) => api.call<TestRunResult>('scripts.testRun', { ...p }),
  runs: (p: { teamId?: number; memberId?: number; limit?: number } = {}) => api.call<Run[]>('scripts.runs', { ...p }),
  run: (id: number) => api.call<RunDetail>('scripts.run', { id }),
  schemas: () => api.call<SchemaSummary[]>('schemas.list', {}),
  schema: (name: string, version?: number) => api.call<SchemaVersion>('schemas.get', version === undefined ? { name } : { name, version }),
  schemaSave: (name: string, schema: Record<string, unknown> | string, description?: string) =>
    api.call<SchemaSaveResult>('schemas.save', description === undefined ? { name, schema } : { name, description, schema }),
});
export type ScriptsApi = ReturnType<typeof scriptsApi>;

/** An integration a requires entry can be bound to: its name (what bindings carry) and its kind (api, email). */
export interface BindingChoice { name: string; kind: string }

/**
 * The integrations of a team a script's requires can be bound to: the web APIs offered to the team (kind api) and the
 * email accounts (kind email, by their Connection's name). Either list failing leaves the other.
 */
export async function teamBindingChoices(api: YawbleApi, teamId: number): Promise<BindingChoice[]> {
  const [apis, conns] = await Promise.all([
    api.call<ApiRow[]>('integrations.apiList', {}).catch(() => [] as ApiRow[]),
    api.call<Connection[]>('integrations.connections', {}).catch(() => [] as Connection[]),
  ]);
  return [
    ...(apis ?? []).filter((a) => a.kind === 'http' && (a.all || a.teams.includes(teamId))).map((a) => ({ name: a.name, kind: 'api' })),
    ...(conns ?? []).filter((c) => (c.kind as string) === 'email').map((c) => ({ name: c.name, kind: 'email' })),
  ];
}

// ---- highlighting

export type JsonTokenKind = 'key' | 'string' | 'number' | 'literal' | 'punct' | 'space' | 'other';
export interface JsonToken { text: string; kind: JsonTokenKind }

const TOKEN = /("(?:[^"\\\n]|\\.)*"?)(\s*:)?|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|\b(true|false|null)\b|([{}[\],:])|(\s+)|([^\s"{}[\],:]+)/g;

/** The JSON text as tokens for a highlighted view; joined, the tokens are the text exactly (even when it does not parse). */
export function jsonTokens(text: string): JsonToken[] {
  const out: JsonToken[] = [];
  for (const m of text.matchAll(TOKEN)) {
    if (m[1] !== undefined) {
      out.push({ text: m[1], kind: m[2] !== undefined ? 'key' : 'string' });
      if (m[2] !== undefined) {
        const colon = m[2];
        const ws = colon.slice(0, -1);
        if (ws) out.push({ text: ws, kind: 'space' });
        out.push({ text: ':', kind: 'punct' });
      }
    } else if (m[3] !== undefined) out.push({ text: m[3], kind: 'number' });
    else if (m[4] !== undefined) out.push({ text: m[4], kind: 'literal' });
    else if (m[5] !== undefined) out.push({ text: m[5], kind: 'punct' });
    else if (m[6] !== undefined) out.push({ text: m[6], kind: 'space' });
    else out.push({ text: m[0], kind: 'other' });
  }
  return out;
}

/** The JSON's parse error as the person reads it, or null when it parses to an object. */
export function jsonProblem(text: string): string | null {
  try {
    const v = JSON.parse(text);
    return v && typeof v === 'object' && !Array.isArray(v) ? null : 'The document must be a JSON object.';
  } catch (e) {
    return `Not valid JSON: ${(e as Error).message}`;
  }
}

const escapeRe = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/**
 * Where a step is in the JSON text: the range of its `"id": "<stepId>"` (the one after "steps" when the text has one),
 * or null when it is not there.
 */
export function stepRange(text: string, stepId: string): { start: number; end: number } | null {
  const re = new RegExp(`"id"\\s*:\\s*"${escapeRe(stepId)}"`, 'g');
  const stepsAt = text.search(/"steps"\s*:/);
  let first: { start: number; end: number } | null = null;
  for (const m of text.matchAll(re)) {
    const r = { start: m.index ?? 0, end: (m.index ?? 0) + m[0].length };
    if (stepsAt >= 0 && r.start > stepsAt) return r;
    first ??= r;
  }
  return first;
}

/** The 0-based line a character offset is on. */
export const lineOf = (text: string, offset: number) => text.slice(0, offset).split('\n').length - 1;

// ---- the outline

export interface OutlineLink { label: string; target: string | null }
export interface OutlineStep { id: string; type: string; links: OutlineLink[]; start: boolean }
export interface Outline {
  settings: { name: string; type: string; detail: string }[];
  requires: { name: string; kind: string; detail: string }[];
  variables: { name: string; type: string; detail: string }[];
  steps: OutlineStep[];
}

function stepLinks(s: ScriptStep): OutlineLink[] {
  const links: OutlineLink[] = [];
  const add = (label: string, v: unknown) => { if (v === null || typeof v === 'string') links.push({ label, target: v as string | null }); };
  if ('next' in s) add('next', s.next);
  if ('body' in s) add('body', s.body);
  if ('then' in s) add('then', s.then);
  if ('else' in s) add('else', s.else);
  if (s.cases && typeof s.cases === 'object') for (const [k, v] of Object.entries(s.cases)) add(`case ${k}`, v);
  if ('default' in s && s.type === 'switch') add('default', s.default);
  return links;
}

/** The read-only outline beside the JSON: settings, requires, variables, and each step with where it goes next. */
export function buildOutline(doc: ScriptDocument | null): Outline {
  const arr = <T>(v: unknown): T[] => (Array.isArray(v) ? (v as T[]) : []);
  const d = doc ?? {};
  return {
    settings: arr<Record<string, unknown>>(d.settings).map((s) => ({
      name: String(s.name ?? '?'), type: String(s.type ?? ''),
      detail: [s.label ? String(s.label) : '', 'default' in s ? `default ${JSON.stringify(s.default)}` : ''].filter(Boolean).join(' · '),
    })),
    requires: arr<Record<string, unknown>>(d.requires).map((r) => ({
      name: String(r.name ?? '?'), kind: String(r.kind ?? ''), detail: arr<string>(r.actions).join(', '),
    })),
    variables: arr<Record<string, unknown>>(d.variables).map((v) => ({
      name: String(v.name ?? '?'), type: String(v.type ?? ''),
      detail: [v.scope ? String(v.scope) : '', 'initial' in v ? `initial ${JSON.stringify(v.initial)}` : ''].filter(Boolean).join(' · '),
    })),
    steps: arr<ScriptStep>(d.steps).filter((s) => s && typeof s === 'object').map((s) => ({
      id: String(s.id ?? '?'), type: String(s.type ?? ''), links: stepLinks(s), start: d.start === s.id,
    })),
  };
}

// ---- runs

const FILE_KEYS = new Set(['path', 'file', 'paths', 'files', 'written', 'wrote']);

/** The files a run wrote, in order, once each: file paths in its steps' outputs (and a file step's path) and in its outputs. */
export function writtenFiles(steps: readonly Step[], outputs: unknown): string[] {
  const seen = new Set<string>();
  const add = (p: unknown) => { if (typeof p === 'string' && p.trim()) seen.add(p); };
  const walk = (v: unknown, key: string | null, depth: number) => {
    if (depth > 6 || v === null || v === undefined) return;
    if (typeof v === 'string') { if (key && FILE_KEYS.has(key)) add(v); return; }
    if (Array.isArray(v)) { for (const x of v) walk(x, key, depth + 1); return; }
    if (typeof v === 'object') for (const [k, x] of Object.entries(v as Record<string, unknown>)) walk(x, k, depth + 1);
  };
  for (const s of steps) {
    if (/^file\.(writeAllText|appendLine)$/.test(s.type)) {
      const out = s.output as Record<string, unknown> | null;
      const inp = s.input as Record<string, unknown> | null;
      add((out && typeof out === 'object' ? out.path : undefined) ?? (inp && typeof inp === 'object' ? inp.path : undefined));
    }
    walk(s.output, null, 0);
  }
  walk(outputs, null, 0);
  return [...seen];
}

/** How long a step or run took, short ("0.4 s", "12 s", "3 min"); empty while it runs. */
export function took(startedAt: string, endedAt: string | null): string {
  if (!endedAt) return '';
  const ms = Date.parse(endedAt) - Date.parse(startedAt);
  if (!Number.isFinite(ms) || ms < 0) return '';
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
  return `${Math.round(ms / 60_000)} min`;
}

/** A value shown in the run view: pretty JSON, or the text as it is. */
export function showValue(v: unknown): string {
  if (v === undefined) return '';
  if (typeof v === 'string') return v;
  try { return JSON.stringify(v, null, 2); } catch { return String(v); }
}

// ---- the test run

/** A TriggerFired payload fired at `now` (built-in schema: triggerId string, kind every|cron|once, firedAt date-time). */
export function fakeTriggerFired(doc: ScriptDocument | null, now: Date = new Date()): { triggerId: string; kind: string; firedAt: string } {
  const accepts = Array.isArray(doc?.trigger?.accepts) ? doc!.trigger!.accepts! : [];
  const kind = accepts.find((a) => a === 'every' || a === 'cron' || a === 'once') ?? 'once';
  return { triggerId: 'test', kind, firedAt: now.toISOString().replace(/\.\d{3}Z$/, 'Z') };
}

/** The form's text for a setting's value, and back (list<...>: one per line; int/float: a number; bool: true/false). */
export function settingFromText(type: string, text: string): unknown {
  const t = type.trim();
  if (t.startsWith('list<')) {
    const inner = t.slice(5, -1);
    return text.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => settingFromText(inner, l));
  }
  if (t === 'int' || t === 'float') { const n = Number(text); return text.trim() === '' || !Number.isFinite(n) ? text : n; }
  if (t === 'bool') return text === 'true';
  return text;
}
export function settingToText(type: string, v: unknown): string {
  if (v === undefined || v === null) return '';
  if (type.trim().startsWith('list<') && Array.isArray(v)) return v.map((x) => (typeof x === 'string' ? x : JSON.stringify(x))).join('\n');
  return typeof v === 'string' ? v : JSON.stringify(v);
}

const LIVE_KEY = 'yawble.scripts.liveConfirmed';

/** The first live (non-dry) test run of a script asks first; after a yes, that script's key is remembered here. */
export const liveConfirm = {
  done(key: string): boolean {
    try { return (JSON.parse(localStorage.getItem(LIVE_KEY) ?? '[]') as unknown[]).includes(key); } catch { return false; }
  },
  remember(key: string): void {
    try {
      const list = JSON.parse(localStorage.getItem(LIVE_KEY) ?? '[]') as unknown[];
      if (!list.includes(key)) localStorage.setItem(LIVE_KEY, JSON.stringify([...list, key]));
    } catch { /* no storage: it asks again next time */ }
  },
};

// ---- new scripts

export const BLANK_SCRIPT: ScriptDocument = {
  format: 'yawble-script/1',
  id: 'new-script',
  name: 'New script',
  description: '',
  trigger: { accepts: ['every', 'cron', 'once'] },
  settings: [],
  requires: [],
  variables: [],
  start: 'done',
  steps: [{ id: 'done', type: 'end' }],
};

/**
 * The "Inbox SMS alert" template (on the pinned email and api contracts): each new email's subject
 * texted to a list of phones. The same document as the example in the Concierge's scripts skill: the api action is the operation's id,
 * email list returns EmailList, and the poll saves the last message's id (docs/connections-and-integrations.md, the poll pattern).
 */
export const INBOX_SMS_ALERT: ScriptDocument = {
  format: 'yawble-script/1',
  id: 'inbox-sms-alert',
  name: 'Inbox SMS alert',
  description: 'Texts the subject and sender of every new email to a list of phones.',
  trigger: { accepts: ['every', 'cron', 'once'] },
  settings: [
    { name: 'recipients', type: 'list<string>', label: 'Phones to text (E.164, e.g. +15551234567)' },
    { name: 'fromNumber', type: 'string', label: 'The SMS number that sends' },
    { name: 'folder', type: 'string', label: 'Mail folder', default: 'INBOX' },
    { name: 'since', type: 'datetime', label: 'First run: mail received after', default: '2026-11-20T00:00:00Z' },
  ],
  requires: [
    { name: 'mail', kind: 'email', actions: ['list'] },
    { name: 'sms', kind: 'api', actions: ['CreateMessage'] },
  ],
  variables: [
    { name: 'lastId', type: 'string', scope: 'saved' },
    { name: 'res', type: 'EmailList', scope: 'run' },
    { name: 'last', type: 'EmailMessage', scope: 'run' },
    { name: 'sent', type: 'int', scope: 'run', initial: 0 },
  ],
  start: 'list',
  steps: [
    { id: 'list', type: 'integration', use: 'mail', action: 'list',
      args: { folder: '{settings.folder}', after: '{vars.lastId}', since: '{settings.since}', max: 200 }, into: 'res', next: 'any' },
    { id: 'any', type: 'if', when: { left: '{count(vars.res.messages)}', op: 'gt', right: 0 }, then: 'each', else: 'done' },
    { id: 'each', type: 'forEach', items: '{vars.res.messages}', as: 'm', body: 'phones', then: 'keep' },
    { id: 'phones', type: 'forEach', items: '{settings.recipients}', as: 'to', body: 'text', then: null },
    { id: 'text', type: 'integration', use: 'sms', action: 'CreateMessage',
      args: { To: '{to}', From: '{settings.fromNumber}', Body: 'New email: {m.subject} ({m.from.address})' }, next: 'count' },
    { id: 'count', type: 'set', var: 'sent', value: '{vars.sent + 1}', next: null },
    { id: 'keep', type: 'set', var: 'last', value: '{last(vars.res.messages)}', next: 'mark' },
    { id: 'mark', type: 'set', var: 'lastId', value: '{vars.last.id}', next: 'log' },
    { id: 'log', type: 'file.appendLine', path: 'run.log', text: '{now()} {count(vars.res.messages)} new, {vars.sent} texts', next: 'done' },
    { id: 'done', type: 'end' },
  ],
};

export const TEMPLATES: readonly { id: string; label: string; doc: ScriptDocument }[] = [
  { id: 'blank', label: 'Blank', doc: BLANK_SCRIPT },
  { id: 'inbox-sms-alert', label: 'Inbox SMS alert', doc: INBOX_SMS_ALERT },
];

export const pretty = (v: unknown) => JSON.stringify(v, null, 2);

/** A Run's script@version, as the runs list shows it. */
export const runScript = (r: Pick<Run, 'script' | 'version'>) => `${r.script}@${r.version}`;
