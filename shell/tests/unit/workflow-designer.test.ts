// @vitest-environment happy-dom
// The designer by hand: the catalog's pure helpers (derived requires, expression suggestions, typed in and out), a form
// per step type, the Workflow pane, the JSON tabs, Use real names, a member's overrides, the minimap's toggle and the End
// node at the bottom. Vue Flow's canvas needs a real layout engine, so it is replaced by a stub that renders each node
// through the component's own node slot. Fakes only: no core; made-up names and values only.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { enableAutoUnmount, flushPromises, mount, type DOMWrapper, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AgentCard, TeamSummary, YawbleApi } from '../../src/shared/api';
import WorkflowMemberPane from '../../src/renderer/src/components/WorkflowMemberPane.vue';
import WorkflowEditor from '../../src/renderer/src/components/workflows/WorkflowEditor.vue';
import WorkflowsLibrary from '../../src/renderer/src/components/workflows/WorkflowsLibrary.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { BLANK_WORKFLOW, INBOX_SMS_ALERT as TEMPLATE, pretty } from '../../src/renderer/src/workflows/api';
import {
  applySuggestion, catalogApi, deriveRequires, expressionSuggestions, ioInText, ioOutText, isAliasDocument, schemaFields, stepIO,
  suggestAt, withDerivedRequires, type DesignContext, type WorkflowCatalog, type SchemaFields,
} from '../../src/renderer/src/workflows/catalog';
import { autoLayout, docToGraph, END_ID, START_ID } from '../../src/renderer/src/workflows/flowchart';
import type { Check, WorkflowDocument } from '../../src/renderer/src/workflows/types';

vi.mock('@vue-flow/core', async () => {
  const { defineComponent, h } = await import('vue');
  const VueFlow = defineComponent({
    name: 'VueFlowStub',
    props: { nodes: { type: Array, default: () => [] }, edges: { type: Array, default: () => [] }, nodesDraggable: Boolean, nodesConnectable: Boolean },
    emits: ['node-click', 'edge-click', 'pane-click', 'node-drag-stop', 'connect'],
    setup(props, { slots, emit }) {
      return () => h('div', { 'data-test': 'vf' }, [
        ...(props.nodes as any[]).map((n) => h('div', { class: 'vf-node', 'data-id': n.id, onClick: () => emit('node-click', { node: n, event: {} }) },
          slots['node-step']?.({ id: n.id, data: n.data }))),
        h('div', { 'data-test': 'vf-pane', onClick: () => emit('pane-click', {}) }),
        slots.default?.(),
      ]);
    },
  });
  const Handle = defineComponent({ name: 'HandleStub', setup: () => () => null });
  return { VueFlow, Handle, Position: { Top: 'top', Bottom: 'bottom' }, MarkerType: { ArrowClosed: 'arrowclosed' } };
});
vi.mock('@vue-flow/background', async () => {
  const { defineComponent } = await import('vue');
  return { Background: defineComponent({ name: 'BackgroundStub', setup: () => () => null }) };
});
vi.mock('@vue-flow/controls', async () => {
  const { defineComponent, h } = await import('vue');
  return { Controls: defineComponent({ name: 'ControlsStub', setup: () => () => h('div', { 'data-test': 'vf-controls' }) }) };
});
vi.mock('@vue-flow/minimap', async () => {
  const { defineComponent, h } = await import('vue');
  return { MiniMap: defineComponent({ name: 'MiniMapStub', setup: () => () => h('div', { 'data-test': 'vf-minimap' }) }) };
});

enableAutoUnmount(afterEach);

const at = '2026-11-23T10:00:00Z';
const teams: TeamSummary[] = [{ id: 1, name: 'Support', repoPath: null, members: 2, running: 0, queued: 0 }];
const clean: Check = { ok: true, issues: [], schemaReferences: [] };

const CATALOG: WorkflowCatalog = {
  integrations: [
    { name: 'Example Support Inbox', kind: 'email', enabledForTeam: true, actions: [
      { name: 'list', description: 'New messages in a folder', result: 'EmailList', params: [
        { name: 'folder', type: 'string', required: true }, { name: 'after', type: 'string', required: false },
        { name: 'since', type: 'datetime', required: false }, { name: 'max', type: 'int', required: false },
      ] },
      { name: 'get', result: 'EmailMessage', params: [{ name: 'id', type: 'string', required: true }] },
      { name: 'markRead', result: 'object', params: [{ name: 'id', type: 'string', required: true }] },
    ] },
    { name: 'Other Inbox', kind: 'email', enabledForTeam: true, actions: [
      { name: 'list', result: 'EmailList', params: [{ name: 'folder', type: 'string', required: true }] },
    ] },
    { name: 'Example Texting', kind: 'api', enabledForTeam: true, actions: [
      { name: 'CreateMessage', result: 'object', params: [
        { name: 'To', type: 'string', required: true }, { name: 'From', type: 'string', required: true }, { name: 'Body', type: 'string', required: true },
      ] },
      { name: 'DeleteMessage', result: 'object', ask: true, params: [{ name: 'Sid', type: 'string', required: true }] },
    ] },
  ],
  functions: [
    { name: 'count', signature: 'count(list) -> int', description: 'How many items' },
    { name: 'last', signature: 'last(list) -> any', description: 'The last item' },
    { name: 'now', signature: 'now() -> datetime', description: 'The time now' },
  ],
};

const SCHEMAS: Record<string, Record<string, unknown>> = {
  EmailList: { type: 'object', properties: { messages: { type: 'array', items: { $ref: '#/$defs/EmailMessage' } } }, required: ['messages'] },
  EmailMessage: { type: 'object', required: ['id'], properties: {
    id: { type: 'string', description: 'The message id' }, subject: { type: 'string' }, from: { $ref: 'EmailAddress' },
    receivedAt: { type: 'string', format: 'date-time' }, flag: { type: 'string', enum: ['none', 'flagged'] },
  } },
  EmailAddress: { type: 'object', properties: { address: { type: 'string' }, name: { type: 'string' } } },
};
const FIELDS: SchemaFields = Object.fromEntries(Object.entries(SCHEMAS).map(([k, v]) => [k, schemaFields(v)]));

/**
 * The template in the alias form (mail, sms): the shipped template names its integrations by real names ("Support inbox",
 * "Twilio"), which this catalog does not have, so the tests rename them to aliases a member's bindings resolve.
 */
const INBOX_SMS_ALERT: WorkflowDocument = (() => {
  const alias: Record<string, string> = { 'Support inbox': 'mail', Twilio: 'sms' };
  const d = JSON.parse(JSON.stringify(TEMPLATE)) as WorkflowDocument;
  for (const s of d.steps!) if (typeof s.use === 'string' && alias[s.use]) s.use = alias[s.use];
  d.requires = (d.requires ?? []).map((r) => ({ ...r, name: alias[r.name] ?? r.name }));
  return d;
})();

/** The template in the real-name form. */
function realDoc(): WorkflowDocument {
  const d = JSON.parse(JSON.stringify(INBOX_SMS_ALERT)) as WorkflowDocument;
  for (const s of d.steps!) {
    if (s.use === 'mail') s.use = 'Example Support Inbox';
    if (s.use === 'sms') s.use = 'Example Texting';
  }
  d.requires = [
    { name: 'Example Support Inbox', kind: 'email', actions: ['list'] },
    { name: 'Example Texting', kind: 'api', actions: ['CreateMessage'] },
  ];
  return d;
}

interface Call { method: string; params: any }
function fakeApi(opts: { doc?: WorkflowDocument; member?: any; realNames?: any; more?: Record<string, (p: any) => unknown> } = {}) {
  const calls: Call[] = [];
  const handlers: Record<string, (p: any) => unknown> = {
    'workflows.get': (p) => ({ id: 1, key: 'inbox-sms-alert', name: 'Inbox SMS alert', description: '', version: p.version ?? 3, ok: true,
      document: opts.doc ?? realDoc(), check: clean, createdAt: at }),
    'workflows.versions': () => [{ version: 3, ok: true, createdAt: at }],
    'workflows.check': () => clean,
    'workflows.catalog': () => CATALOG,
    'schemas.list': () => Object.keys(SCHEMAS).map((name) => ({ name, description: '', latestVersion: 1, builtIn: true, updatedAt: at })),
    'schemas.get': (p) => ({ name: p.name, description: '', version: 1, builtIn: true, schema: SCHEMAS[p.name], createdAt: at }),
    'workflows.memberGet': () => opts.member,
    'workflows.useRealNames': () => opts.realNames,
    ...opts.more,
  };
  const api = {
    getInfo: async () => ({}), getAvailableBackends: async () => [], onEvent: () => () => {}, openTerminal: async () => {}, pickFolder: async () => null,
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      const h = handlers[method];
      if (!h) throw new Error(`no fake for ${method}`);
      return h(params);
    },
  } as unknown as YawbleApi;
  return { api, calls, of: (m: string) => calls.filter((c) => c.method === m) };
}

async function editor(api: YawbleApi, props: Record<string, unknown> = {}) {
  const w = mount(WorkflowEditor, { props: { api, teams, workflow: { id: 1, key: 'inbox-sms-alert' }, ...props }, attachTo: document.body });
  await flushPromises();
  await w.get('[data-test=editor-tab-flowchart]').trigger('click');
  await flushPromises();
  return w;
}
const jsonText = (w: VueWrapper) => (w.get('[data-test=json-text]').element as HTMLTextAreaElement).value;
const docOf = (w: VueWrapper) => JSON.parse(jsonText(w)) as WorkflowDocument;
const stepOf = (w: VueWrapper, id: string) => docOf(w).steps!.find((s) => s.id === id)!;
const pick = async (w: VueWrapper, id: string) => { await w.get(`.vf-node[data-id="${id}"]`).trigger('click'); await flushPromises(); };
// What get() answers: an element that is there.
type Found = Omit<DOMWrapper<Element>, 'exists'>;
const expr = (w: VueWrapper | Found, name: string) => w.get(`[data-test=expr-field][data-name="${name}"] [data-test=expr-input]`);
async function typeIn(el: Found, value: string) { await el.setValue(value); await flushPromises(); }
async function changeTo(el: Found, value: string) { await el.setValue(value); await el.trigger('change'); await flushPromises(); }

beforeEach(() => { localStorage.clear(); applyRemoteAccess('full'); });
afterEach(() => { applyRemoteAccess('full'); document.body.innerHTML = ''; });

// ---- the pure helpers

describe('catalog helpers', () => {
  const ctx = (doc: WorkflowDocument): DesignContext => ({ doc, catalog: CATALOG, schemas: FIELDS });

  it('catalogApi calls workflows.catalog and workflows.useRealNames with the core\'s parameters', async () => {
    const f = fakeApi({ realNames: { document: realDoc(), renamed: {}, unresolved: [] } });
    const c = catalogApi(f.api);
    await c.catalog();
    await c.catalog(4);
    await c.useRealNames('{}');
    await c.useRealNames('{}', { mail: 'Other Inbox' });
    expect(f.calls.map((x) => [x.method, x.params])).toEqual([
      ['workflows.catalog', {}], ['workflows.catalog', { teamId: 4 }],
      ['workflows.useRealNames', { document: '{}' }], ['workflows.useRealNames', { document: '{}', bindings: { mail: 'Other Inbox' } }],
    ]);
  });

  it('derives requires: one entry per integration a step names, its kind, and the distinct actions sorted', () => {
    const doc: WorkflowDocument = { requires: [{ name: 'texts', kind: 'api', actions: [] }], steps: [
      { id: 'a', type: 'integration', use: 'Example Support Inbox', action: 'list' },
      { id: 'b', type: 'integration', use: 'Example Texting', action: 'CreateMessage' },
      { id: 'c', type: 'integration', use: 'Example Support Inbox', action: 'markRead' },
      { id: 'd', type: 'integration', use: 'Example Support Inbox', action: 'list' },
      { id: 'e', type: 'integration', use: 'texts', action: 'Send' },
      { id: 'f', type: 'integration', use: 'inbox', action: 'get' },
      { id: 'g', type: 'integration', use: '', action: 'list' },
      { id: 'h', type: 'set', var: 'x', value: 1 },
    ] };
    expect(deriveRequires(doc, CATALOG)).toEqual([
      { name: 'Example Support Inbox', kind: 'email', actions: ['list', 'markRead'] },
      { name: 'Example Texting', kind: 'api', actions: ['CreateMessage'] },
      { name: 'texts', kind: 'api', actions: ['Send'] }, // the document's own kind for an alias
      { name: 'inbox', kind: 'email', actions: ['get'] }, // only Email actions: an Email integration
    ]);
  });

  it('derives requires again only when a step\'s use or action changed', () => {
    const before = realDoc();
    const renamed = { ...before, name: 'Renamed', requires: [] };
    expect(withDerivedRequires(before, renamed, CATALOG).requires).toEqual([]);
    const changed = JSON.parse(JSON.stringify(before)) as WorkflowDocument;
    changed.steps!.find((s) => s.id === 'list')!.action = 'get';
    expect(withDerivedRequires(before, changed, CATALOG).requires).toEqual([
      { name: 'Example Support Inbox', kind: 'email', actions: ['get'] },
      { name: 'Example Texting', kind: 'api', actions: ['CreateMessage'] },
    ]);
  });

  it('an alias document is one whose requires or uses are not integrations\' names', () => {
    expect(isAliasDocument(INBOX_SMS_ALERT, CATALOG)).toBe(true);
    expect(isAliasDocument(realDoc(), CATALOG)).toBe(false);
    expect(isAliasDocument(INBOX_SMS_ALERT, null)).toBe(false);
  });

  it('reads a schema\'s fields with type, required, description, enum and references', () => {
    expect(FIELDS.EmailMessage).toEqual([
      { name: 'id', type: 'string', required: true, description: 'The message id' },
      { name: 'subject', type: 'string', required: false },
      { name: 'from', type: 'EmailAddress', required: false },
      { name: 'receivedAt', type: 'datetime', required: false },
      { name: 'flag', type: 'string', required: false, enum: ['none', 'flagged'] },
    ]);
    expect(FIELDS.EmailList![0]!.type).toBe('list<EmailMessage>');
  });

  it('suggests settings, variables and their fields, the loop items in scope with theirs, and the functions', () => {
    const c = ctx(realDoc());
    const inLoop = expressionSuggestions(c, 'note').map((s) => s.text);
    expect(inLoop).toEqual(expect.arrayContaining(['settings.folder', 'settings.recipients', 'vars.res', 'vars.res.messages', 'vars.last.subject',
      'vars.last.from.address', 'm', 'm.subject', 'm.from.address', 'count', 'now']));
    // phones is a loop inside m's loop: inside it, both items are in scope.
    const inner = expressionSuggestions(c, 'text');
    expect(inner.find((s) => s.text === 'to')).toMatchObject({ kind: 'item', detail: 'string' });
    expect(inner.find((s) => s.text === 'm')).toMatchObject({ kind: 'item', detail: 'EmailMessage' });
    // Outside the loops there is no item.
    expect(expressionSuggestions(c, 'list').some((s) => s.text === 'm' || s.text === 'to')).toBe(false);
    expect(expressionSuggestions(c, 'list').find((s) => s.text === 'count')).toMatchObject({ kind: 'function', detail: 'count(list) -> int', insert: 'count(' });
  });

  it('suggests for what is typed at the cursor and puts the pick in, in braces when outside them', () => {
    const all = expressionSuggestions(ctx(realDoc()), 'note');
    const a = suggestAt('New: {m.su', 10, all);
    expect(a.items.map((s) => s.text)).toEqual(['m.subject']);
    expect(a.wrap).toBe(false);
    expect(applySuggestion('New: {m.su', a, a.items[0]!)).toEqual({ value: 'New: {m.subject', cursor: 15 });
    const b = suggestAt('', 0, all);
    expect(b.wrap).toBe(true);
    expect(b.items.slice(0, 4).map((s) => s.text)).toEqual(['m', 'count', 'last', 'now']); // the shallow names first
    expect(b.items[4]!.text).toBe('settings.recipients');
    const vars = suggestAt('vars.r', 6, all);
    expect(vars.items.map((s) => s.text)).toEqual(['vars.res', 'vars.res.messages']);
    expect(applySuggestion('vars.r', vars, vars.items[1]!)).toEqual({ value: '{vars.res.messages}', cursor: 19 });
    const fn = suggestAt('{cou', 4, all);
    expect(applySuggestion('{cou', fn, fn.items[0]!)).toEqual({ value: '{count(', cursor: 7 });
    const wrapped = suggestAt('', 0, all.filter((s) => s.text === 'now'));
    expect(applySuggestion('', wrapped, wrapped.items[0]!)).toEqual({ value: '{now()}', cursor: 5 });
  });

  it('gives each step its typed in and out', () => {
    const c = ctx(realDoc());
    const io = (id: string) => stepIO(c, c.doc.steps!.find((s) => s.id === id)!);
    expect(ioInText(io('list'))).toBe('folder: string, after: string, since: datetime, max: int');
    expect(ioOutText(io('list'))).toBe('EmailList → res');
    expect(ioInText(io('text'))).toBe('To: string, From: string, Body: string');
    expect(ioOutText(io('text'))).toBe('object');
    expect(ioInText(io('each'))).toBe('items: list<EmailMessage>');
    expect(ioOutText(io('each'))).toBe('EmailMessage → m');
    expect(ioOutText(io('mark'))).toBe('string → lastId');
    expect(ioInText(io('any'))).toBe('left: int, right: int');
    expect(ioInText(io('note'))).toBe('path: string, text: string');
    // Through the member's binding, an alias finds its action.
    const alias: DesignContext = { doc: INBOX_SMS_ALERT, catalog: CATALOG, schemas: FIELDS, bindings: { mail: 'Example Support Inbox' } };
    expect(ioOutText(stepIO(alias, INBOX_SMS_ALERT.steps![0]!))).toBe('EmailList → res');
  });

  it('Move, Copy and List files: their in and out', () => {
    const doc: WorkflowDocument = { steps: [
      { id: 'mv', type: 'file.move', path: 'a', to: 'b', into: 'final', next: null },
      { id: 'cp', type: 'file.copy', path: 'a', to: 'b', next: null },
      { id: 'ls', type: 'file.list', path: 'in', pattern: '*.jpg', into: 'photos', next: null },
    ] };
    const io = (id: string) => stepIO({ doc }, doc.steps!.find((s) => s.id === id)!);
    expect(ioInText(io('mv'))).toBe('path: string, to: string, overwrite: bool');
    expect(ioOutText(io('mv'))).toBe('string → final');
    expect(ioInText(io('cp'))).toBe('path: string, to: string, overwrite: bool');
    expect(ioOutText(io('cp'))).toBe('string');
    expect(ioInText(io('ls'))).toBe('path: string, pattern: string, recursive: bool');
    expect(ioOutText(io('ls'))).toBe('list<object> → photos');
  });

  it('lays the end node out at the bottom, even when no step goes to it', () => {
    const doc: WorkflowDocument = { start: 'a', steps: [{ id: 'a', type: 'log', text: 'x', next: 'b' }, { id: 'b', type: 'log', text: 'y', next: 'c' }, { id: 'c', type: 'end' }] };
    const { nodes, edges } = docToGraph(doc);
    expect(edges.some((e) => e.target === END_ID)).toBe(false);
    const p = autoLayout(nodes, edges);
    for (const id of [START_ID, 'a', 'b', 'c']) expect(p[END_ID]!.y, id).toBeGreaterThan(p[id]!.y);
    expect(p[END_ID]!.x).toBe(p[START_ID]!.x);
    // With steps going to it, it is still below every node.
    const g = docToGraph(INBOX_SMS_ALERT);
    const q = autoLayout(g.nodes, g.edges);
    expect(Math.max(...g.nodes.filter((n) => n.id !== END_ID).map((n) => q[n.id]!.y))).toBeLessThan(q[END_ID]!.y);
  });
});

// ---- the designer

describe('the step forms', () => {
  it('integration: picks the integration and action from the catalog, one field per input, and saves the result into a variable', async () => {
    const w = await editor(fakeApi().api);
    await pick(w, 'list');
    const form = w.get('[data-test=step-form]');
    expect(form.attributes('data-type')).toBe('integration');
    const use = form.get('[data-test=form-use]');
    expect((use.element as HTMLSelectElement).value).toBe('Example Support Inbox');
    expect(use.findAll('option').map((o) => o.text())).toEqual(expect.arrayContaining(['Example Support Inbox (Email)', 'Other Inbox (Email)', 'Example Texting (API)']));
    const action = form.get('[data-test=form-action]');
    expect(action.findAll('option:not([disabled])').map((o) => o.attributes('value'))).toEqual(['list', 'get', 'markRead']);
    // One field per input, labelled with its type, the required ones marked.
    const fields = form.findAll('[data-test=form-args] [data-test=expr-field]');
    expect(fields.map((f) => [f.attributes('data-name'), f.get('[data-test=expr-type]').text(), f.find('[data-test=expr-required]').exists()])).toEqual([
      ['arg-folder', 'string', true], ['arg-after', 'string', false], ['arg-since', 'datetime', false], ['arg-max', 'int', false],
    ]);
    expect((expr(form, 'arg-max').element as HTMLInputElement).value).toBe('200');
    await typeIn(expr(form, 'arg-max'), '50');
    expect(stepOf(w, 'list').args).toMatchObject({ max: 50 });
    await typeIn(expr(form, 'arg-after'), '');
    expect(stepOf(w, 'list').args).not.toHaveProperty('after');
    // The form shows the same in and out as the node.
    expect(w.get('[data-test=form-io-out]').text()).toBe('EmailList → res');

    // Another action: its inputs replace the old ones, and requires follows.
    await changeTo(w.get('[data-test=form-action]'), 'get');
    expect(stepOf(w, 'list').action).toBe('get');
    expect(stepOf(w, 'list').args).toEqual({});
    expect(docOf(w).requires).toEqual([
      { name: 'Example Support Inbox', kind: 'email', actions: ['get'] },
      { name: 'Example Texting', kind: 'api', actions: ['CreateMessage'] },
    ]);
    await typeIn(expr(w, 'arg-id'), '{vars.lastId}');
    expect(stepOf(w, 'list').args).toEqual({ id: '{vars.lastId}' });

    // Save the result into a new typed variable.
    await changeTo(w.get('[data-test=form-into]'), '__new');
    expect((w.get('[data-test=form-into-new-type]').element as HTMLInputElement).value).toBe('EmailMessage');
    await w.get('[data-test=form-into-new-name]').setValue('one');
    await w.get('[data-test=form-into-new-add]').trigger('click');
    await flushPromises();
    expect(stepOf(w, 'list').into).toBe('one');
    expect(docOf(w).variables!.at(-1)).toEqual({ name: 'one', type: 'EmailMessage', scope: 'run' });
    // Or an existing one.
    await changeTo(w.get('[data-test=form-into]'), 'last');
    expect(stepOf(w, 'list').into).toBe('last');

    // Another integration: the action it does not offer is cleared, and requires follows.
    await changeTo(w.get('[data-test=form-use]'), 'Example Texting');
    expect(stepOf(w, 'list')).toMatchObject({ use: 'Example Texting', action: '', args: {} });
    expect(docOf(w).requires).toEqual([{ name: 'Example Texting', kind: 'api', actions: ['CreateMessage'] }]);
    expect(w.get('[data-test=form-action]').findAll('option').map((o) => o.text())).toContain('DeleteMessage (asks first)');
  });

  it('if: left value, operator, right value, then and else', async () => {
    const w = await editor(fakeApi().api);
    await pick(w, 'any');
    expect((expr(w, 'when-left').element as HTMLInputElement).value).toBe('{count(vars.res.messages)}');
    await typeIn(expr(w, 'when-right'), '3');
    expect(stepOf(w, 'any').when).toEqual({ left: '{count(vars.res.messages)}', op: 'gt', right: 3 });
    await changeTo(w.get('[data-test=form-when-op]'), 'exists');
    expect(stepOf(w, 'any').when).toEqual({ left: '{count(vars.res.messages)}', op: 'exists' });
    expect(w.find('[data-test=expr-field][data-name=when-right]').exists()).toBe(false);
    await changeTo(w.get('[data-test=form-when-op]'), 'ge');
    await typeIn(expr(w, 'when-left'), '{vars.sent}');
    expect(stepOf(w, 'any').when).toMatchObject({ left: '{vars.sent}', op: 'ge' });
    await changeTo(w.get('[data-test=form-then]'), 'log');
    await changeTo(w.get('[data-test=form-else]'), '');
    expect(stepOf(w, 'any')).toMatchObject({ then: 'log', else: null });
  });

  it('switch: the value and the cases', async () => {
    const doc: WorkflowDocument = { ...realDoc(), start: 'pick', steps: [
      { id: 'pick', type: 'switch', on: '{settings.folder}', cases: { INBOX: 'a' }, default: null },
      { id: 'a', type: 'log', text: 'a', next: null }, { id: 'done', type: 'end' },
    ] };
    const w = await editor(fakeApi({ doc }).api);
    await pick(w, 'pick');
    await typeIn(expr(w, 'on'), '{settings.since}');
    expect(stepOf(w, 'pick').on).toBe('{settings.since}');
    await w.get('[data-test=form-case-add]').trigger('click');
    await flushPromises();
    expect(stepOf(w, 'pick').cases).toEqual({ INBOX: 'a', value2: null });
    const second = w.findAll('[data-test=form-case]')[1]!;
    await changeTo(second.get('[data-test=form-case-value]'), 'Spam');
    await changeTo(w.findAll('[data-test=form-case]')[1]!.get('[data-test=form-case-target]'), 'done');
    expect(stepOf(w, 'pick').cases).toEqual({ INBOX: 'a', Spam: 'done' });
    await w.findAll('[data-test=form-case-remove]')[0]!.trigger('click');
    await flushPromises();
    expect(stepOf(w, 'pick').cases).toEqual({ Spam: 'done' });
    await changeTo(w.get('[data-test=form-default]'), 'a');
    expect(stepOf(w, 'pick').default).toBe('a');
  });

  it('forEach: the items and the item name; set: the variable and its value', async () => {
    const w = await editor(fakeApi().api);
    await pick(w, 'each');
    await typeIn(expr(w, 'items'), '{vars.res.messages}x');
    expect(stepOf(w, 'each').items).toBe('{vars.res.messages}x');
    await typeIn(w.get('[data-test=form-as]'), 'msg');
    expect(stepOf(w, 'each').as).toBe('msg');
    await pick(w, 'count');
    expect((w.get('[data-test=form-var]').element as HTMLSelectElement).value).toBe('sent');
    await changeTo(w.get('[data-test=form-var]'), 'lastId');
    await typeIn(expr(w, 'value'), '{vars.last.id}');
    expect(stepOf(w, 'count')).toMatchObject({ var: 'lastId', value: '{vars.last.id}' });
  });

  it('file: path and text; end: the outputs', async () => {
    const w = await editor(fakeApi().api);
    await pick(w, 'note');
    await typeIn(expr(w, 'path'), 'mail.log');
    await typeIn(expr(w, 'text'), '{m.subject}');
    expect(stepOf(w, 'note')).toMatchObject({ path: 'mail.log', text: '{m.subject}' });
    await changeTo(w.get('[data-test=form-next]'), 'done');
    expect(stepOf(w, 'note').next).toBe('done');
    await pick(w, 'done');
    expect(w.get('[data-test=step-form]').attributes('data-type')).toBe('end');
    await w.get('[data-test=form-output-add]').trigger('click');
    await flushPromises();
    expect(stepOf(w, 'done').outputs).toEqual({ output1: '' });
    await changeTo(w.get('[data-test=form-output-name]'), 'sent');
    await typeIn(expr(w, 'output-sent'), '{vars.sent}');
    expect(stepOf(w, 'done').outputs).toEqual({ sent: '{vars.sent}' });
    await w.get('[data-test=form-output-remove]').trigger('click');
    await flushPromises();
    expect(stepOf(w, 'done')).not.toHaveProperty('outputs');
  });

  it('an expression field suggests as it is typed, and a pick goes into the document', async () => {
    const w = await editor(fakeApi().api);
    await pick(w, 'note');
    const input = expr(w, 'text');
    const el = input.element as HTMLInputElement;
    el.value = 'From {m.fr';
    el.setSelectionRange(10, 10);
    await input.trigger('input');
    await flushPromises();
    const items = w.findAll('[data-test=expr-suggestion]').map((s) => s.attributes('data-text'));
    expect(items).toEqual(['m.from', 'm.from.address', 'm.from.name']);
    await w.get('[data-test=expr-suggestion][data-text="m.from.address"]').trigger('mousedown');
    await flushPromises();
    expect(stepOf(w, 'note').text).toBe('From {m.from.address');
    // Keys: down, then Enter picks.
    el.value = '{vars.la';
    el.setSelectionRange(8, 8);
    await input.trigger('input');
    await flushPromises();
    expect(w.findAll('[data-test=expr-suggestion]').map((s) => s.attributes('data-text')).slice(0, 2)).toEqual(['vars.lastId', 'vars.last']);
    await input.trigger('keydown', { key: 'ArrowDown' });
    await input.trigger('keydown', { key: 'Enter' });
    await flushPromises();
    expect(stepOf(w, 'note').text).toBe('{vars.last');
    expect(w.find('[data-test=expr-suggestions]').exists()).toBe(false);
  });

  it('log: reads the step\'s id, text and next, writes each edit back, and both JSON tabs show it', async () => {
    const doc: WorkflowDocument = { ...realDoc(), start: 'first', steps: [
      { id: 'first', type: 'log', text: 'Hello {settings.folder}', next: 'say' },
      { id: 'say', type: 'log', text: '{count(vars.res.messages)} new', next: 'done' },
      { id: 'done', type: 'end' },
    ] };
    const w = await editor(fakeApi({ doc }).api);
    await pick(w, 'say');
    const form = w.get('[data-test=step-form]');
    expect(form.attributes('data-type')).toBe('log');
    // It reads every field the log step has.
    expect((form.get('[data-test=form-id]').element as HTMLInputElement).value).toBe('say');
    expect((expr(form, 'text').element as HTMLInputElement).value).toBe('{count(vars.res.messages)} new');
    expect(form.get('[data-test=expr-field][data-name=text] [data-test=expr-type]').text()).toBe('string');
    expect((form.get('[data-test=form-next]').element as HTMLSelectElement).value).toBe('done');
    expect(form.get('[data-test=form-next]').findAll('option').map((o) => o.attributes('value'))).toEqual(['', 'first', 'done']);
    expect(form.find('[data-test=form-into]').exists()).toBe(false);

    // And writes each one back: text, next (another step, then the end), id.
    await typeIn(expr(w, 'text'), 'Checked {settings.folder}: {vars.sent} sent');
    expect(stepOf(w, 'say').text).toBe('Checked {settings.folder}: {vars.sent} sent');
    await changeTo(w.get('[data-test=form-next]'), 'first');
    expect(stepOf(w, 'say').next).toBe('first');
    await changeTo(w.get('[data-test=form-next]'), '');
    expect(stepOf(w, 'say').next).toBeNull();
    await changeTo(w.get('[data-test=form-id]'), 'report');
    expect(docOf(w).steps!.map((s) => s.id)).toEqual(['first', 'report', 'done']);
    const edited = { id: 'report', type: 'log', text: 'Checked {settings.folder}: {vars.sent} sent', next: null };
    expect(stepOf(w, 'report')).toEqual(edited);
    // The other steps are as they were.
    expect(stepOf(w, 'first')).toEqual(doc.steps![0]);

    // The step panel's JSON tab shows the edited step, and the editor's JSON tab the whole document with it.
    expect(w.get('[data-test=flow-panel]').attributes('data-node')).toBe('report');
    await w.get('[data-test=flow-panel-tab-json]').trigger('click');
    expect(JSON.parse((w.get('[data-test=flow-panel-json]').element as HTMLTextAreaElement).value)).toEqual(edited);
    await w.get('[data-test=editor-tab-json]').trigger('click');
    await flushPromises();
    expect(docOf(w).steps![1]).toEqual(edited);
    expect(jsonText(w)).toContain('"text": "Checked {settings.folder}: {vars.sent} sent"');
  });

  it('a JSON edit in the step panel and the form are one step; invalid JSON is refused and the form stays', async () => {
    const w = await editor(fakeApi().api);
    await pick(w, 'note');
    await w.get('[data-test=flow-panel-tab-json]').trigger('click');
    const area = w.get('[data-test=flow-panel-json]');
    await area.setValue(pretty({ ...stepOf(w, 'note'), path: 'other.log' }));
    await flushPromises();
    await area.setValue('{ "id": ');
    await flushPromises();
    expect(w.get('[data-test=flow-panel-error]').text()).toContain('not used');
    await w.get('[data-test=flow-panel-tab-design]').trigger('click');
    expect((expr(w, 'path').element as HTMLInputElement).value).toBe('other.log');
    expect(stepOf(w, 'note').path).toBe('other.log');
  });
});

describe('the Workflow pane', () => {
  it('shows when nothing is selected, and after a click on the empty canvas', async () => {
    const w = await editor(fakeApi().api);
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(true);
    expect(w.get('[data-test=workflow-pane-tab-design]').attributes('aria-selected')).toBe('true');
    await pick(w, 'list');
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false);
    await w.get('[data-test=vf-pane]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(true);
    expect(w.find('[data-test=flow-panel]').exists()).toBe(false);
  });

  it('edits the name, id, description and the triggers it accepts', async () => {
    const w = await editor(fakeApi().api);
    await typeIn(w.get('[data-test=workflow-name]'), 'Mail to texts');
    await typeIn(w.get('[data-test=workflow-id]'), 'mail-to-texts');
    await typeIn(w.get('[data-test=workflow-description]'), 'Texts new mail.');
    expect(docOf(w)).toMatchObject({ name: 'Mail to texts', id: 'mail-to-texts', description: 'Texts new mail.' });
    expect((w.get('[data-test=workflow-accept-every]').element as HTMLInputElement).checked).toBe(true);
    await w.get('[data-test=workflow-accept-every]').setValue(false);
    await w.get('[data-test=workflow-accept-tell]').setValue(true);
    await flushPromises();
    expect(docOf(w).trigger).toEqual({ accepts: ['cron', 'once', 'tell'] });
  });

  it('adds, removes and reorders settings, and edits their name, type, label and default', async () => {
    const w = await editor(fakeApi().api);
    const names = () => docOf(w).settings!.map((s) => s.name);
    expect(names()).toEqual(['recipients', 'fromNumber', 'folder', 'since']);
    await w.findAll('[data-test=setting-down]')[0]!.trigger('click');
    await flushPromises();
    expect(names()).toEqual(['fromNumber', 'recipients', 'folder', 'since']);
    await w.findAll('[data-test=setting-up]')[3]!.trigger('click');
    await flushPromises();
    expect(names()).toEqual(['fromNumber', 'recipients', 'since', 'folder']);
    await w.get('[data-test=setting-add]').trigger('click');
    await flushPromises();
    expect(docOf(w).settings!.at(-1)).toEqual({ name: 'setting5', type: 'string' });
    const row = () => w.findAll('[data-test=workflow-setting]').at(-1)!;
    await changeTo(row().get('[data-test=setting-name]'), 'limit');
    await changeTo(row().get('[data-test=setting-type]'), 'int');
    await changeTo(row().get('[data-test=setting-label]'), 'At most');
    await changeTo(row().get('[data-test=setting-default]'), '25');
    expect(docOf(w).settings!.at(-1)).toEqual({ name: 'limit', type: 'int', label: 'At most', default: 25 });
    // A list's default is one item per line.
    const recipients = w.findAll('[data-test=workflow-setting]')[1]!;
    await changeTo(recipients.get('[data-test=setting-default]'), '+15550000001\n+15550000002');
    expect(docOf(w).settings![1]).toMatchObject({ name: 'recipients', default: ['+15550000001', '+15550000002'] });
    await w.findAll('[data-test=setting-remove]')[0]!.trigger('click');
    await flushPromises();
    expect(names()).toEqual(['recipients', 'since', 'folder', 'limit']);
  });

  it('adds and edits variables (name, type, scope, initial value) and outputs', async () => {
    const w = await editor(fakeApi().api);
    await w.get('[data-test=variable-add]').trigger('click');
    await flushPromises();
    const row = () => w.findAll('[data-test=workflow-variable]').at(-1)!;
    expect(row().find('[data-test=variable-type]').attributes('list')).toBe('workflow-pane-types');
    expect([...document.querySelectorAll('#workflow-pane-types option')].map((o) => (o as HTMLOptionElement).value)).toEqual(expect.arrayContaining(['string', 'int', 'EmailMessage', 'EmailList']));
    await changeTo(row().get('[data-test=variable-name]'), 'seen');
    await changeTo(row().get('[data-test=variable-type]'), 'list<string>');
    await changeTo(row().get('[data-test=variable-scope]'), 'saved');
    await changeTo(row().get('[data-test=variable-initial]'), '[]');
    expect(docOf(w).variables!.at(-1)).toEqual({ name: 'seen', type: 'list<string>', scope: 'saved', initial: [] });
    await changeTo(row().get('[data-test=variable-initial]'), '');
    expect(docOf(w).variables!.at(-1)).toEqual({ name: 'seen', type: 'list<string>', scope: 'saved' });
    await w.findAll('[data-test=variable-remove]')[0]!.trigger('click');
    await flushPromises();
    expect(docOf(w).variables!.map((v) => v.name)).toEqual(['res', 'last', 'sent', 'seen']);

    await w.get('[data-test=output-add]').trigger('click');
    await flushPromises();
    await changeTo(w.get('[data-test=output-name]'), 'texts');
    await changeTo(w.get('[data-test=output-type]'), 'int');
    await changeTo(w.get('[data-test=output-description]'), 'How many were sent');
    expect(docOf(w).outputs).toEqual([{ name: 'texts', type: 'int', description: 'How many were sent' }]);
  });

  it('lists the integrations used, derived from the steps, each linking to its steps', async () => {
    const w = await editor(fakeApi().api);
    const rows = w.findAll('[data-test=workflow-require]');
    expect(rows.map((r) => r.attributes('data-name'))).toEqual(['Example Support Inbox', 'Example Texting']);
    expect(rows[1]!.findAll('[data-test=workflow-require-step]').map((b) => b.attributes('data-step'))).toEqual(['text']);
    await rows[1]!.get('[data-test=workflow-require-step]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=flow-panel]').attributes('data-node')).toBe('text');
    expect(w.get('[data-test=flow-node][data-node=text]').classes()).toContain('picked');
  });

  it('the JSON tab, the forms and the editor\'s JSON are one document; invalid JSON is refused without losing the form', async () => {
    const w = await editor(fakeApi().api);
    await w.get('[data-test=workflow-pane-tab-json]').trigger('click');
    const area = w.get('[data-test=workflow-pane-json]');
    expect(JSON.parse((area.element as HTMLTextAreaElement).value)).toEqual(docOf(w));
    await area.setValue(pretty({ ...docOf(w), name: 'From the JSON tab' }));
    await flushPromises();
    expect(docOf(w).name).toBe('From the JSON tab');
    await area.setValue('{ "name": ');
    await flushPromises();
    expect(w.get('[data-test=workflow-pane-json-error]').text()).toContain('not used');
    expect(docOf(w).name).toBe('From the JSON tab');
    await w.get('[data-test=workflow-pane-tab-design]').trigger('click');
    expect((w.get('[data-test=workflow-name]').element as HTMLInputElement).value).toBe('From the JSON tab');
    // The editor's own JSON tab edits the same document.
    await w.get('[data-test=editor-tab-json]').trigger('click');
    await w.get('[data-test=json-text]').setValue(pretty({ ...docOf(w), name: 'From the editor' }));
    await w.get('[data-test=editor-tab-flowchart]').trigger('click');
    await flushPromises();
    expect((w.get('[data-test=workflow-name]').element as HTMLInputElement).value).toBe('From the editor');
  });
});

describe('typed in and out, real names, overrides and the canvas', () => {
  it('each node shows its in (name: type) and out (type → variable)', async () => {
    const w = await editor(fakeApi().api);
    const node = (id: string) => w.get(`[data-test=flow-node][data-node="${id}"]`);
    expect(node('list').get('[data-test=flow-node-in]').text()).toBe('in folder: string, after: string, since: datetime, max: int');
    expect(node('list').get('[data-test=flow-node-out]').text()).toBe('out EmailList → res');
    expect(node('each').get('[data-test=flow-node-out]').text()).toBe('out EmailMessage → m');
    expect(node('keep').get('[data-test=flow-node-out]').text()).toBe('out EmailMessage → last');
    expect(node('__start').find('[data-test=flow-node-in]').exists()).toBe(false);
    // No dataflow wires: the edges are the control flow only.
    const edges = (w.getComponent({ name: 'VueFlowStub' }).props('edges') as any[]).map((e) => e.data.kind);
    expect(new Set(edges)).toEqual(new Set(['next', 'then', 'else', 'body']));
  });

  it('Use real names shows for an alias document, calls workflows.useRealNames and reports what it could not resolve', async () => {
    const renamedDoc = realDoc();
    const f = fakeApi({ doc: INBOX_SMS_ALERT, realNames: { document: renamedDoc, renamed: { mail: 'Example Support Inbox' }, unresolved: ['sms'] } });
    const w = await editor(f.api);
    await w.get('[data-test=editor-real-names]').trigger('click');
    await flushPromises();
    expect(f.of('workflows.useRealNames')).toHaveLength(1);
    expect(JSON.parse(f.of('workflows.useRealNames')[0]!.params.document)).toEqual(INBOX_SMS_ALERT);
    expect(f.of('workflows.useRealNames')[0]!.params).not.toHaveProperty('bindings');
    expect(docOf(w)).toEqual(renamedDoc);
    expect(w.get('[data-test=editor-renamed]').text()).toContain('mail → Example Support Inbox');
    expect(w.get('[data-test=editor-unresolved]').text()).toContain('sms');
    // Now in the real-name form: no button.
    expect(w.find('[data-test=editor-real-names]').exists()).toBe(false);
  });

  it('opened from a member: Use real names sends its bindings, and overridden integrations show "A → B"', async () => {
    const member = { memberId: 9, teamId: 1, bindings: { mail: 'Example Support Inbox', sms: 'Example Texting' },
      integrations: [{ name: 'mail', kind: 'email', uses: 'Example Support Inbox', overridden: false }, { name: 'sms', kind: 'api', uses: 'Example Texting', overridden: false }] };
    const f = fakeApi({ doc: INBOX_SMS_ALERT, member, realNames: { document: realDoc(), renamed: { mail: 'Example Support Inbox', sms: 'Example Texting' }, unresolved: [] } });
    const w = await editor(f.api, { memberId: 9 });
    expect(f.of('workflows.memberGet')[0]!.params).toEqual({ memberId: 9 });
    expect(f.of('workflows.catalog')[0]!.params).toEqual({ teamId: 1 });
    await w.get('[data-test=editor-real-names]').trigger('click');
    await flushPromises();
    expect(f.of('workflows.useRealNames')[0]!.params.bindings).toEqual(member.bindings);
    expect(w.find('[data-test=editor-unresolved]').exists()).toBe(false);

    const overridden = { memberId: 9, teamId: 1, bindings: { 'Example Support Inbox': 'Other Inbox' },
      integrations: [{ name: 'Example Support Inbox', kind: 'email', uses: 'Other Inbox', overridden: true }, { name: 'Example Texting', kind: 'api', uses: 'Example Texting', overridden: false }] };
    const w2 = await editor(fakeApi({ member: overridden }).api, { memberId: 9 });
    const rows = w2.findAll('[data-test=workflow-require-override]');
    expect(rows.map((r) => r.text())).toEqual(['Example Support Inbox → Other Inbox']);
    await pick(w2, 'list');
    expect(w2.get('[data-test=form-override]').text()).toBe('Example Support Inbox → Other Inbox');
    await pick(w2, 'text');
    expect(w2.find('[data-test=form-override]').exists()).toBe(false);
  });

  it('the minimap is folded away on a small pane and its toggle shows it; a wide pane starts with it shown', async () => {
    const w = await editor(fakeApi().api);
    expect(w.find('[data-test=vf-minimap]').exists()).toBe(false);
    const toggle = w.get('[data-test=flow-minimap-toggle]');
    expect(toggle.attributes('aria-pressed')).toBe('false');
    await toggle.trigger('click');
    expect(w.find('[data-test=vf-minimap]').exists()).toBe(true);
    expect(toggle.attributes('aria-pressed')).toBe('true');
    await toggle.trigger('click');
    expect(w.find('[data-test=vf-minimap]').exists()).toBe(false);

    Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => 1200 });
    try {
      const wide = await editor(fakeApi().api);
      expect(wide.find('[data-test=vf-minimap]').exists()).toBe(true);
    } finally {
      delete (HTMLElement.prototype as { clientWidth?: number }).clientWidth;
    }
  });

  it('a Visitor sees every form read-only', async () => {
    applyRemoteAccess('read');
    const w = await editor(fakeApi().api);
    const before = jsonText(w);
    expect(w.find('[data-test=editor-real-names]').exists()).toBe(false);
    for (const t of ['setting-add', 'variable-add', 'output-add', 'setting-remove', 'setting-up']) expect(w.find(`[data-test=${t}]`).exists(), t).toBe(false);
    expect(w.get('[data-test=workflow-name]').attributes('readonly')).toBeDefined();
    expect(w.get('[data-test=workflow-accept-every]').attributes('disabled')).toBeDefined();
    await typeIn(w.get('[data-test=workflow-name]'), 'changed');
    await w.get('[data-test=workflow-pane-tab-json]').trigger('click');
    expect(w.get('[data-test=workflow-pane-json]').attributes('readonly')).toBeDefined();
    await w.get('[data-test=workflow-pane-json]').setValue('{}');
    for (const id of ['list', 'any', 'each', 'count', 'note', 'done']) {
      await pick(w, id);
      const form = w.get('[data-test=step-form]');
      for (const i of form.findAll('[data-test=expr-input], input:not([type=checkbox])')) expect(i.attributes('readonly'), `${id} input`).toBeDefined();
      for (const s of form.findAll('select')) expect(s.attributes('disabled'), `${id} select`).toBeDefined();
      expect(form.findAll('button').length, `${id} buttons`).toBe(0);
    }
    await pick(w, 'list');
    await typeIn(expr(w, 'arg-folder'), 'Spam');
    await flushPromises();
    expect(w.find('[data-test=expr-suggestions]').exists()).toBe(false);
    expect(jsonText(w)).toBe(before);
  });
});

/**
 * The document the next test builds through the designer, committed for the core: DesignerBuiltWorkflowTests loads it and runs
 * the real checker over it. When the designer's output changes on purpose, run this file once with YAWBLE_WRITE_FIXTURE=1.
 */
const DESIGNER_BUILT = resolve(__dirname, '../../../core/tests/Yawble.Core.Tests/Workflows/Fixtures/designer-built.json');

describe('a workflow built only through the designer', () => {
  it('Blank, then the Workflow pane, the palette, the step forms and a canvas connection: the JSON is the committed fixture', async () => {
    const w = await editor(fakeApi().api, { workflow: undefined, initial: BLANK_WORKFLOW });
    const set = async (sel: string, value: string, i = 0) => { await changeTo(w.findAll(`[data-test=${sel}]`)[i]!, value); };
    const add = async (type: string) => { await w.get(`[data-test=palette-${type}]`).trigger('click'); await flushPromises(); };

    // The Workflow pane: name, id, description, a setting and a variable.
    await typeIn(w.get('[data-test=workflow-name]'), 'Designer built');
    await typeIn(w.get('[data-test=workflow-id]'), 'designer-built');
    await typeIn(w.get('[data-test=workflow-description]'), 'Counts new mail and logs it.');
    await w.get('[data-test=setting-add]').trigger('click');
    await flushPromises();
    await set('setting-name', 'folder');
    await set('setting-label', 'Mail folder');
    await set('setting-default', 'INBOX');
    await w.get('[data-test=variable-add]').trigger('click');
    await flushPromises();
    await set('variable-name', 'found');
    await set('variable-type', 'int');
    await set('variable-initial', '0');

    // An integration step that saves its result into a new variable.
    await add('integration');
    await set('form-id', 'list');
    await set('form-use', 'Example Support Inbox');
    await set('form-action', 'list');
    await typeIn(expr(w, 'arg-folder'), '{settings.folder}');
    await typeIn(expr(w, 'arg-max'), '10');
    await set('form-into', '__new');
    expect((w.get('[data-test=form-into-new-type]').element as HTMLInputElement).value).toBe('EmailList');
    await w.get('[data-test=form-into-new-name]').setValue('res');
    await w.get('[data-test=form-into-new-add]').trigger('click');
    await flushPromises();

    // An if, a set and a log.
    await add('if');
    await set('form-id', 'any');
    await typeIn(expr(w, 'when-left'), '{count(vars.res.messages)}');
    await set('form-when-op', 'gt');
    await typeIn(expr(w, 'when-right'), '0');
    await add('set');
    await set('form-id', 'keep');
    await set('form-var', 'found');
    await typeIn(expr(w, 'value'), '{count(vars.res.messages)}');
    await add('log');
    await set('form-id', 'say');
    await typeIn(expr(w, 'text'), '{vars.found} new in {settings.folder}');
    await set('form-next', 'done');

    // The connections: through the forms, and the start on the canvas.
    await pick(w, 'list');
    await set('form-next', 'any');
    await pick(w, 'any');
    await set('form-then', 'keep');
    await set('form-else', 'done');
    await pick(w, 'keep');
    await set('form-next', 'say');
    w.findComponent({ name: 'VueFlowStub' }).vm.$emit('connect', { source: START_ID, target: 'list', sourceHandle: null, targetHandle: null });
    await flushPromises();

    const built = docOf(w);
    expect(built.start).toBe('list');
    expect(built.steps!.map((s) => [s.id, s.type])).toEqual([['done', 'end'], ['list', 'integration'], ['any', 'if'], ['keep', 'set'], ['say', 'log']]);
    expect(built.requires).toEqual([{ name: 'Example Support Inbox', kind: 'email', actions: ['list'] }]);
    expect(built.variables).toEqual([{ name: 'found', type: 'int', scope: 'run', initial: 0 }, { name: 'res', type: 'EmailList', scope: 'run' }]);
    expect(built.steps!.find((s) => s.id === 'list')).toMatchObject({ args: { folder: '{settings.folder}', max: 10 }, into: 'res', next: 'any' });
    // 0 typed into the blank If's right value is the number (the text "0" the checker refuses against an int).
    expect(built.steps!.find((s) => s.id === 'any')!.when).toEqual({ left: '{count(vars.res.messages)}', op: 'gt', right: 0 });

    const expected = `${pretty(built)}\n`;
    if (process.env.YAWBLE_WRITE_FIXTURE === '1') writeFileSync(DESIGNER_BUILT, expected, 'utf8');
    expect(existsSync(DESIGNER_BUILT), DESIGNER_BUILT).toBe(true);
    const fixture = readFileSync(DESIGNER_BUILT, 'utf8').replace(/\r\n/g, '\n');
    expect(JSON.parse(fixture)).toEqual(built);
    expect(fixture).toBe(expected);
  });
});

describe('the designer opened from a workflow member', () => {
  const card = { member: { id: 9, teamId: 1, name: 'Poller', role: 'member' } } as unknown as AgentCard;
  const realBindings = { 'Example Support Inbox': 'Other Inbox' };
  const workflowMember = (over: Record<string, unknown> = {}) => ({
    memberId: 9, teamId: 1, team: 'Support', name: 'Poller', workflowId: 1, workflow: 'inbox-sms-alert', version: 2, runsVersion: 2,
    settings: {}, bindings: realBindings, timeoutMinutes: 10, consecutiveFailures: 0, running: false, lastRun: null, nextTriggerAt: null,
    integrations: [{ name: 'Example Support Inbox', kind: 'email', uses: 'Other Inbox', overridden: true },
      { name: 'Example Texting', kind: 'api', uses: 'Example Texting', overridden: false }],
    ...over,
  });
  const paneHandlers = {
    'workflows.runs': () => [],
    'workflows.memberState': () => ({ memberId: 9, variables: {}, consecutiveFailures: 0, updatedAt: null }),
    'workflows.memberFiles': () => ({ memberId: 9, files: [] }),
    'triggers.list': () => [],
    'integrations.emailList': () => [],
    'integrations.apiList': () => [],
    'workflows.save': () => ({ id: 1, key: 'inbox-sms-alert', version: 4, ok: true, draft: false, check: clean }),
  };

  /** The member pane, and the editor its Edit workflow opens (teleported to the body, so found as a component). */
  async function openFromPane(f: ReturnType<typeof fakeApi>) {
    const pane = mount(WorkflowMemberPane, { props: { card, api: f.api, pollMs: 60_000 }, attachTo: document.body });
    await flushPromises();
    expect(pane.findComponent(WorkflowEditor).exists()).toBe(false);
    await pane.get('[data-test=workflow-member-edit]').trigger('click');
    await flushPromises();
    const ed = pane.findComponent(WorkflowEditor);
    expect(ed.props('memberId')).toBe(9);
    await ed.get('[data-test=editor-tab-flowchart]').trigger('click');
    await flushPromises();
    return { pane, ed };
  }

  it('Edit workflow on the member pane opens the member\'s workflow with its overrides as "Real name → Other"', async () => {
    const f = fakeApi({ member: workflowMember(), more: paneHandlers });
    const { pane, ed } = await openFromPane(f);
    expect(pane.get('[data-test=workflow-member-edit]').text()).toBe('Edit workflow');
    expect(document.body.querySelector('[data-test=workflow-member-editor] [data-test=workflow-editor]')).not.toBeNull();
    expect(f.of('workflows.get').at(-1)!.params.workflow).toBe('inbox-sms-alert');
    expect(f.of('workflows.memberGet').map((c) => c.params)).toContainEqual({ memberId: 9 });
    expect(f.of('workflows.catalog')[0]!.params).toEqual({ teamId: 1 });
    expect(ed.findAll('[data-test=workflow-require-override]').map((r) => r.text())).toEqual(['Example Support Inbox → Other Inbox']);
    await pick(ed, 'list');
    expect(ed.get('[data-test=form-override]').text()).toBe('Example Support Inbox → Other Inbox');
    await pick(ed, 'text');
    expect(ed.find('[data-test=form-override]').exists()).toBe(false);
    // Back: the tile again.
    expect(ed.get('[data-test=editor-back]').text()).toBe('← Poller');
    await ed.get('[data-test=editor-back]').trigger('click');
    await flushPromises();
    expect(pane.findComponent(WorkflowEditor).exists()).toBe(false);
    expect(document.body.querySelector('[data-test=workflow-member-editor]')).toBeNull();
  });

  it('Use real names from the member sends the member\'s bindings', async () => {
    const bindings = { mail: 'Example Support Inbox', sms: 'Example Texting' };
    const member = workflowMember({ bindings, integrations: [
      { name: 'mail', kind: 'email', uses: 'Example Support Inbox', overridden: false }, { name: 'sms', kind: 'api', uses: 'Example Texting', overridden: false }] });
    const f = fakeApi({ doc: INBOX_SMS_ALERT, member, more: paneHandlers,
      realNames: { document: realDoc(), renamed: { mail: 'Example Support Inbox', sms: 'Example Texting' }, unresolved: [] } });
    const { ed } = await openFromPane(f);
    await ed.get('[data-test=editor-real-names]').trigger('click');
    await flushPromises();
    expect(f.of('workflows.useRealNames')).toHaveLength(1);
    expect(f.of('workflows.useRealNames')[0]!.params.bindings).toEqual(bindings);
    expect(docOf(ed)).toEqual(realDoc());
  });

  it('Save from there saves a new version of the workflow and leaves the member\'s pin and bindings alone', async () => {
    const f = fakeApi({ member: workflowMember(), more: paneHandlers });
    const { ed } = await openFromPane(f);
    await ed.get('[data-test=editor-save]').trigger('click');
    await flushPromises();
    expect(f.of('workflows.save')).toHaveLength(1);
    expect(JSON.parse(f.of('workflows.save')[0]!.params.document)).toEqual(realDoc());
    expect(ed.get('[data-test=editor-saved]').text()).toContain('version 4');
    // Only reads of the member: no memberUpdate (pin or bindings) or any other member write.
    const memberCalls = new Set(f.calls.map((c) => c.method).filter((m) => m.startsWith('workflows.member')));
    expect([...memberCalls].sort()).toEqual(['workflows.memberFiles', 'workflows.memberGet', 'workflows.memberState']);
  });

  it('a Visitor gets Open workflow and the editor read-only, still with the overrides', async () => {
    applyRemoteAccess('read');
    const f = fakeApi({ member: workflowMember(), more: paneHandlers });
    const { pane, ed } = await openFromPane(f);
    expect(pane.get('[data-test=workflow-member-edit]').text()).toBe('Open workflow');
    expect(ed.find('[data-test=editor-save]').exists()).toBe(false);
    expect(ed.findAll('[data-test=workflow-require-override]')).toHaveLength(1);
  });
  it('opened from the library: no member, so no arrows', async () => {
    const f = fakeApi({ member: workflowMember(), more: { 'workflows.list': () => [{ id: 1, key: 'inbox-sms-alert', name: 'Inbox SMS alert', description: '',
      latestVersion: 3, runnableVersion: 3, draft: false, updatedAt: at, usedBy: [{ teamId: 1, team: 'Support', memberId: 9, member: 'Poller', version: 2 }] }] } });
    const w = mount(WorkflowsLibrary, { props: { api: f.api, teams }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=workflow-open]').trigger('click');
    await flushPromises();
    await w.get('[data-test=editor-tab-flowchart]').trigger('click');
    await flushPromises();
    expect(f.of('workflows.memberGet')).toHaveLength(0);
    expect(w.findAll('[data-test=workflow-require]').length).toBeGreaterThan(0);
    expect(w.find('[data-test=workflow-require-override]').exists()).toBe(false);
    await pick(w, 'list');
    expect(w.find('[data-test=form-override]').exists()).toBe(false);
    expect(w.text()).not.toContain('→ Other Inbox');
  });
});
