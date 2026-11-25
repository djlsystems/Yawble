// What the designer knows about the world outside the document: the integrations and their actions (scripts.catalog),
// the expression language's functions, and the schemas' fields. From these it derives a script's requires, the
// suggestions an expression field offers, and the typed "in" and "out" of each step. Everything here but catalogApi is
// a pure function of its arguments.
import type { YawbleApi } from '../../../shared/api';
import type { ScriptDocument, ScriptRequire, ScriptStep } from './types';

export interface CatalogParam { name: string; type: string; required: boolean; description?: string }
export interface CatalogAction { name: string; description?: string; params: CatalogParam[]; result: string; ask?: boolean }
export interface CatalogIntegration { name: string; kind: 'email' | 'api'; enabledForTeam: boolean | null; actions: CatalogAction[] }
export interface CatalogFunction { name: string; signature: string; description: string }
export interface ScriptCatalog { integrations: CatalogIntegration[]; functions: CatalogFunction[] }
export interface UseRealNamesResult { document: ScriptDocument; renamed: Record<string, string>; unresolved: string[] }
export interface MemberIntegration { name: string; kind: 'email' | 'api'; uses: string; overridden: boolean }

export const catalogApi = (api: YawbleApi) => ({
  catalog: (teamId?: number) => api.call<ScriptCatalog>('scripts.catalog', teamId === undefined ? {} : { teamId }),
  useRealNames: (document: ScriptDocument | string, bindings?: Record<string, string>) =>
    api.call<UseRealNamesResult>('scripts.useRealNames', bindings === undefined ? { document } : { document, bindings }),
});
export type CatalogApi = ReturnType<typeof catalogApi>;

/** The actions every Email integration offers. */
export const EMAIL_ACTIONS: readonly string[] = ['list', 'get', 'search', 'thread', 'markRead', 'move'];

/** The types a setting, variable or output can have without a schema. */
export const NATIVE_TYPES: readonly string[] = ['string', 'int', 'float', 'bool', 'date', 'datetime', 'duration', 'object', 'any',
  'list<string>', 'list<int>', 'list<object>'];

/** The trigger kinds a script can accept. */
export const TRIGGER_KINDS: readonly string[] = ['every', 'cron', 'once', 'folder', 'event', 'tell'];

/** The condition operators of an if (exists and empty take no right value). */
export const CONDITION_OPS: readonly string[] = ['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'contains', 'startsWith', 'matches', 'exists', 'empty'];
export const isUnaryOp = (op: string) => op === 'exists' || op === 'empty';

const str = (v: unknown): string | null => (typeof v === 'string' ? v : null);
const steps = (doc: ScriptDocument): ScriptStep[] => (Array.isArray(doc.steps) ? doc.steps.filter((s) => s && typeof s === 'object') : []);

// ---- requires

export function integrationOf(catalog: ScriptCatalog | null | undefined, name: string): CatalogIntegration | null {
  return catalog?.integrations.find((i) => i.name === name) ?? null;
}

/**
 * The requires the steps call for: one entry per integration named by a step's `use`, in the order the steps first use
 * them, its kind (from the catalog, else the document's own entry, else email when every action is an Email action),
 * and the distinct actions called, sorted.
 */
export function deriveRequires(doc: ScriptDocument, catalog: ScriptCatalog | null | undefined): ScriptRequire[] {
  const order: string[] = [];
  const actions = new Map<string, Set<string>>();
  for (const s of steps(doc)) {
    if (s.type !== 'integration') continue;
    const use = str(s.use)?.trim();
    if (!use) continue;
    if (!actions.has(use)) { actions.set(use, new Set()); order.push(use); }
    const action = str(s.action)?.trim();
    if (action) actions.get(use)!.add(action);
  }
  const own = Array.isArray(doc.requires) ? doc.requires : [];
  return order.map((name) => {
    const acts = [...actions.get(name)!].sort();
    const kind = integrationOf(catalog, name)?.kind
      ?? own.find((r) => r && r.name === name)?.kind
      ?? (acts.length > 0 && acts.every((a) => EMAIL_ACTIONS.includes(a)) ? 'email' : 'api');
    return { name, kind, actions: acts };
  });
}

/** Each integration step's use and action, as one text: when it changes, requires is derived again. */
export function usesKey(doc: ScriptDocument | null): string {
  if (!doc) return '';
  return JSON.stringify(steps(doc).filter((s) => s.type === 'integration').map((s) => [str(s.use) ?? '', str(s.action) ?? '']));
}

/** The next document, with requires derived again when an edit changed a step's use or action (or added or removed one). */
export function withDerivedRequires(prev: ScriptDocument | null, next: ScriptDocument, catalog: ScriptCatalog | null | undefined): ScriptDocument {
  if (prev && usesKey(prev) === usesKey(next)) return next;
  const requires = deriveRequires(next, catalog);
  if (JSON.stringify(requires) === JSON.stringify(next.requires ?? [])) return next;
  return { ...next, requires };
}

/** The document names an integration by an alias: a requires entry or a step's use that is not an integration's name. */
export function isAliasDocument(doc: ScriptDocument | null, catalog: ScriptCatalog | null | undefined): boolean {
  if (!doc || !catalog) return false;
  const names = new Set(catalog.integrations.map((i) => i.name));
  const used = [
    ...(Array.isArray(doc.requires) ? doc.requires.map((r) => r?.name) : []),
    ...steps(doc).filter((s) => s.type === 'integration').map((s) => str(s.use)),
  ].filter((n): n is string => !!n && !!n.trim());
  return used.some((n) => !names.has(n));
}

/** The steps that call this integration. */
export function stepsUsing(doc: ScriptDocument, name: string): string[] {
  return steps(doc).filter((s) => s.type === 'integration' && s.use === name).map((s) => s.id);
}

// ---- schemas

export interface SchemaField { name: string; type: string; required: boolean; description?: string; enum?: string[] }
/** The fields of each schema, by the schema's name. */
export type SchemaFields = Record<string, SchemaField[]>;

const refName = (ref: string) => ref.split('/').filter(Boolean).pop() ?? ref;

/** A JSON Schema's type as script type text: string, int, float, bool, date, datetime, list<…>, a schema's name, object. */
export function schemaType(s: unknown): string {
  if (!s || typeof s !== 'object') return 'any';
  const o = s as Record<string, unknown>;
  if (typeof o.$ref === 'string') return refName(o.$ref);
  const type = Array.isArray(o.type) ? o.type.find((t) => t !== 'null') : o.type;
  switch (type) {
    case 'integer': return 'int';
    case 'number': return 'float';
    case 'boolean': return 'bool';
    case 'string': return o.format === 'date-time' ? 'datetime' : o.format === 'date' ? 'date' : o.format === 'duration' ? 'duration' : 'string';
    case 'array': return `list<${schemaType(o.items)}>`;
    case 'object': return typeof o.title === 'string' && o.title ? o.title : 'object';
    default: return 'any';
  }
}

/** A JSON Schema's fields (its properties), in their order. */
export function schemaFields(schema: unknown): SchemaField[] {
  if (!schema || typeof schema !== 'object') return [];
  const o = schema as Record<string, unknown>;
  const props = o.properties && typeof o.properties === 'object' ? (o.properties as Record<string, unknown>) : {};
  const required = new Set(Array.isArray(o.required) ? o.required.filter((r): r is string => typeof r === 'string') : []);
  return Object.entries(props).map(([name, p]) => {
    const f: SchemaField = { name, type: schemaType(p), required: required.has(name) };
    const po = (p && typeof p === 'object' ? p : {}) as Record<string, unknown>;
    if (typeof po.description === 'string') f.description = po.description;
    if (Array.isArray(po.enum)) f.enum = po.enum.map(String);
    return f;
  });
}

/** list<X>: X; any other type: null. */
export function elementType(type: string): string | null {
  const m = /^list<(.+)>$/.exec(type.trim());
  return m ? m[1]!.trim() : null;
}

// ---- types of values in the document

export interface DesignContext {
  doc: ScriptDocument;
  catalog?: ScriptCatalog | null;
  schemas?: SchemaFields;
  /** The member's bindings, alias to integration (when opened from a member). */
  bindings?: Record<string, string>;
}

interface LoopItem { name: string; type: string; loop: string }

function successorRefs(s: ScriptStep): (string | null)[] {
  switch (s.type) {
    case 'if': return [str(s.then), str(s.else)];
    case 'switch': return [...Object.values(s.cases && typeof s.cases === 'object' ? s.cases : {}).map(str), str(s.default)];
    case 'forEach': return [str(s.body), str(s.then)];
    case 'end': return [];
    default: return [str(s.next)];
  }
}

/** The steps that run inside this forEach's body. */
function bodyOf(doc: ScriptDocument, loop: ScriptStep): Set<string> {
  const byId = new Map(steps(doc).map((s) => [s.id, s]));
  const seen = new Set<string>();
  const queue = [str(loop.body)].filter((x): x is string => !!x);
  while (queue.length) {
    const id = queue.shift()!;
    if (seen.has(id) || id === loop.id || !byId.has(id)) continue;
    seen.add(id);
    for (const t of successorRefs(byId.get(id)!)) if (t) queue.push(t);
  }
  return seen;
}

/** The loop items in scope at a step, the outermost loop first, each with its type. */
export function loopItemsAt(ctx: DesignContext, stepId: string | null | undefined): LoopItem[] {
  if (!stepId) return [];
  const loops = steps(ctx.doc).filter((s) => s.type === 'forEach' && str(s.as) && bodyOf(ctx.doc, s).has(stepId));
  // Outer loops contain the inner ones in their bodies: sort so an outer loop comes first.
  loops.sort((a, b) => (bodyOf(ctx.doc, a).has(b.id) ? -1 : bodyOf(ctx.doc, b).has(a.id) ? 1 : 0));
  const out: LoopItem[] = [];
  for (const l of loops) {
    const items = exprType(ctx, l.items, out);
    out.push({ name: str(l.as)!, type: elementType(items) ?? 'any', loop: l.id });
  }
  return out;
}

function fieldType(ctx: DesignContext, type: string, field: string): string {
  const f = ctx.schemas?.[type]?.find((x) => x.name === field);
  return f ? f.type : 'any';
}

/** The type of a dotted path: settings.x, vars.x.y, a loop item and its fields. */
export function pathType(ctx: DesignContext, path: string, items: readonly { name: string; type: string }[] = []): string {
  const [root, ...rest] = path.trim().split('.');
  let type: string | null = null;
  let fields = rest;
  if (root === 'settings' && rest.length) {
    type = ctx.doc.settings?.find((s) => s?.name === rest[0])?.type ?? null;
    fields = rest.slice(1);
  } else if (root === 'vars' && rest.length) {
    type = ctx.doc.variables?.find((v) => v?.name === rest[0])?.type ?? null;
    fields = rest.slice(1);
  } else {
    type = [...items].reverse().find((i) => i.name === root)?.type ?? null;
  }
  if (!type) return 'any';
  for (const f of fields) type = fieldType(ctx, type, f);
  return type;
}

/** The type of a value in a step: a whole "{path}" has the path's type, other text is a string; JSON literals have theirs. */
export function exprType(ctx: DesignContext, value: unknown, items: readonly { name: string; type: string }[] = []): string {
  if (typeof value === 'number') return Number.isInteger(value) ? 'int' : 'float';
  if (typeof value === 'boolean') return 'bool';
  if (Array.isArray(value)) return 'list<any>';
  if (value && typeof value === 'object') return 'object';
  if (typeof value !== 'string') return 'any';
  const m = /^\{\s*([A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*)\s*\}$/.exec(value);
  if (m) return pathType(ctx, m[1]!, items);
  const fn = /^\{\s*([A-Za-z_]\w*)\s*\(/.exec(value);
  if (fn) {
    const sig = ctx.catalog?.functions.find((f) => f.name === fn[1])?.signature;
    const result = sig ? /->\s*(.+)$/.exec(sig)?.[1]?.trim() : undefined;
    if (result) return result;
    return 'any';
  }
  return 'string';
}

// ---- in and out of a step

export interface StepIO {
  in: { name: string; type: string; required?: boolean }[];
  /** What the step produces and the variable (or loop item) it goes into. */
  out: { type: string; into: string | null } | null;
}

const FILE_RESULTS: Record<string, string> = { 'file.readAllText': 'string', 'file.readLines': 'list<string>', 'file.exists': 'bool' };

/** The catalog's action a step calls (through the member's binding for an alias). */
export function actionOf(ctx: DesignContext, step: ScriptStep): CatalogAction | null {
  const use = str(step.use);
  const action = str(step.action);
  if (!use || !action) return null;
  const integration = integrationOf(ctx.catalog, use) ?? integrationOf(ctx.catalog, ctx.bindings?.[use] ?? '');
  return integration?.actions.find((a) => a.name === action) ?? null;
}

export function stepIO(ctx: DesignContext, step: ScriptStep): StepIO {
  const items = loopItemsAt(ctx, step.id);
  const type = (v: unknown) => exprType(ctx, v, items);
  const into = str(step.into);
  const varType = (name: string | null) => (name ? ctx.doc.variables?.find((v) => v?.name === name)?.type ?? 'any' : 'any');
  switch (step.type) {
    case 'integration': {
      const action = actionOf(ctx, step);
      const args = step.args && typeof step.args === 'object' && !Array.isArray(step.args) ? (step.args as Record<string, unknown>) : {};
      if (!action) return { in: Object.entries(args).map(([name, v]) => ({ name, type: type(v) })), out: into ? { type: varType(into), into } : null };
      const params = action.params.filter((p) => p.required || p.name in args).map((p) => ({ name: p.name, type: p.type, required: p.required }));
      return { in: params, out: { type: action.result, into } };
    }
    case 'set': return { in: [{ name: 'value', type: type(step.value) }], out: { type: varType(str(step.var)), into: str(step.var) } };
    case 'forEach': {
      const t = type(step.items);
      return { in: [{ name: 'items', type: t }], out: { type: elementType(t) ?? 'any', into: str(step.as) } };
    }
    case 'if': {
      const w = step.when && typeof step.when === 'object' ? (step.when as Record<string, unknown>) : null;
      if (!w || !('left' in w)) return { in: [], out: null };
      return { in: [{ name: 'left', type: type(w.left) }, ...(isUnaryOp(String(w.op ?? '')) ? [] : [{ name: 'right', type: type(w.right) }])], out: null };
    }
    case 'switch': return { in: [{ name: 'on', type: type(step.on) }], out: null };
    case 'log': return { in: [{ name: 'text', type: 'string' }], out: null };
    case 'end': {
      const outs = step.outputs && typeof step.outputs === 'object' ? Object.entries(step.outputs as Record<string, unknown>) : [];
      return { in: outs.map(([name, v]) => ({ name, type: type(v) })), out: null };
    }
    default: {
      if (!step.type.startsWith('file.')) return { in: [], out: null };
      const ins = [{ name: 'path', type: 'string' }];
      if (step.type === 'file.writeAllText' || step.type === 'file.appendLine') ins.push({ name: 'text', type: 'string' });
      const result = FILE_RESULTS[step.type];
      return { in: ins, out: result ? { type: result, into } : null };
    }
  }
}

/** "folder: string, max: int" */
export const ioInText = (io: StepIO) => io.in.map((p) => `${p.name}: ${p.type}`).join(', ');
/** "EmailList → res", or just the type when it is not saved. */
export const ioOutText = (io: StepIO) => (io.out ? (io.out.into ? `${io.out.type} → ${io.out.into}` : io.out.type) : '');

// ---- expression suggestions

export interface Suggestion {
  /** The path or function name, as it is typed. */
  text: string;
  /** What is inserted (a function: "count("). */
  insert: string;
  kind: 'setting' | 'variable' | 'item' | 'field' | 'function';
  /** The type, or a function's signature. */
  detail: string;
}

const MAX_DEPTH = 3;

function fieldSuggestions(ctx: DesignContext, base: string, type: string, depth: number, seen: Set<string>, out: Suggestion[]) {
  if (depth >= MAX_DEPTH || seen.has(type)) return;
  const fields = ctx.schemas?.[type];
  if (!fields) return;
  const next = new Set(seen).add(type);
  for (const f of fields) {
    const text = `${base}.${f.name}`;
    out.push({ text, insert: text, kind: 'field', detail: f.type });
    fieldSuggestions(ctx, text, f.type, depth + 1, next, out);
  }
}

/** Every value an expression at this step can name: settings.*, vars.*, the loop items and their fields, the functions. */
export function expressionSuggestions(ctx: DesignContext, stepId?: string | null): Suggestion[] {
  const out: Suggestion[] = [];
  for (const s of ctx.doc.settings ?? []) {
    if (!s?.name) continue;
    out.push({ text: `settings.${s.name}`, insert: `settings.${s.name}`, kind: 'setting', detail: s.type ?? '' });
    fieldSuggestions(ctx, `settings.${s.name}`, s.type ?? '', 0, new Set(), out);
  }
  for (const v of ctx.doc.variables ?? []) {
    if (!v?.name) continue;
    out.push({ text: `vars.${v.name}`, insert: `vars.${v.name}`, kind: 'variable', detail: v.type ?? '' });
    fieldSuggestions(ctx, `vars.${v.name}`, v.type ?? '', 0, new Set(), out);
  }
  for (const i of loopItemsAt(ctx, stepId)) {
    out.push({ text: i.name, insert: i.name, kind: 'item', detail: i.type });
    fieldSuggestions(ctx, i.name, i.type, 0, new Set(), out);
  }
  for (const f of ctx.catalog?.functions ?? []) out.push({ text: f.name, insert: `${f.name}(`, kind: 'function', detail: f.signature });
  return out;
}

export interface SuggestionsAt {
  /** The range of the text the pick replaces. */
  from: number;
  to: number;
  /** The cursor is outside braces: the pick is put in "{…}". */
  wrap: boolean;
  items: Suggestion[];
}

/** The suggestions for what is being typed at the cursor (the dotted name before it), at most `limit`. */
export function suggestAt(value: string, cursor: number, all: readonly Suggestion[], limit = 30): SuggestionsAt {
  const before = value.slice(0, cursor);
  const open = before.lastIndexOf('{');
  const inside = open >= 0 && before.indexOf('}', open) < 0;
  const token = /[A-Za-z_][\w.]*$/.exec(before)?.[0] ?? '';
  const t = token.toLowerCase();
  const items = all.filter((s) => s.text.toLowerCase().startsWith(t) && s.text !== token)
    // The shallower paths first, then in the document's order.
    .map((s, i) => ({ s, i, d: s.text.split('.').length }))
    .sort((a, b) => a.d - b.d || a.i - b.i)
    .slice(0, limit)
    .map((x) => x.s);
  return { from: cursor - token.length, to: cursor, wrap: !inside, items };
}

/** The value with the pick put in, and where the cursor goes. */
export function applySuggestion(value: string, at: SuggestionsAt, s: Suggestion): { value: string; cursor: number } {
  const piece = at.wrap ? (s.kind === 'function' ? `{${s.insert})}` : `{${s.insert}}`) : s.insert;
  const next = value.slice(0, at.from) + piece + value.slice(at.to);
  // A function's cursor goes between its parentheses; otherwise after what was put in.
  const cursor = at.from + (s.kind === 'function' ? (at.wrap ? s.insert.length + 1 : s.insert.length) : piece.length);
  return { value: next, cursor };
}

// ---- values in form fields

/** A step's value as the field shows it: text as it is, anything else as JSON. */
export function valueText(v: unknown): string {
  if (v === undefined || v === null) return '';
  return typeof v === 'string' ? v : JSON.stringify(v);
}

/**
 * What the field's text means: text stays text when the value was text; otherwise a number, true, false or null is
 * kept as such and anything else is text.
 */
export function valueFromText(text: string, previous: unknown): unknown {
  if (typeof previous === 'string') return text;
  const t = text.trim();
  if (/^-?\d+(\.\d+)?$/.test(t)) return Number(t);
  if (t === 'true') return true;
  if (t === 'false') return false;
  if ((t.startsWith('[') || t.startsWith('{')) && previous !== undefined) {
    try { return JSON.parse(t); } catch { return text; }
  }
  return text;
}
