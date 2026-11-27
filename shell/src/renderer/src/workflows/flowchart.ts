// The workflow document as a flowchart: nodes and edges for the designer, and the designer's edits as pure functions
// that return a new document and keep every key they do not edit. Node positions live in `layout.nodes[stepId]`; a
// step without one is placed by dagre. Successors follow the core's checker (Yawble.Core.Workflows.Language): if: then
// / else; switch: cases / default; forEach and while: body / then; end: none; any other step: next. A null successor
// ends the run, or inside a loop body ends that pass.
import dagre from '@dagrejs/dagre';
import type { Check, WorkflowDocument, WorkflowStep } from './types';

export type FlowKind = 'start' | 'end' | 'integration' | 'if' | 'switch' | 'forEach' | 'while' | 'set' | 'file' | 'tell' | 'code' | 'other';
export type EdgeKind = 'next' | 'then' | 'else' | 'case' | 'default' | 'body';

export interface FlowNode {
  id: string;
  kind: FlowKind;
  /** The step's `type` ('' for the start and end nodes). */
  stepType: string;
  title: string;
  /** integration: use.action; if: the condition; switch: the value it branches on; forEach: "items as x"; while: the condition; tell: "tell Ava (waits)"; code: "code → Type". */
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
  /** A loop body (forEach or while). */
  dashed: boolean;
}

/** The start node: its edge is the document's `start`. */
export const START_ID = '__start';
/** The end node: a successor that is null (outside a loop body) ends the run here. */
export const END_ID = '__end';

/** The size dagre lays nodes out with. */
export const NODE_SIZE = { width: 220, height: 88 } as const;
const LAYOUT = { nodesep: 40, ranksep: 60 } as const;

export type Branch = { kind: 'next' | 'then' | 'else' | 'default' | 'body' } | { kind: 'case'; value: string };

// ---- step types

/** The step types the checker accepts, with a blank step of each for the designer's palette. */
export const STEP_TYPES: readonly { type: string; kind: FlowKind; label: string; template: Omit<WorkflowStep, 'id'> }[] = [
  { type: 'integration', kind: 'integration', label: 'Call an integration', template: { type: 'integration', use: '', action: '', args: {}, next: null } },
  { type: 'if', kind: 'if', label: 'If', template: { type: 'if', when: { left: '', op: 'eq', right: '' }, then: null, else: null } },
  { type: 'switch', kind: 'switch', label: 'Switch', template: { type: 'switch', on: '', cases: {}, default: null } },
  { type: 'forEach', kind: 'forEach', label: 'For each', template: { type: 'forEach', items: '', as: 'item', body: null, then: null } },
  { type: 'while', kind: 'while', label: 'Loop', template: { type: 'while', when: { left: '', op: 'eq', right: '' }, body: null, then: null } },
  { type: 'set', kind: 'set', label: 'Set a variable', template: { type: 'set', var: '', value: '', next: null } },
  { type: 'log', kind: 'other', label: 'Log a line', template: { type: 'log', text: '', next: null } },
  { type: 'file.writeAllText', kind: 'file', label: 'Write a file', template: { type: 'file.writeAllText', path: '', text: '', next: null } },
  { type: 'file.appendLine', kind: 'file', label: 'Append a line to a file', template: { type: 'file.appendLine', path: '', text: '', next: null } },
  { type: 'file.readAllText', kind: 'file', label: 'Read a file', template: { type: 'file.readAllText', path: '', next: null } },
  { type: 'file.readLines', kind: 'file', label: "Read a file's lines", template: { type: 'file.readLines', path: '', next: null } },
  { type: 'file.exists', kind: 'file', label: 'Check a file exists', template: { type: 'file.exists', path: '', next: null } },
  { type: 'file.delete', kind: 'file', label: 'Delete a file', template: { type: 'file.delete', path: '', next: null } },
  { type: 'file.move', kind: 'file', label: 'Move a file', template: { type: 'file.move', path: '', to: '', next: null } },
  { type: 'file.copy', kind: 'file', label: 'Copy a file', template: { type: 'file.copy', path: '', to: '', next: null } },
  { type: 'file.list', kind: 'file', label: 'List files', template: { type: 'file.list', path: '', pattern: '*', next: null } },
  { type: 'tell', kind: 'tell', label: 'Tell a team member', template: { type: 'tell', member: 'Manager', text: '', next: null } },
  { type: 'code', kind: 'code', label: 'Code', template: { type: 'code', inputs: {}, code: '', returns: 'any', next: null } },
  { type: 'end', kind: 'end', label: 'End', template: { type: 'end' } },
];

/** A file step's fields besides path, into and next. */
export type FileField = 'text' | 'to' | 'overwrite' | 'pattern' | 'recursive';

/** The value a field gets when an operation switch adds it (overwrite and recursive are off when absent). */
const FILE_FIELD_DEFAULTS: Partial<Record<FileField, unknown>> = { text: '', to: '', pattern: '*' };

/**
 * The file step types: one File block on the palette, the operation picked on its form. writes: it changes files;
 * reads: it reads them; into: it has a result to save; fields: the ones besides path, into and next.
 */
export const FILE_OPERATIONS: readonly { type: string; label: string; writes: boolean; reads: boolean; into: boolean; fields: readonly FileField[] }[] = [
  { type: 'file.readAllText', label: 'Read the text', writes: false, reads: true, into: true, fields: [] },
  { type: 'file.readLines', label: 'Read the lines', writes: false, reads: true, into: true, fields: [] },
  { type: 'file.writeAllText', label: 'Write the text', writes: true, reads: false, into: false, fields: ['text'] },
  { type: 'file.appendLine', label: 'Append a line', writes: true, reads: false, into: false, fields: ['text'] },
  { type: 'file.exists', label: 'Check it exists', writes: false, reads: true, into: true, fields: [] },
  { type: 'file.delete', label: 'Delete it', writes: true, reads: false, into: false, fields: [] },
  { type: 'file.move', label: 'Move it', writes: true, reads: false, into: true, fields: ['to', 'overwrite'] },
  { type: 'file.copy', label: 'Copy it', writes: true, reads: false, into: true, fields: ['to', 'overwrite'] },
  { type: 'file.list', label: 'List the files in a folder', writes: false, reads: true, into: true, fields: ['pattern', 'recursive'] },
];

const ALL_FILE_FIELDS: readonly FileField[] = ['text', 'to', 'overwrite', 'pattern', 'recursive'];

/** The file operation of a step type (undefined for any other type). */
export function fileOperation(type: string) {
  return FILE_OPERATIONS.find((o) => o.type === type);
}

/** The palette: the blocks in groups. Each block adds a step of its type (File adds a read; its form picks the operation). */
export const PALETTE: readonly { group: string; blocks: readonly { key: string; type: string; kind: FlowKind; label: string }[] }[] = [
  { group: 'Flow', blocks: [
    { key: 'if', type: 'if', kind: 'if', label: 'If / else' },
    { key: 'switch', type: 'switch', kind: 'switch', label: 'Switch' },
    { key: 'forEach', type: 'forEach', kind: 'forEach', label: 'For each' },
    { key: 'while', type: 'while', kind: 'while', label: 'Loop' },
    { key: 'end', type: 'end', kind: 'end', label: 'End' },
  ] },
  { group: 'Data', blocks: [
    { key: 'set', type: 'set', kind: 'set', label: 'Set a variable' },
  ] },
  { group: 'Do', blocks: [
    { key: 'integration', type: 'integration', kind: 'integration', label: 'Integration' },
    { key: 'file', type: 'file.readAllText', kind: 'file', label: 'File' },
    { key: 'log', type: 'log', kind: 'other', label: 'Log' },
    { key: 'tell', type: 'tell', kind: 'tell', label: 'Tell' },
    { key: 'code', type: 'code', kind: 'code', label: 'Code' },
  ] },
];

/**
 * A file step switched to another operation: the path stays; the new operation's fields are kept or added with their
 * defaults (text '', to '', pattern '*'; overwrite and recursive stay off), the others go; into only where it has a result.
 */
export function withFileOperation(step: WorkflowStep, type: string): WorkflowStep {
  const op = fileOperation(type);
  if (!op || step.type === type) return step;
  const next: WorkflowStep = { ...step, type };
  for (const f of ALL_FILE_FIELDS) {
    if (!op.fields.includes(f)) delete next[f];
    else if (!(f in next) && f in FILE_FIELD_DEFAULTS) next[f] = FILE_FIELD_DEFAULTS[f];
  }
  if (!op.into) delete next.into;
  return next;
}

function kindOf(type: string): FlowKind {
  const known = STEP_TYPES.find((t) => t.type === type);
  return known ? known.kind : 'other';
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

/** "tell Manager", "tell Ava (waits)"; another team adds it: "tell Manager of Billing". */
export function tellDetail(step: WorkflowStep): string {
  const member = text(step.member)?.trim() || 'Manager';
  const team = text(step.team)?.trim();
  return `tell ${member}${team ? ` of ${team}` : ''}${step.wait === true ? ' (waits)' : ''}`;
}

/** "code → Staleness": the type the code returns (any when none is given). */
export function codeDetail(step: WorkflowStep): string {
  return `code → ${text(step.returns)?.trim() || 'any'}`;
}

function detailOf(step: WorkflowStep): string {
  switch (step.type) {
    case 'integration': return `${text(step.use) ?? ''}.${text(step.action) ?? ''}`;
    case 'if': return conditionText(step.when);
    case 'switch': return show(step.on);
    case 'forEach': return `${show(step.items)} as ${text(step.as) ?? ''}`;
    case 'while': return conditionText(step.when);
    case 'set': return `${text(step.var) ?? ''} = ${show(step.value)}`;
    case 'log': return show(step.text);
    case 'tell': return tellDetail(step);
    case 'code': return codeDetail(step);
    case 'end': return step.outputs && typeof step.outputs === 'object' ? Object.keys(step.outputs).join(', ') : '';
    case 'file.move':
    case 'file.copy': return `${show(step.path)} → ${show(step.to)}`;
    default: return kindOf(step.type) === 'file' ? show(step.path) : '';
  }
}

interface Successor { kind: EdgeKind; target: string | null; caseValue?: string }

/** The step's successors in the order the core lists them (WorkflowStep.Successors). */
function successors(step: WorkflowStep): Successor[] {
  switch (step.type) {
    case 'if': return [{ kind: 'then', target: text(step.then) }, { kind: 'else', target: text(step.else) }];
    case 'switch': {
      const cases = step.cases && typeof step.cases === 'object' ? step.cases : {};
      return [
        ...Object.entries(cases).map(([value, target]) => ({ kind: 'case' as const, target: text(target), caseValue: value })),
        { kind: 'default', target: text(step.default) },
      ];
    }
    case 'forEach':
    case 'while': return [{ kind: 'body', target: text(step.body) }, { kind: 'then', target: text(step.then) }];
    case 'end': return [];
    default: return [{ kind: 'next', target: text(step.next) }];
  }
}

function labelOf(step: WorkflowStep, s: Successor): string | null {
  switch (s.kind) {
    case 'then': return isLoop(step.type) ? 'done' : 'yes';
    case 'else': return 'no';
    case 'case': return s.caseValue ?? '';
    case 'default': return 'default';
    case 'body': return step.type === 'while' ? 'repeat' : 'each';
    default: return null;
  }
}

function edgeId(source: string, s: { kind: EdgeKind; caseValue?: string }): string {
  return s.kind === 'case' ? `${source}->case:${s.caseValue}` : `${source}->${s.kind}`;
}

/** A loop step: forEach or while (body / then). */
function isLoop(type: string): boolean {
  return type === 'forEach' || type === 'while';
}

/** The ids of steps that run inside a loop body (reached first through a body from start, as the checker walks it). */
function stepsInBodies(doc: WorkflowDocument, byId: Map<string, WorkflowStep>): Set<string> {
  const inBody = new Map<string, boolean>();
  const queue: string[] = [];
  if (doc.start && byId.has(doc.start)) { inBody.set(doc.start, false); queue.push(doc.start); }
  while (queue.length > 0) {
    const id = queue.shift()!;
    const step = byId.get(id)!;
    for (const s of successors(step)) {
      if (!s.target || !byId.has(s.target) || inBody.has(s.target)) continue;
      inBody.set(s.target, inBody.get(id)! || (isLoop(step.type) && s.kind === 'body'));
      queue.push(s.target);
    }
  }
  return new Set([...inBody].filter(([, b]) => b).map(([id]) => id));
}

function layoutNodes(doc: WorkflowDocument): Record<string, { x: number; y: number }> {
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
  doc: WorkflowDocument,
  opts?: { issues?: Check['issues']; run?: { taken: string[]; failed: string | null } },
): { nodes: FlowNode[]; edges: FlowEdge[] } {
  const steps = Array.isArray(doc.steps) ? doc.steps : [];
  const byId = new Map<string, WorkflowStep>();
  for (const s of steps) if (!byId.has(s.id)) byId.set(s.id, s);
  const issues = opts?.issues ?? [];
  const run = opts?.run;
  const taken = new Set(run?.taken ?? []);
  const runOf = (id: string): FlowNode['run'] => (run?.failed === id ? 'failed' : taken.has(id) ? 'taken' : null);
  const at = { x: 0, y: 0 };

  const accepts = doc.trigger?.accepts ?? [];
  const nodes: FlowNode[] = [
    {
      id: START_ID, kind: 'start', stepType: '', title: 'Start', detail: accepts.length ? `on ${accepts.join(', ')}` : 'no trigger yet', position: at,
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
  g.setGraph({ rankdir: 'TB', ...LAYOUT });
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
  // The end node goes below everything else: under its steps when some go to it, else under the start node.
  if (out[END_ID]) {
    const others = nodes.filter((n) => n.id !== END_ID).map((n) => out[n.id]!);
    const bottom = others.length ? Math.max(...others.map((p) => p.y)) : 0;
    const into = edges.filter((e) => e.target === END_ID && out[e.source]);
    const x = into.length ? Math.round(into.reduce((sum, e) => sum + out[e.source]!.x, 0) / into.length) : out[START_ID]?.x ?? out[END_ID].x;
    out[END_ID] = { x, y: bottom + NODE_SIZE.height + LAYOUT.ranksep };
  }
  return out;
}

// ---- edits

/** Writes the node's position into `layout.nodes` (the start and end nodes too). */
export function moveNode(doc: WorkflowDocument, stepId: string, pos: { x: number; y: number }): WorkflowDocument {
  const next = copy(doc);
  const layout = next.layout && typeof next.layout === 'object' && !Array.isArray(next.layout) ? (next.layout as Record<string, unknown>) : {};
  const nodes = layout.nodes && typeof layout.nodes === 'object' && !Array.isArray(layout.nodes) ? (layout.nodes as Record<string, unknown>) : {};
  nodes[stepId] = { x: pos.x, y: pos.y };
  layout.nodes = nodes;
  next.layout = layout;
  return next;
}

/** Removes the layout section, so every node is placed by dagre. */
export function clearLayout(doc: WorkflowDocument): WorkflowDocument {
  const next = copy(doc);
  delete next.layout;
  return next;
}

/** The branches a new connection from this node can take; more than one: the UI asks which. */
export function branchesOf(doc: WorkflowDocument, stepId: string): Branch[] {
  if (stepId === START_ID) return [{ kind: 'next' }];
  const step = doc.steps?.find((s) => s.id === stepId);
  if (!step) return [];
  switch (step.type) {
    case 'if': return [{ kind: 'then' }, { kind: 'else' }];
    case 'switch': return [...Object.keys(step.cases ?? {}).map((value) => ({ kind: 'case' as const, value })), { kind: 'default' }];
    case 'forEach':
    case 'while': return [{ kind: 'body' }, { kind: 'then' }];
    case 'end': return [];
    default: return [{ kind: 'next' }];
  }
}

/** Points the source's branch (next by default) at the target; END_ID writes null; source START_ID sets doc.start. */
export function connect(doc: WorkflowDocument, source: string, target: string, branch: Branch = { kind: 'next' }): WorkflowDocument {
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
export function removeEdge(doc: WorkflowDocument, edge: FlowEdge): WorkflowDocument {
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
export function removeStep(doc: WorkflowDocument, stepId: string): WorkflowDocument {
  const next = copy(doc);
  if (next.steps) next.steps = next.steps.filter((s) => s.id !== stepId);
  const nodes = (next.layout as { nodes?: Record<string, unknown> } | undefined)?.nodes;
  if (nodes && typeof nodes === 'object') delete nodes[stepId];
  return next;
}

/** Puts the step in place of the one with this id; a changed id takes the layout position with it. */
export function replaceStep(doc: WorkflowDocument, stepId: string, step: WorkflowStep): WorkflowDocument {
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
export function addStep(doc: WorkflowDocument, type: string): { doc: WorkflowDocument; id: string } {
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
