// The script document as a flowchart: nodes and edges for the designer, and the edits the designer makes, as pure
// functions. The document stays the source of truth: every edit returns a new document (the input is never changed)
// that keeps every key it does not edit. Node positions live in the document's `layout` section
// (`layout.nodes[stepId] = { x, y }`, top-left corner); a step without one is placed by dagre.
//
// Step types and their successors follow the core's checker and engine (Yawble.Core.Scripts.Language):
// if: then / else; switch: cases (by value) / default; forEach: body / then; end: none; every other step: next.
// A null successor ends the run, or, inside a forEach body, ends that pass of the body.
import dagre from '@dagrejs/dagre';
import type { Check, ScriptDocument, ScriptStep } from './types';

export type FlowKind = 'start' | 'end' | 'integration' | 'if' | 'switch' | 'forEach' | 'set' | 'file' | 'agent' | 'other';
export type EdgeKind = 'next' | 'then' | 'else' | 'case' | 'default' | 'body';

export interface FlowNode {
  id: string;
  kind: FlowKind;
  /** The step's `type` ('' for the start and end nodes). */
  stepType: string;
  title: string;
  /** integration: use.action; if: the condition; switch: the value it branches on; forEach: "items as x". */
  detail: string;
  position: { x: number; y: number };
  /** The check's messages for this step (the start node carries the document-wide ones). */
  problems: string[];
  run: 'taken' | 'failed' | null;
}

export interface FlowEdge {
  id: string;
  source: string;
  target: string;
  kind: EdgeKind;
  label: string | null;
  /** kind 'case': the case's value. */
  caseValue?: string;
  /** A forEach body. */
  dashed: boolean;
}

/** The start node: its edge is the document's `start`. */
export const START_ID = '__start';
/** The end node: a successor that is null (outside a loop body) ends the run here. */
export const END_ID = '__end';

/** The size dagre lays nodes out with. */
export const NODE_SIZE = { width: 220, height: 72 } as const;

export type Branch = { kind: 'next' | 'then' | 'else' | 'default' | 'body' } | { kind: 'case'; value: string };

// ---- step types

/** The step types the checker accepts, with a blank step of each for the designer's palette. */
export const STEP_TYPES: readonly { type: string; kind: FlowKind; label: string; template: Omit<ScriptStep, 'id'> }[] = [
  { type: 'integration', kind: 'integration', label: 'Call an integration', template: { type: 'integration', use: '', action: '', args: {}, next: null } },
  { type: 'if', kind: 'if', label: 'If', template: { type: 'if', when: { left: '', op: 'eq', right: '' }, then: null, else: null } },
  { type: 'switch', kind: 'switch', label: 'Switch', template: { type: 'switch', on: '', cases: {}, default: null } },
  { type: 'forEach', kind: 'forEach', label: 'For each', template: { type: 'forEach', items: '', as: 'item', body: null, then: null } },
  { type: 'set', kind: 'set', label: 'Set a variable', template: { type: 'set', var: '', value: '', next: null } },
  { type: 'log', kind: 'other', label: 'Log a line', template: { type: 'log', text: '', next: null } },
  { type: 'file.writeAllText', kind: 'file', label: 'Write a file', template: { type: 'file.writeAllText', path: '', text: '', next: null } },
  { type: 'file.appendLine', kind: 'file', label: 'Append a line to a file', template: { type: 'file.appendLine', path: '', text: '', next: null } },
  { type: 'file.readAllText', kind: 'file', label: 'Read a file', template: { type: 'file.readAllText', path: '', next: null } },
  { type: 'file.readLines', kind: 'file', label: "Read a file's lines", template: { type: 'file.readLines', path: '', next: null } },
  { type: 'file.exists', kind: 'file', label: 'Check a file exists', template: { type: 'file.exists', path: '', next: null } },
  { type: 'file.delete', kind: 'file', label: 'Delete a file', template: { type: 'file.delete', path: '', next: null } },
  { type: 'end', kind: 'end', label: 'End', template: { type: 'end' } },
];

function kindOf(type: string): FlowKind {
  const known = STEP_TYPES.find((t) => t.type === type);
  if (known) return known.kind;
  return type === 'agent' ? 'agent' : 'other';
}

// ---- helpers

/** A deep copy through JSON: the document is JSON, and this also unwraps reactive proxies. */
function copy<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function text(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function show(value: unknown): string {
  if (value === undefined) return '';
  return typeof value === 'string' ? value : JSON.stringify(value);
}

function conditionText(c: unknown): string {
  if (typeof c === 'string') return c;
  if (typeof c === 'boolean') return String(c);
  if (c && typeof c === 'object') {
    const o = c as Record<string, unknown>;
    if (Array.isArray(o.all)) return o.all.map((x) => `(${conditionText(x)})`).join(' and ');
    if (Array.isArray(o.any)) return o.any.map((x) => `(${conditionText(x)})`).join(' or ');
    if ('not' in o) return `not (${conditionText(o.not)})`;
    if (typeof o.op === 'string') {
      return o.op === 'exists' || o.op === 'empty' ? `${show(o.left)} ${o.op}` : `${show(o.left)} ${o.op} ${show(o.right)}`;
    }
  }
  return show(c);
}

function detailOf(step: ScriptStep): string {
  switch (step.type) {
    case 'integration': return `${text(step.use) ?? ''}.${text(step.action) ?? ''}`;
    case 'if': return conditionText(step.when);
    case 'switch': return show(step.on);
    case 'forEach': return `${show(step.items)} as ${text(step.as) ?? ''}`;
    case 'set': return `${text(step.var) ?? ''} = ${show(step.value)}`;
    case 'log': return show(step.text);
    case 'end': return step.outputs && typeof step.outputs === 'object' ? Object.keys(step.outputs).join(', ') : '';
    default: return kindOf(step.type) === 'file' ? show(step.path) : '';
  }
}

interface Successor { kind: EdgeKind; target: string | null; caseValue?: string }

/** The step's successors in the order the core lists them (ScriptStep.Successors). */
function successors(step: ScriptStep): Successor[] {
  switch (step.type) {
    case 'if': return [{ kind: 'then', target: text(step.then) }, { kind: 'else', target: text(step.else) }];
    case 'switch': {
      const cases = step.cases && typeof step.cases === 'object' ? step.cases : {};
      return [
        ...Object.entries(cases).map(([value, target]) => ({ kind: 'case' as const, target: text(target), caseValue: value })),
        { kind: 'default', target: text(step.default) },
      ];
    }
    case 'forEach': return [{ kind: 'body', target: text(step.body) }, { kind: 'then', target: text(step.then) }];
    case 'end': return [];
    default: return [{ kind: 'next', target: text(step.next) }];
  }
}

function labelOf(step: ScriptStep, s: Successor): string | null {
  switch (s.kind) {
    case 'then': return step.type === 'forEach' ? 'done' : 'yes';
    case 'else': return 'no';
    case 'case': return s.caseValue ?? '';
    case 'default': return 'default';
    case 'body': return 'each';
    default: return null;
  }
}

function edgeId(source: string, s: { kind: EdgeKind; caseValue?: string }): string {
  return s.kind === 'case' ? `${source}->case:${s.caseValue}` : `${source}->${s.kind}`;
}

/** The ids of steps that run inside a forEach body (reached first through a body from start, as the checker walks it). */
function stepsInBodies(doc: ScriptDocument, byId: Map<string, ScriptStep>): Set<string> {
  const inBody = new Map<string, boolean>();
  const queue: string[] = [];
  if (doc.start && byId.has(doc.start)) { inBody.set(doc.start, false); queue.push(doc.start); }
  while (queue.length > 0) {
    const id = queue.shift()!;
    const step = byId.get(id)!;
    for (const s of successors(step)) {
      if (!s.target || !byId.has(s.target) || inBody.has(s.target)) continue;
      inBody.set(s.target, inBody.get(id)! || (step.type === 'forEach' && s.kind === 'body'));
      queue.push(s.target);
    }
  }
  return new Set([...inBody].filter(([, b]) => b).map(([id]) => id));
}

function layoutNodes(doc: ScriptDocument): Record<string, { x: number; y: number }> {
  const layout = doc.layout;
  if (!layout || typeof layout !== 'object') return {};
  const nodes = (layout as Record<string, unknown>).nodes;
  if (!nodes || typeof nodes !== 'object') return {};
  const out: Record<string, { x: number; y: number }> = {};
  for (const [id, p] of Object.entries(nodes as Record<string, unknown>)) {
    if (p && typeof p === 'object' && typeof (p as { x?: unknown }).x === 'number' && typeof (p as { y?: unknown }).y === 'number') {
      out[id] = { x: (p as { x: number }).x, y: (p as { y: number }).y };
    }
  }
  return out;
}

// ---- the graph

export function docToGraph(
  doc: ScriptDocument,
  opts?: { issues?: Check['issues']; run?: { taken: string[]; failed: string | null } },
): { nodes: FlowNode[]; edges: FlowEdge[] } {
  const steps = Array.isArray(doc.steps) ? doc.steps : [];
  const byId = new Map<string, ScriptStep>();
  for (const s of steps) if (!byId.has(s.id)) byId.set(s.id, s);
  const issues = opts?.issues ?? [];
  const run = opts?.run;
  const taken = new Set(run?.taken ?? []);
  const runOf = (id: string): FlowNode['run'] => (run?.failed === id ? 'failed' : taken.has(id) ? 'taken' : null);
  const at = { x: 0, y: 0 };

  const accepts = doc.trigger?.accepts ?? [];
  const nodes: FlowNode[] = [
    {
      id: START_ID, kind: 'start', stepType: '', title: 'Start', detail: accepts.join(', '), position: at,
      problems: issues.filter((i) => i.stepId === null).map((i) => i.message), run: run ? 'taken' : null,
    },
    ...[...byId.values()].map((s): FlowNode => ({
      id: s.id, kind: kindOf(s.type), stepType: s.type, title: s.id, detail: detailOf(s), position: at,
      problems: issues.filter((i) => i.stepId === s.id).map((i) => i.message), run: runOf(s.id),
    })),
    { id: END_ID, kind: 'end', stepType: '', title: 'End', detail: '', position: at, problems: [], run: null },
  ];

  const edges: FlowEdge[] = [];
  if (doc.start && byId.has(doc.start)) {
    edges.push({ id: `${START_ID}->start`, source: START_ID, target: doc.start, kind: 'next', label: null, dashed: false });
  }
  const inBodies = stepsInBodies(doc, byId);
  for (const step of byId.values()) {
    for (const s of successors(step)) {
      // A null successor ends the run, except inside a loop body (it ends that pass) and for a body itself.
      const target = s.target === null ? (inBodies.has(step.id) || s.kind === 'body' ? null : END_ID) : s.target;
      if (target === null || (target !== END_ID && !byId.has(target))) continue;
      const edge: FlowEdge = { id: edgeId(step.id, s), source: step.id, target, kind: s.kind, label: labelOf(step, s), dashed: s.kind === 'body' };
      if (s.kind === 'case') edge.caseValue = s.caseValue;
      edges.push(edge);
    }
  }

  const auto = autoLayout(nodes, edges);
  const saved = layoutNodes(doc);
  for (const n of nodes) n.position = saved[n.id] ?? auto[n.id] ?? { x: 0, y: 0 };
  return { nodes, edges };
}

/** Top-left positions from dagre, top to bottom. */
export function autoLayout(nodes: FlowNode[], edges: FlowEdge[]): Record<string, { x: number; y: number }> {
  const g = new dagre.graphlib.Graph({ multigraph: true });
  g.setGraph({ rankdir: 'TB', nodesep: 40, ranksep: 60 });
  g.setDefaultEdgeLabel(() => ({}));
  for (const n of nodes) g.setNode(n.id, { width: NODE_SIZE.width, height: NODE_SIZE.height });
  const ids = new Set(nodes.map((n) => n.id));
  for (const e of edges) if (ids.has(e.source) && ids.has(e.target)) g.setEdge(e.source, e.target, {}, e.id);
  dagre.layout(g);
  const out: Record<string, { x: number; y: number }> = {};
  for (const n of nodes) {
    const p = g.node(n.id);
    out[n.id] = { x: Math.round(p.x - NODE_SIZE.width / 2), y: Math.round(p.y - NODE_SIZE.height / 2) };
  }
  return out;
}

// ---- edits

/** Writes the node's position into `layout.nodes` (the start and end nodes too). */
export function moveNode(doc: ScriptDocument, stepId: string, pos: { x: number; y: number }): ScriptDocument {
  const next = copy(doc);
  const layout = next.layout && typeof next.layout === 'object' && !Array.isArray(next.layout) ? (next.layout as Record<string, unknown>) : {};
  const nodes = layout.nodes && typeof layout.nodes === 'object' && !Array.isArray(layout.nodes) ? (layout.nodes as Record<string, unknown>) : {};
  nodes[stepId] = { x: pos.x, y: pos.y };
  layout.nodes = nodes;
  next.layout = layout;
  return next;
}

/** Removes the layout section, so every node is placed by dagre. */
export function clearLayout(doc: ScriptDocument): ScriptDocument {
  const next = copy(doc);
  delete next.layout;
  return next;
}

/** The branches a new connection from this node can take; more than one: the UI asks which. */
export function branchesOf(doc: ScriptDocument, stepId: string): Branch[] {
  if (stepId === START_ID) return [{ kind: 'next' }];
  const step = doc.steps?.find((s) => s.id === stepId);
  if (!step) return [];
  switch (step.type) {
    case 'if': return [{ kind: 'then' }, { kind: 'else' }];
    case 'switch': return [...Object.keys(step.cases ?? {}).map((value) => ({ kind: 'case' as const, value })), { kind: 'default' }];
    case 'forEach': return [{ kind: 'body' }, { kind: 'then' }];
    case 'end': return [];
    default: return [{ kind: 'next' }];
  }
}

/** Points the source's branch (next by default) at the target; END_ID writes null; source START_ID sets doc.start. */
export function connect(doc: ScriptDocument, source: string, target: string, branch: Branch = { kind: 'next' }): ScriptDocument {
  const next = copy(doc);
  const ref = target === END_ID ? null : target;
  if (source === START_ID) {
    if (ref === null) delete next.start;
    else next.start = ref;
    return next;
  }
  const step = next.steps?.find((s) => s.id === source);
  if (!step) return next;
  if (branch.kind === 'case') step.cases = { ...(step.cases ?? {}), [branch.value]: ref };
  else step[branch.kind] = ref;
  return next;
}

/** Sets the ref the edge stands for to null (a case keeps its value); the start edge removes doc.start. */
export function removeEdge(doc: ScriptDocument, edge: FlowEdge): ScriptDocument {
  const next = copy(doc);
  if (edge.source === START_ID) {
    delete next.start;
    return next;
  }
  const step = next.steps?.find((s) => s.id === edge.source);
  if (!step) return next;
  if (edge.kind === 'case') {
    if (edge.caseValue !== undefined) step.cases = { ...(step.cases ?? {}), [edge.caseValue]: null };
  } else {
    step[edge.kind] = null;
  }
  return next;
}

/** Removes the step and its layout position. Other steps' refs to it stay, so the check reports them. */
export function removeStep(doc: ScriptDocument, stepId: string): ScriptDocument {
  const next = copy(doc);
  if (next.steps) next.steps = next.steps.filter((s) => s.id !== stepId);
  const nodes = (next.layout as { nodes?: Record<string, unknown> } | undefined)?.nodes;
  if (nodes && typeof nodes === 'object') delete nodes[stepId];
  return next;
}

/** Puts the step in place of the one with this id; a changed id takes the layout position with it. */
export function replaceStep(doc: ScriptDocument, stepId: string, step: ScriptStep): ScriptDocument {
  const next = copy(doc);
  const steps = next.steps ?? [];
  const i = steps.findIndex((s) => s.id === stepId);
  if (i < 0) return next;
  steps[i] = copy(step);
  const nodes = (next.layout as { nodes?: Record<string, unknown> } | undefined)?.nodes;
  if (step.id !== stepId && nodes && typeof nodes === 'object' && stepId in nodes) {
    nodes[step.id] = nodes[stepId];
    delete nodes[stepId];
  }
  return next;
}

/** Appends a blank step of this type with an id unique in doc.steps; a document without a start starts there. */
export function addStep(doc: ScriptDocument, type: string): { doc: ScriptDocument; id: string } {
  const next = copy(doc);
  const steps = next.steps ?? [];
  const used = new Set(steps.map((s) => s.id));
  let n = steps.length + 1;
  while (used.has(`step${n}`)) n++;
  const id = `step${n}`;
  const template = STEP_TYPES.find((t) => t.type === type)?.template ?? { next: null };
  steps.push({ ...copy(template), id, type });
  next.steps = steps;
  if (!next.start) next.start = id;
  return { doc: next, id };
}

// ---- undo and redo

export interface History {
  push(text: string): void;
  undo(): string | null;
  redo(): string | null;
  canUndo: boolean;
  canRedo: boolean;
}

/** Undo and redo over document texts; keeps the last `limit` (at least 20, 100 by default) edits. */
export function createHistory(initial: string, limit = 100): History {
  const max = Math.max(20, Math.floor(limit));
  const texts = [initial];
  let at = 0;
  return {
    push(t: string) {
      if (t === texts[at]) return;
      texts.splice(at + 1);
      texts.push(t);
      if (texts.length > max + 1) texts.splice(0, texts.length - (max + 1));
      at = texts.length - 1;
    },
    undo() {
      if (at === 0) return null;
      return texts[--at];
    },
    redo() {
      if (at >= texts.length - 1) return null;
      return texts[++at];
    },
    get canUndo() { return at > 0; },
    get canRedo() { return at < texts.length - 1; },
  };
}
