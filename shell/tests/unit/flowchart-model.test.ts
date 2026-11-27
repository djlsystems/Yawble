// The flowchart model: the workflow document as nodes and edges, and the designer's edits going back into the document.
import { describe, expect, it } from 'vitest';
import {
  END_ID, FILE_OPERATIONS, PALETTE, START_ID, STEP_TYPES, addStep, tellDetail, withFileOperation, autoLayout, branchesOf, clearLayout, connect, createHistory, docToGraph,
  moveNode, removeEdge, removeStep, replaceStep, type FlowEdge,
} from '../../src/renderer/src/workflows/flowchart';
import type { WorkflowDocument } from '../../src/renderer/src/workflows/types';

/** A fake workflow that uses every step type and every branch kind. */
function sample(): WorkflowDocument {
  return {
    format: 'yawble-workflow/1',
    id: 'sample-flow',
    name: 'Sample flow',
    trigger: { accepts: ['every', 'tell'] },
    requires: [{ name: 'mail', kind: 'email', actions: ['list'] }],
    variables: [{ name: 'messages', type: 'list<EmailMessage>' }, { name: 'n', type: 'int', initial: 0 }],
    start: 'list',
    steps: [
      { id: 'list', type: 'integration', use: 'mail', action: 'list', args: { folder: 'INBOX' }, into: 'messages', next: 'any' },
      { id: 'any', type: 'if', when: { left: '{count(vars.messages)}', op: 'gt', right: 0 }, then: 'each', else: 'kind' },
      { id: 'each', type: 'forEach', items: '{vars.messages}', as: 'm', body: 'count', then: 'note' },
      { id: 'count', type: 'set', var: 'n', value: '{vars.n + 1}', next: null },
      { id: 'note', type: 'log', text: '{vars.n} seen', next: 'kind' },
      { id: 'kind', type: 'switch', on: '{settings.mode}', cases: { quiet: 'done', loud: 'write' }, default: null },
      { id: 'write', type: 'file.writeAllText', path: 'out.txt', text: 'x', next: 'append' },
      { id: 'append', type: 'file.appendLine', path: 'run.log', text: 'x', next: 'read' },
      { id: 'read', type: 'file.readAllText', path: 'out.txt', next: 'lines' },
      { id: 'lines', type: 'file.readLines', path: 'out.txt', next: 'exists' },
      { id: 'exists', type: 'file.exists', path: 'out.txt', next: 'del' },
      { id: 'del', type: 'file.delete', path: 'out.txt', next: 'done' },
      { id: 'done', type: 'end', outputs: { seen: '{vars.n}' } },
    ],
    layout: { nodes: { list: { x: 11, y: 22 } }, zoom: 1.5 },
    custom: { kept: true },
  };
}

const edge = (edges: FlowEdge[], id: string) => edges.find((e) => e.id === id);

describe('docToGraph', () => {
  it('gives a node per step with its kind and detail, plus the start and end nodes', () => {
    const { nodes } = docToGraph(sample());
    const kinds = Object.fromEntries(nodes.map((n) => [n.id, n.kind]));
    expect(kinds).toEqual({
      [START_ID]: 'start', list: 'integration', any: 'if', each: 'forEach', count: 'set', note: 'other', kind: 'switch',
      write: 'file', append: 'file', read: 'file', lines: 'file', exists: 'file', del: 'file', done: 'end', [END_ID]: 'end',
    });
    const byId = Object.fromEntries(nodes.map((n) => [n.id, n]));
    expect(byId.list.detail).toBe('mail.list');
    expect(byId.any.detail).toBe('{count(vars.messages)} gt 0');
    expect(byId.each.detail).toBe('{vars.messages} as m');
    expect(byId.kind.detail).toBe('{settings.mode}');
    expect(byId.count.detail).toBe('n = {vars.n + 1}');
    expect(byId.write.detail).toBe('out.txt');
    expect(byId.done.stepType).toBe('end');
    expect(byId[START_ID].detail).toBe('on every, tell');
  });

  it('every STEP_TYPES entry maps to its kind, and an unknown type is other', () => {
    for (const t of STEP_TYPES) {
      const doc: WorkflowDocument = { start: 'a', steps: [{ id: 'a', ...t.template } as NonNullable<WorkflowDocument['steps']>[number]] };
      expect(docToGraph(doc).nodes.find((n) => n.id === 'a')?.kind).toBe(t.kind);
    }
    // A type the checker does not know (an agent step is not one: a workflow tells the team instead) is other.
    const odd = docToGraph({ start: 'a', steps: [{ id: 'a', type: 'mystery', next: null }, { id: 'b', type: 'agent', next: null }, { id: 'c', type: 'tell', text: 'x', next: null }] });
    expect(odd.nodes.find((n) => n.id === 'a')?.kind).toBe('other');
    expect(odd.nodes.find((n) => n.id === 'b')?.kind).toBe('other');
    expect(odd.nodes.find((n) => n.id === 'c')?.kind).toBe('tell');
  });

  it('a tell node says whom it tells, the team when it names one, and when it waits', () => {
    const doc: WorkflowDocument = { start: 'a', steps: [
      { id: 'a', type: 'tell', text: 'Look at {input.text}', next: 'b' },
      { id: 'b', type: 'tell', member: 'Ava', text: 'x', wait: true, next: 'c' },
      { id: 'c', type: 'tell', member: 'Manager', team: 'Billing', text: 'x', wait: false, next: 'd' },
      { id: 'd', type: 'tell', member: 'Ava', team: 'Billing', text: 'x', wait: true, timeout: '4h', next: null },
    ] };
    const detail = (id: string) => docToGraph(doc).nodes.find((n) => n.id === id)!.detail;
    expect(detail('a')).toBe('tell Manager');
    expect(detail('b')).toBe('tell Ava (waits)');
    expect(detail('c')).toBe('tell Manager of Billing');
    expect(detail('d')).toBe('tell Ava of Billing (waits)');
    expect(tellDetail({ id: 'x', type: 'tell', member: '  ', text: '' })).toBe('tell Manager');
    // A tell has one successor, next, like every plain step.
    expect(branchesOf(doc, 'a')).toEqual([{ kind: 'next' }]);
    expect(docToGraph(doc).edges.find((e) => e.id === 'a->next')).toMatchObject({ target: 'b', kind: 'next' });
  });

  it('the Tell block is in the Do group and adds a blank tell to the Manager that goes on at once', () => {
    const doGroup = PALETTE.find((g) => g.group === 'Do')!;
    expect(doGroup.blocks.find((b) => b.key === 'tell')).toEqual({ key: 'tell', type: 'tell', kind: 'tell', label: 'Tell' });
    const r = addStep({ start: 'a', steps: [{ id: 'a', type: 'end' }] }, 'tell');
    expect(r.doc.steps!.find((s) => s.id === r.id)).toEqual({ id: r.id, type: 'tell', member: 'Manager', text: '', next: null });
  });

  it('draws every branch kind', () => {
    const { edges } = docToGraph(sample());
    expect(edge(edges, `${START_ID}->start`)).toMatchObject({ source: START_ID, target: 'list', kind: 'next' });
    expect(edge(edges, 'list->next')).toMatchObject({ target: 'any', kind: 'next', label: null, dashed: false });
    expect(edge(edges, 'any->then')).toMatchObject({ target: 'each', kind: 'then' });
    expect(edge(edges, 'any->else')).toMatchObject({ target: 'kind', kind: 'else' });
    expect(edge(edges, 'each->body')).toMatchObject({ target: 'count', kind: 'body', dashed: true });
    expect(edge(edges, 'each->then')).toMatchObject({ target: 'note', kind: 'then', dashed: false });
    expect(edge(edges, 'kind->case:quiet')).toMatchObject({ target: 'done', kind: 'case', label: 'quiet', caseValue: 'quiet' });
    expect(edge(edges, 'kind->case:loud')).toMatchObject({ target: 'write', kind: 'case', label: 'loud', caseValue: 'loud' });
    // A null default outside a loop ends the run: it goes to the end node.
    expect(edge(edges, 'kind->default')).toMatchObject({ target: END_ID, kind: 'default', label: 'default' });
    // A null next inside a forEach body ends that pass, not the run: no edge.
    expect(edge(edges, 'count->next')).toBeUndefined();
    // An end step has no successors.
    expect(edges.filter((e) => e.source === 'done')).toEqual([]);
    expect(new Set(edges.map((e) => e.id)).size).toBe(edges.length);
  });

  it('skips a ref to a step that does not exist, and a forEach without a body', () => {
    const { edges } = docToGraph({ start: 'a', steps: [{ id: 'a', type: 'log', text: 'x', next: 'gone' }, { id: 'f', type: 'forEach', items: '', as: 'x', body: null, then: null }] });
    expect(edge(edges, 'a->next')).toBeUndefined();
    expect(edge(edges, 'f->body')).toBeUndefined();
    expect(edge(edges, 'f->then')?.target).toBe(END_ID);
  });

  it('uses layout positions over dagre, and places the rest', () => {
    const { nodes } = docToGraph(sample());
    expect(nodes.find((n) => n.id === 'list')?.position).toEqual({ x: 11, y: 22 });
    const any = nodes.find((n) => n.id === 'any')!.position;
    const each = nodes.find((n) => n.id === 'each')!.position;
    expect(Number.isFinite(any.x) && Number.isFinite(any.y)).toBe(true);
    expect(each.y).toBeGreaterThan(any.y);
  });

  it('autoLayout lays out top to bottom', () => {
    const { nodes, edges } = docToGraph({ start: 'a', steps: [{ id: 'a', type: 'log', text: '', next: 'b' }, { id: 'b', type: 'log', text: '', next: null }] });
    const p = autoLayout(nodes, edges);
    expect(p[START_ID].y).toBeLessThan(p.a.y);
    expect(p.a.y).toBeLessThan(p.b.y);
    expect(p.b.y).toBeLessThan(p[END_ID].y);
  });

  it('puts check issues on their nodes, and document-wide ones on the start node', () => {
    const { nodes } = docToGraph(sample(), {
      issues: [
        { stepId: 'any', code: 'missing-field', message: 'if needs a when' },
        { stepId: 'any', code: 'type-mismatch', message: 'second' },
        { stepId: null, code: 'duplicate', message: 'setting twice' },
      ],
    });
    const byId = Object.fromEntries(nodes.map((n) => [n.id, n]));
    expect(byId.any.problems).toEqual(['if needs a when', 'second']);
    expect(byId[START_ID].problems).toEqual(['setting twice']);
    expect(byId.list.problems).toEqual([]);
  });

  it("marks a run's taken and failed steps", () => {
    const { nodes } = docToGraph(sample(), { run: { taken: ['list', 'any', 'kind'], failed: 'kind' } });
    const byId = Object.fromEntries(nodes.map((n) => [n.id, n]));
    expect(byId.list.run).toBe('taken');
    expect(byId.any.run).toBe('taken');
    expect(byId.kind.run).toBe('failed');
    expect(byId.write.run).toBeNull();
    expect(docToGraph(sample()).nodes.every((n) => n.run === null)).toBe(true);
  });
});

describe('edits', () => {
  it('connect writes the branch into the document without changing the input', () => {
    const doc = sample();
    const before = JSON.stringify(doc);
    let d = connect(doc, 'list', 'note');
    expect(d.steps!.find((s) => s.id === 'list')!.next).toBe('note');
    d = connect(d, 'any', 'done', { kind: 'else' });
    expect(d.steps!.find((s) => s.id === 'any')!.else).toBe('done');
    d = connect(d, 'kind', 'note', { kind: 'case', value: 'other' });
    expect(d.steps!.find((s) => s.id === 'kind')!.cases).toEqual({ quiet: 'done', loud: 'write', other: 'note' });
    d = connect(d, 'kind', 'note', { kind: 'default' });
    expect(d.steps!.find((s) => s.id === 'kind')!.default).toBe('note');
    d = connect(d, 'each', 'read', { kind: 'body' });
    expect(d.steps!.find((s) => s.id === 'each')!.body).toBe('read');
    d = connect(d, 'note', END_ID);
    expect(d.steps!.find((s) => s.id === 'note')!.next).toBeNull();
    d = connect(d, START_ID, 'any');
    expect(d.start).toBe('any');
    expect(docToGraph(d).edges.find((e) => e.source === START_ID)?.target).toBe('any');
    expect(JSON.stringify(doc)).toBe(before);
    expect(d.custom).toEqual({ kept: true });
    expect(d.layout).toEqual(doc.layout);
  });

  it('removeEdge sets the ref to null; a case keeps its value', () => {
    const doc = sample();
    const { edges } = docToGraph(doc);
    let d = removeEdge(doc, edge(edges, 'any->then')!);
    expect(d.steps!.find((s) => s.id === 'any')!.then).toBeNull();
    d = removeEdge(d, edge(edges, 'kind->case:loud')!);
    expect(d.steps!.find((s) => s.id === 'kind')!.cases).toEqual({ quiet: 'done', loud: null });
    d = removeEdge(d, edge(edges, `${START_ID}->start`)!);
    expect(d.start).toBeUndefined();
    expect(doc.steps!.find((s) => s.id === 'any')!.then).toBe('each');
  });

  it('removeStep drops the step and its position; refs to it stay', () => {
    const doc = sample();
    const d = removeStep(doc, 'list');
    expect(d.steps!.some((s) => s.id === 'list')).toBe(false);
    expect(d.start).toBe('list');
    expect((d.layout as { nodes: Record<string, unknown> }).nodes.list).toBeUndefined();
    const d2 = removeStep(doc, 'note');
    expect(d2.steps!.find((s) => s.id === 'each')!.then).toBe('note');
    expect(doc.steps!.length).toBe(13);
  });

  it('replaceStep puts the new step in place', () => {
    const doc = sample();
    const d = replaceStep(doc, 'note', { id: 'note', type: 'log', text: 'changed', next: 'kind' });
    const i = d.steps!.findIndex((s) => s.id === 'note');
    expect(i).toBe(doc.steps!.findIndex((s) => s.id === 'note'));
    expect(d.steps![i].text).toBe('changed');
    expect(doc.steps![i].text).toBe('{vars.n} seen');
    const renamed = replaceStep(doc, 'list', { id: 'fetch', type: 'integration', use: 'mail', action: 'list', next: 'any' });
    expect((renamed.layout as { nodes: Record<string, unknown> }).nodes).toEqual({ fetch: { x: 11, y: 22 } });
  });

  it('moveNode writes layout.nodes, clearLayout removes the section, other keys round-trip', () => {
    const doc = sample();
    const d = moveNode(doc, 'any', { x: 5, y: 6 });
    expect(d.layout).toEqual({ nodes: { list: { x: 11, y: 22 }, any: { x: 5, y: 6 } }, zoom: 1.5 });
    expect(docToGraph(d).nodes.find((n) => n.id === 'any')?.position).toEqual({ x: 5, y: 6 });
    expect(doc.layout).toEqual({ nodes: { list: { x: 11, y: 22 } }, zoom: 1.5 });
    const fresh = moveNode({ start: 'a', steps: [] }, START_ID, { x: 1, y: 2 });
    expect(fresh.layout).toEqual({ nodes: { [START_ID]: { x: 1, y: 2 } } });
    const cleared = clearLayout(d);
    expect('layout' in cleared).toBe(false);
    const { layout: _l1, ...restD } = d;
    const { layout: _l2, ...restCleared } = cleared;
    expect(restCleared).toEqual(restD);
    expect(d.layout).toBeDefined();
  });

  it('branchesOf lists the branches a connection can take', () => {
    const doc = sample();
    expect(branchesOf(doc, 'list')).toEqual([{ kind: 'next' }]);
    expect(branchesOf(doc, 'any')).toEqual([{ kind: 'then' }, { kind: 'else' }]);
    expect(branchesOf(doc, 'each')).toEqual([{ kind: 'body' }, { kind: 'then' }]);
    expect(branchesOf(doc, 'kind')).toEqual([{ kind: 'case', value: 'quiet' }, { kind: 'case', value: 'loud' }, { kind: 'default' }]);
    expect(branchesOf(doc, 'done')).toEqual([]);
    expect(branchesOf(doc, START_ID)).toEqual([{ kind: 'next' }]);
    expect(branchesOf(doc, 'nope')).toEqual([]);
  });

  it('addStep gives an id unique in doc.steps', () => {
    const doc: WorkflowDocument = { start: 'step1', steps: [{ id: 'step1', type: 'log', next: 'step2' }, { id: 'step2', type: 'end' }] };
    const a = addStep(doc, 'if');
    expect(a.id).toBe('step3');
    expect(a.doc.steps!.at(-1)).toEqual({ id: 'step3', type: 'if', when: { left: '', op: 'eq', right: '' }, then: null, else: null });
    const b = addStep(a.doc, 'log');
    expect(b.id).toBe('step4');
    const gap = addStep({ start: 'x', steps: [{ id: 'step2', type: 'end' }] }, 'end');
    expect(gap.doc.steps!.map((s) => s.id)).toEqual(['step2', 'step3']);
    expect(doc.steps!.length).toBe(2);
    expect(a.doc.start).toBe('step1');
    expect(addStep({ steps: [] }, 'set').doc.start).toBe('step1');
  });

  it('every template is a step of its own type', () => {
    for (const t of STEP_TYPES) expect(t.template.type).toBe(t.type);
  });

  it('every palette block and file operation is a step type the checker accepts', () => {
    const known = new Set(STEP_TYPES.map((t) => t.type));
    for (const b of PALETTE.flatMap((g) => g.blocks)) expect(known.has(b.type), b.key).toBe(true);
    for (const o of FILE_OPERATIONS) expect(known.has(o.type), o.type).toBe(true);
    const fileTypes = STEP_TYPES.filter((t) => t.kind === 'file').map((t) => t.type).sort();
    expect(FILE_OPERATIONS.map((o) => o.type).sort()).toEqual(fileTypes);
  });

  it('Move, Copy and List files: their labels and blank steps', () => {
    const t = (type: string) => STEP_TYPES.find((s) => s.type === type);
    expect(t('file.move')).toEqual({ type: 'file.move', kind: 'file', label: 'Move a file', template: { type: 'file.move', path: '', to: '', next: null } });
    expect(t('file.copy')).toEqual({ type: 'file.copy', kind: 'file', label: 'Copy a file', template: { type: 'file.copy', path: '', to: '', next: null } });
    expect(t('file.list')).toEqual({ type: 'file.list', kind: 'file', label: 'List files', template: { type: 'file.list', path: '', pattern: '*', next: null } });
    const op = (type: string) => FILE_OPERATIONS.find((o) => o.type === type);
    expect(op('file.move')).toMatchObject({ writes: true, reads: false, into: true, fields: ['to', 'overwrite'] });
    expect(op('file.copy')).toMatchObject({ writes: true, reads: false, into: true, fields: ['to', 'overwrite'] });
    expect(op('file.list')).toMatchObject({ writes: false, reads: true, into: true, fields: ['pattern', 'recursive'] });
  });
});

describe('withFileOperation', () => {
  const read = { id: 'f', type: 'file.readAllText', path: '{settings.p}', into: 'body', next: 'n' };
  it('keeps the path and next; a write gains text and loses into', () => {
    expect(withFileOperation(read, 'file.writeAllText')).toEqual({ id: 'f', type: 'file.writeAllText', path: '{settings.p}', text: '', next: 'n' });
  });
  it('a read keeps into; a delete drops text and into', () => {
    expect(withFileOperation(read, 'file.exists')).toEqual({ ...read, type: 'file.exists' });
    expect(withFileOperation({ ...read, type: 'file.appendLine', text: 'x' }, 'file.delete')).toEqual({ id: 'f', type: 'file.delete', path: '{settings.p}', next: 'n' });
  });
  it('move and copy gain to, keep into and drop text; overwrite stays off until checked', () => {
    expect(withFileOperation({ id: 'f', type: 'file.writeAllText', path: '{settings.p}', text: 'x', next: 'n' }, 'file.move')).toEqual({ id: 'f', type: 'file.move', path: '{settings.p}', to: '', next: 'n' });
    expect(withFileOperation(read, 'file.copy')).toEqual({ ...read, type: 'file.copy', to: '' });
    const move = { id: 'f', type: 'file.move', path: 'a', to: 'b', overwrite: true, next: 'n' };
    expect(withFileOperation(move, 'file.copy')).toEqual({ ...move, type: 'file.copy' });
    expect(withFileOperation(move, 'file.delete')).toEqual({ id: 'f', type: 'file.delete', path: 'a', next: 'n' });
  });
  it('list gains pattern *, drops to and overwrite; leaving it drops pattern and recursive', () => {
    const move = { id: 'f', type: 'file.move', path: 'in', to: 'b', overwrite: true, into: 'p', next: 'n' };
    expect(withFileOperation(move, 'file.list')).toEqual({ id: 'f', type: 'file.list', path: 'in', pattern: '*', into: 'p', next: 'n' });
    const list = { id: 'f', type: 'file.list', path: 'in', pattern: '*.jpg', recursive: true, next: 'n' };
    expect(withFileOperation(list, 'file.readLines')).toEqual({ id: 'f', type: 'file.readLines', path: 'in', next: 'n' });
  });
  it('an unknown type or the same one leaves the step as it is', () => {
    expect(withFileOperation(read, 'log')).toBe(read);
    expect(withFileOperation(read, 'file.readAllText')).toBe(read);
  });
});

describe('createHistory', () => {
  it('undoes and redoes over 25 pushes, at least 20 of them', () => {
    const h = createHistory('v0', 20);
    expect(h.canUndo).toBe(false);
    for (let i = 1; i <= 25; i++) h.push(`v${i}`);
    const undone: string[] = [];
    let t: string | null;
    while ((t = h.undo()) !== null) undone.push(t);
    expect(undone.length).toBeGreaterThanOrEqual(20);
    expect(undone[0]).toBe('v24');
    expect(h.canUndo).toBe(false);
    expect(h.canRedo).toBe(true);
    const redone: string[] = [];
    while ((t = h.redo()) !== null) redone.push(t);
    expect(redone.at(-1)).toBe('v25');
    expect(redone.length).toBe(undone.length);
    expect(h.canRedo).toBe(false);
  });

  it('a push after undo drops the redo branch; the same text twice is one entry; limit is at least 20', () => {
    const h = createHistory('a', 3);
    h.push('b');
    h.push('b');
    h.push('c');
    expect(h.undo()).toBe('b');
    h.push('d');
    expect(h.canRedo).toBe(false);
    expect(h.undo()).toBe('b');
    expect(h.undo()).toBe('a');
    expect(h.undo()).toBeNull();
    const small = createHistory('0', 1);
    for (let i = 1; i <= 25; i++) small.push(String(i));
    let n = 0;
    while (small.undo() !== null) n++;
    expect(n).toBe(20);
  });
});
