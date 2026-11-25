import type { ApiRow, YawbleApi } from '../../shared/api';

// Script members on the team page. The shapes and method names are the core's, from the team documents'
// scripts-rpc.md (core Ui/ScriptsRpc.cs); the calls go through the window's own api.call like every other screen.

/** waiting: parked at a tell that waits on a workflow (waitWorkflowId). */
export type ScriptRunStatus = 'running' | 'waiting' | 'succeeded' | 'failed' | 'skipped' | 'interrupted';

/** One script run (Run in scripts-rpc.md). */
export interface ScriptRun {
  id: number; teamId: number; team: string; memberId: number | null; member: string | null; scriptId: number; script: string; version: number;
  trigger: string; status: ScriptRunStatus; dryRun: boolean; test: boolean; input: unknown; outputs: Record<string, unknown> | null;
  failedStepId: string | null; reason: string | null; startedAt: string; endedAt: string | null;
  /** The workflow a waiting run waits on, else null (an older core leaves it out). */
  waitWorkflowId?: number | null;
}

/**
 * One integration a script member's runs use (scripts.memberGet's integrations): `name` as the script's steps name it, `uses`
 * the integration that runs ('' for an alias with no binding), `overridden` when the member replaces a real name with another.
 */
export interface ScriptMemberIntegration { name: string; kind: 'email' | 'api'; uses: string; overridden: boolean }

/** A script member (ScriptMember): version null = latest, runsVersion the version it runs; nextTriggerAt UTC ISO or null. */
export interface ScriptMember {
  memberId: number; teamId: number; team: string; name: string; scriptId: number; script: string; version: number | null; runsVersion: number | null;
  settings: Record<string, unknown>; bindings: Record<string, string>; timeoutMinutes: number; consecutiveFailures: number;
  running?: boolean; lastRun: ScriptRun | null; nextTriggerAt: string | null;
  /** How many tells one run may make (default 5, at most 50); the next one fails the run. An older core leaves it out. */
  maxTells?: number;
  /** Each integration the script names and what the member uses for it (an older core leaves it out). */
  integrations?: ScriptMemberIntegration[];
}

/** A member's tells per run: the default and the most it can be set to. */
export const MAX_TELLS_DEFAULT = 5;
export const MAX_TELLS_LIMIT = 50;

/** scripts.memberUpdate: what is given changes (bindings replaces the member's whole map). */
export interface ScriptMemberUpdate {
  memberId: number; version?: number | null; settings?: Record<string, unknown>; bindings?: Record<string, string>; timeoutMinutes?: number;
  maxTells?: number;
}

/** scripts.memberState: the member's saved variables. */
export interface ScriptMemberState { memberId: number; variables: Record<string, unknown>; consecutiveFailures: number; updatedAt: string | null }

/** scripts.memberFiles: the files a script member wrote in its data folder, newest first; a text file carries its last lines. */
export interface ScriptMemberFile { path: string; bytes: number; modifiedAt: string; tail?: string[] }

/** scripts.list's ScriptSummary. runnableVersion null: only drafts (no member can run it). */
export interface ScriptSummary {
  id: number; key: string; name: string; description: string; latestVersion: number; runnableVersion: number | null; draft: boolean; updatedAt: string;
  usedBy: { teamId: number; team: string; memberId: number; member: string; version: number | null }[];
}

export interface ScriptVersionInfo { version: number; ok: boolean; createdAt: string }

/** A setting the script declares (its document's settings). */
export interface ScriptSettingDecl { name: string; type?: string; label?: string; default?: unknown }
/** An integration the script needs (its document's requires): bound per member to one of the team's integrations. */
export interface ScriptRequire { name: string; kind: string; actions?: string[] }

/** scripts.get's ScriptVersion (only what the team page reads of the document). */
export interface ScriptVersionDoc {
  id: number; key: string; name: string; version: number; ok: boolean;
  document: { settings?: ScriptSettingDecl[]; requires?: ScriptRequire[]; [k: string]: unknown };
}

export interface ScriptMemberAdd {
  teamId: number; name: string; script: number | string; version?: number | null; settings?: Record<string, unknown>; bindings?: Record<string, string>;
}

/** An Email integration (integrations.emailList). */
interface EmailIntegrationRow { id: number; name: string; kind: 'email'; enabledAll: boolean; teamIds: number[] }

export const scriptsApi = (api: YawbleApi) => ({
  list: () => api.call<ScriptSummary[]>('scripts.list', {}),
  versions: (script: number | string) => api.call<ScriptVersionInfo[]>('scripts.versions', { script }),
  get: (script: number | string, version?: number) => api.call<ScriptVersionDoc>('scripts.get', version === undefined ? { script } : { script, version }),
  memberAdd: (p: ScriptMemberAdd) => api.call<ScriptMember>('scripts.memberAdd', { ...p }),
  memberGet: (memberId: number) => api.call<ScriptMember>('scripts.memberGet', { memberId }),
  memberUpdate: (p: ScriptMemberUpdate) => api.call<ScriptMember>('scripts.memberUpdate', { ...p }),
  memberState: (memberId: number) => api.call<ScriptMemberState>('scripts.memberState', { memberId }),
  memberResetVariable: (memberId: number, name: string) => api.call<{ reset: boolean }>('scripts.memberResetVariable', { memberId, name }),
  memberFiles: (memberId: number, lines = 30) => api.call<{ memberId: number; files: ScriptMemberFile[] }>('scripts.memberFiles', { memberId, lines }),
  runs: (memberId: number, limit = 10) => api.call<ScriptRun[]>('scripts.runs', { memberId, limit }),
});

/** The team's integrations a requires entry of this kind can be bound to (by name): Email integrations or web APIs offered to the team. */
export async function bindableIntegrations(api: YawbleApi, teamId: number, kind: string): Promise<string[] | null> {
  if (kind === 'email') {
    const rows = await api.call<EmailIntegrationRow[]>('integrations.emailList', {});
    return rows.filter((r) => r.enabledAll || r.teamIds.includes(teamId)).map((r) => r.name);
  }
  if (kind === 'api') {
    const rows = await api.call<ApiRow[]>('integrations.apiList', {});
    return rows.filter((r) => (r.kind ?? 'http') === 'http' && (r.all || r.teams.includes(teamId))).map((r) => r.name);
  }
  return null; // a kind the page does not list: the name is typed
}

// ---- integrations: real names and the per-member override ----

/** One of the person's integrations a script can name: its name, its kind, and the teams it is turned on for. */
export interface KnownIntegration { name: string; kind: 'email' | 'api'; all: boolean; teamIds: number[] }

/** Every Email integration and web API by name (either list failing leaves the other). */
export async function knownIntegrations(api: YawbleApi): Promise<KnownIntegration[]> {
  const [emails, apis] = await Promise.all([
    api.call<EmailIntegrationRow[]>('integrations.emailList', {}).catch(() => [] as EmailIntegrationRow[]),
    api.call<ApiRow[]>('integrations.apiList', {}).catch(() => [] as ApiRow[]),
  ]);
  return [
    ...(emails ?? []).map((e) => ({ name: e.name, kind: 'email' as const, all: e.enabledAll, teamIds: e.teamIds ?? [] })),
    ...(apis ?? []).filter((a) => (a.kind ?? 'http') === 'http').map((a) => ({ name: a.name, kind: 'api' as const, all: a.all, teamIds: a.teams ?? [] })),
  ];
}

/** Whether a requires name is one of the person's integrations (a real name) rather than an alias each member binds. */
export const isRealName = (known: readonly KnownIntegration[], name: string) =>
  known.some((k) => k.name.toLowerCase() === name.trim().toLowerCase());

/** "Yawble DistinctID Support → Other inbox" for an override (or an alias's binding); the name alone when the member uses it. */
export function integrationText(i: Pick<ScriptMemberIntegration, 'name' | 'uses'>): string {
  if (!i.uses) return `${i.name} → (not set)`;
  return i.uses.toLowerCase() === i.name.toLowerCase() ? i.name : `${i.name} → ${i.uses}`;
}

/** What a member may use in place of an integration: the team's integrations of the same kind, other than the one named. */
export function overrideChoices(choices: readonly { name: string; kind: string }[], i: Pick<ScriptMemberIntegration, 'name' | 'kind'>): string[] {
  return choices.filter((c) => c.kind === i.kind && c.name.toLowerCase() !== i.name.toLowerCase()).map((c) => c.name);
}

/**
 * The bindings to save from the form: per requires name the choice ('' = use the integration the script names, so no entry).
 * An alias keeps its binding even when it names the same text.
 */
export function bindingsFrom(choice: Record<string, string>, real: (name: string) => boolean): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [name, value] of Object.entries(choice)) {
    const v = (value ?? '').trim();
    if (!v) continue;
    if (real(name) && v.toLowerCase() === name.trim().toLowerCase()) continue;
    out[name] = v;
  }
  return out;
}

// ---- the settings form ----

/** The options of an enum setting ("enum(a, b, c)"), or null when the type is not one. */
export function enumOptions(type: string | undefined): string[] | null {
  const m = /^enum\s*\((.*)\)$/.exec((type ?? '').trim());
  return m ? m[1].split(',').map((s) => s.trim()).filter((s) => s.length > 0) : null;
}

export type SettingInput = 'text' | 'number' | 'bool' | 'enum' | 'list' | 'json';

/** How a setting of this type is typed in: a box, a number, a checkbox, a select, one item per line, or JSON. */
export function settingInput(type: string | undefined): SettingInput {
  const t = (type ?? 'string').trim();
  if (t === 'int' || t === 'float') return 'number';
  if (t === 'bool') return 'bool';
  if (enumOptions(t)) return 'enum';
  if (t.startsWith('list<')) return 'list';
  if (['string', 'date', 'datetime', 'duration'].includes(t) || t.startsWith('enum')) return 'text';
  return 'json';
}

/** A setting's value as its form field shows it (the default, or what the member has). */
export function settingText(decl: ScriptSettingDecl, value: unknown): string | boolean {
  const kind = settingInput(decl.type);
  if (kind === 'bool') return value === true;
  if (value === undefined || value === null) return '';
  if (kind === 'list') return Array.isArray(value) ? value.map((v) => (typeof v === 'string' ? v : JSON.stringify(v))).join('\n') : '';
  if (kind === 'json') return JSON.stringify(value, null, 2);
  return String(value);
}

/**
 * A form field's value as the setting's type: undefined when it is left empty (the core then takes the default, or says
 * the setting needs a value). Throws with the reason when the text does not fit the type.
 */
export function settingValue(decl: ScriptSettingDecl, text: string | boolean): unknown {
  const type = (decl.type ?? 'string').trim();
  const kind = settingInput(type);
  if (kind === 'bool') return text === true;
  const s = String(text);
  if (!s.trim()) return undefined;
  if (kind === 'number') {
    const n = Number(s.trim());
    if (!Number.isFinite(n) || (type === 'int' && !Number.isInteger(n))) throw new Error(`${label(decl)} must be ${type === 'int' ? 'a whole number' : 'a number'}`);
    return n;
  }
  if (kind === 'list') {
    const inner = type.slice(5, -1).trim();
    const items = s.split('\n').map((x) => x.trim()).filter((x) => x.length > 0);
    if (inner === 'int' || inner === 'float') {
      return items.map((x) => {
        const n = Number(x);
        if (!Number.isFinite(n) || (inner === 'int' && !Number.isInteger(n))) throw new Error(`${label(decl)}: '${x}' is not ${inner === 'int' ? 'a whole number' : 'a number'}`);
        return n;
      });
    }
    if (inner === 'bool') return items.map((x) => x.toLowerCase() === 'true');
    if (settingInput(inner) === 'json') return items.map((x) => parseJson(decl, x));
    return items;
  }
  if (kind === 'json') return parseJson(decl, s);
  return s;
}

function parseJson(decl: ScriptSettingDecl, s: string): unknown {
  try { return JSON.parse(s); } catch { throw new Error(`${label(decl)} must be JSON`); }
}

export const label = (decl: ScriptSettingDecl) => decl.label?.trim() || decl.name;

/** A saved variable's value in one short line (the pane's list). */
export function shortValue(v: unknown): string {
  const s = typeof v === 'string' ? v : JSON.stringify(v);
  return s.length > 120 ? `${s.slice(0, 117)}…` : s;
}

/** script@version as the tile says it: a member on latest shows the version it runs ("poller@latest (v3)"). */
export function scriptAtVersion(m: Pick<ScriptMember, 'script' | 'version' | 'runsVersion'>): string {
  if (m.version !== null) return `${m.script}@${m.version}`;
  return m.runsVersion !== null ? `${m.script}@latest (v${m.runsVersion})` : `${m.script}@latest (no runnable version)`;
}
