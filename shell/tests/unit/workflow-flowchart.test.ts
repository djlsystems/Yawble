// @vitest-environment happy-dom
// The Flowchart tab of the workflow editor and the run view's flowchart: nodes and edges from the document, the side
// panel, the palette, connect (asking for the branch from an if), delete, undo and redo, the saved layout, the
// problems' badges, a Visitor's read-only view, and a run's steps highlighted. Vue Flow's canvas needs a real layout
// engine, so it is replaced here by a stub that renders each node through the component's own node slot and lets the
// tests fire the canvas's events. Fakes only: no core; made-up values only.
import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';
import WorkflowEditor from '../../src/renderer/src/components/workflows/WorkflowEditor.vue';
import WorkflowRunView from '../../src/renderer/src/components/workflows/WorkflowRunView.vue';
import { confirmState } from '../../src/renderer/src/confirm';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { INBOX_SMS_ALERT, pretty } from '../../src/renderer/src/workflows/api';
import type { Check, RunDetail, WorkflowDocument } from '../../src/renderer/src/workflows/types';

vi.mock('@vue-flow/core', async () => {
  const { defineComponent, h } = await import('vue');
  const VueFlow = defineComponent({
    name: 'VueFlowStub',
    props: { nodes: { type: Array, default: () => [] }, edges: { type: Array, default: () => [] }, nodesDraggable: Boolean, nodesConnectable: Boolean },
    emits: ['node-click', 'edge-click', 'pane-click', 'node-drag-stop', 'connect'],
    setup(props, { slots, emit }) {
      return () => h('div', { 'data-test': 'vf', 'data-draggable': String(props.nodesDraggable), 'data-connectable': String(props.nodesConnectable) }, [
        ...(props.nodes as any[]).map((n) => h('div', { class: 'vf-node', 'data-id': n.id, 'data-draggable': String(n.draggable), onClick: () => emit('node-click', { node: n, event: {} }) },
          slots['node-step']?.({ id: n.id, data: n.data }))),
        ...(props.edges as any[]).map((e) => h('div', {
          'data-test': 'vf-edge', 'data-id': e.id, 'data-source': e.source, 'data-target': e.target, 'data-label': e.label ?? '', class: e.class,
          onClick: () => emit('edge-click', { edge: e, event: {} }),
        })),
        slots.default?.(),
      ]);
    },
  });
  const Handle = defineComponent({ name: 'HandleStub', props: { type: String, position: String }, setup: () => () => null });
  return { VueFlow, Handle, Position: { Top: 'top', Bottom: 'bottom', Left: 'left', Right: 'right' }, MarkerType: { Arrow: 'arrow', ArrowClosed: 'arrowclosed' } };
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

interface Call { method: string; params: any }
function fakeApi(opts: { doc?: WorkflowDocument; check?: Check; recheck?: (doc: WorkflowDocument) => Check } = {}) {
  const calls: Call[] = [];
  const handlers: Record<string, (p: any) => unknown> = {
    'workflows.get': (p) => ({ id: 1, key: 'inbox-sms-alert', name: 'Inbox SMS alert', description: '', version: p.version ?? 3, ok: true,
      document: opts.doc ?? INBOX_SMS_ALERT, check: opts.check ?? clean, createdAt: at }),
    'workflows.versions': () => [{ version: 3, ok: true, createdAt: at }],
    'workflows.save': () => ({ id: 1, key: 'inbox-sms-alert', version: 4, ok: true, draft: false, check: clean }),
    'workflows.check': (p) => (opts.recheck ? opts.recheck(JSON.parse(p.document)) : clean),
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

async function editor(api: YawbleApi) {
  const w = mount(WorkflowEditor, { props: { api, teams, workflow: { id: 1, key: 'inbox-sms-alert' } }, attachTo: document.body });
  await flushPromises();
  await w.get('[data-test=editor-tab-flowchart]').trigger('click');
  await flushPromises();
  return w;
}
const jsonText = (w: VueWrapper) => (w.get('[data-test=json-text]').element as HTMLTextAreaElement).value;
const docOf = (w: VueWrapper) => JSON.parse(jsonText(w)) as WorkflowDocument;
const stepOf = (w: VueWrapper, id: string) => docOf(w).steps!.find((s) => s.id === id);
const node = (w: VueWrapper, id: string) => w.get(`[data-test=flow-node][data-node="${id}"]`);
const pick = async (w: VueWrapper, id: string) => { await w.get(`.vf-node[data-id="${id}"]`).trigger('click'); await flushPromises(); };
const canvas = (w: VueWrapper) => w.getComponent({ name: 'VueFlowStub' });
const edgeIds = (w: VueWrapper) => w.findAll('[data-test=vf-edge]').map((e) => e.attributes('data-id'));
async function answerConfirm(ok: boolean) {
  await flushPromises();
  expect(confirmState.value, 'a confirm is open').not.toBeNull();
  confirmState.value!.resolve(ok);
  await flushPromises();
}
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

beforeEach(() => { localStorage.clear(); applyRemoteAccess('full'); });
afterEach(() => { applyRemoteAccess('full'); confirmState.value?.resolve(false); document.body.innerHTML = ''; });

describe('the Flowchart tab', () => {
  it('draws one node per step plus start and end, each by kind, and the edges with their labels', async () => {
    const w = await editor(fakeApi().api);
    const ids = w.findAll('[data-test=flow-node]').map((n) => n.attributes('data-node'));
    expect(ids).toEqual(['__start', ...INBOX_SMS_ALERT.steps!.map((s) => s.id), '__end']);
    expect(node(w, '__start').classes()).toContain('round');
    expect(node(w, '__end').classes()).toContain('round');
    expect(node(w, 'any').classes()).toContain('diamond');
    expect(node(w, 'any').get('[data-test=flow-node-detail]').text()).toBe('{count(vars.res.messages)} gt 0');
    expect(node(w, 'list').attributes('data-kind')).toBe('integration');
    expect(node(w, 'list').get('[data-test=flow-node-detail]').text()).toBe('Support inbox.list');
    expect(node(w, 'each').get('[data-test=flow-node-detail]').text()).toBe('{vars.res.messages} as m');
    expect(node(w, 'note').attributes('data-kind')).toBe('file');
    expect(node(w, 'count').attributes('data-kind')).toBe('set');
    const label = (id: string) => w.get(`[data-test=vf-edge][data-id="${id}"]`).attributes('data-label');
    expect(label('any->then')).toBe('yes');
    expect(label('any->else')).toBe('no');
    expect(label('each->body')).toBe('each');
    expect(label('each->then')).toBe('done');
    expect(w.get('[data-test=vf-edge][data-id="each->body"]').classes()).toContain('dashed');
    expect(w.get('[data-test=vf-edge][data-id="__start->start"]').attributes('data-target')).toBe('list');
    // The controls and the minimap's toggle are on the canvas; the palette and the editing tools are there for an editor.
    expect(w.find('[data-test=vf-controls]').exists()).toBe(true);
    expect(w.find('[data-test=flow-minimap-toggle]').exists()).toBe(true);
    expect(w.find('[data-test=flow-palette]').exists()).toBe(true);
    expect(canvas(w).props('nodesDraggable')).toBe(true);
  });

  it('a switch shows its cases by value and its default', async () => {
    const doc: WorkflowDocument = { ...INBOX_SMS_ALERT, start: 'pick', steps: [
      { id: 'pick', type: 'switch', on: '{settings.folder}', cases: { INBOX: 'done', Spam: 'done' }, default: null },
      { id: 'done', type: 'end' },
    ] };
    const w = await editor(fakeApi({ doc }).api);
    expect(node(w, 'pick').classes()).toContain('diamond');
    expect(edgeIds(w)).toEqual(['__start->start', 'pick->case:INBOX', 'pick->case:Spam', 'pick->default']);
    expect(w.get('[data-test=vf-edge][data-id="pick->case:Spam"]').attributes('data-label')).toBe('Spam');
  });

  it('a panel edit updates the document and the JSON view, and the panel lists the step\'s problems', async () => {
    const check: Check = { ok: false, schemaReferences: [], issues: [{ stepId: 'mark', code: 'unknown_ref', message: 'vars.last has no field idd' }] };
    const w = await editor(fakeApi({ check }).api);
    await pick(w, 'mark');
    const panel = w.get('[data-test=flow-panel]');
    expect(panel.attributes('data-node')).toBe('mark');
    expect(panel.get('[data-test=flow-panel-problem]').text()).toBe('vars.last has no field idd');
    expect(panel.get('[data-test=flow-panel-tab-design]').attributes('aria-selected')).toBe('true');
    await panel.get('[data-test=flow-panel-tab-json]').trigger('click');
    const area = panel.get('[data-test=flow-panel-json]');
    expect(JSON.parse((area.element as HTMLTextAreaElement).value)).toEqual(INBOX_SMS_ALERT.steps!.find((s) => s.id === 'mark'));
    await area.setValue(pretty({ id: 'mark', type: 'set', var: 'lastId', value: '{vars.last.uid}', next: 'log' }));
    await flushPromises();
    expect(stepOf(w, 'mark')!.value).toBe('{vars.last.uid}');
    expect(jsonText(w)).toContain('"value": "{vars.last.uid}"');
    expect(w.find('[data-test=editor-dirty]').exists()).toBe(true);
    // Invalid JSON in the panel leaves the document as it was.
    await area.setValue('{ "id": ');
    await flushPromises();
    expect(w.get('[data-test=flow-panel-error]').text()).toContain('Not valid JSON');
    expect(stepOf(w, 'mark')!.value).toBe('{vars.last.uid}');
    // A changed id renames the step and keeps it selected.
    await area.setValue(pretty({ id: 'remember', type: 'set', var: 'lastId', value: '{vars.last.id}', next: 'log' }));
    await flushPromises();
    expect(stepOf(w, 'remember')).toBeTruthy();
    expect(w.get('[data-test=flow-panel]').attributes('data-node')).toBe('remember');
    // And a JSON edit shows on the flowchart.
    await w.get('[data-test=editor-tab-json]').trigger('click');
    const edited = docOf(w);
    edited.steps!.find((s) => s.id === 'list')!.action = 'search';
    await w.get('[data-test=json-text]').setValue(pretty(edited));
    await w.get('[data-test=editor-tab-flowchart]').trigger('click');
    await flushPromises();
    expect(node(w, 'list').get('[data-test=flow-node-detail]').text()).toBe('Support inbox.search');
  });

  it('the palette adds a step of the chosen type with a unique id and opens it', async () => {
    const w = await editor(fakeApi().api);
    expect(w.findAll('[data-test=flow-palette] button')).toHaveLength(11);
    await w.get('[data-test=palette-log]').trigger('click');
    await w.get('[data-test=palette-if]').trigger('click');
    await flushPromises();
    const steps = docOf(w).steps!;
    expect(steps.slice(-2).map((s) => [s.id, s.type])).toEqual([['step12', 'log'], ['step13', 'if']]);
    expect(new Set(steps.map((s) => s.id)).size).toBe(steps.length);
    expect(node(w, 'step13').classes()).toContain('diamond');
    expect(w.get('[data-test=flow-panel]').attributes('data-node')).toBe('step13');
  });

  it('the palette groups its blocks as Flow, Data and Do, and has one File block', async () => {
    const w = await editor(fakeApi().api);
    const group = (g: string) => w.get(`[data-test=palette-group-${g}]`).findAll('button').map((b) => b.attributes('data-test'));
    expect(group('flow')).toEqual(['palette-if', 'palette-switch', 'palette-forEach', 'palette-while', 'palette-end']);
    expect(group('data')).toEqual(['palette-set']);
    expect(group('do')).toEqual(['palette-integration', 'palette-file', 'palette-log', 'palette-tell', 'palette-code']);
  });

  it('the File block adds a read; its Operation picks another file step type and keeps the path', async () => {
    const w = await editor(fakeApi().api);
    await w.get('[data-test=palette-file]').trigger('click');
    await flushPromises();
    const id = docOf(w).steps!.at(-1)!.id;
    expect(docOf(w).steps!.at(-1)!.type).toBe('file.readAllText');
    const op = w.get('[data-test=form-file-op]');
    expect(op.findAll('option')).toHaveLength(9);
    await op.setValue('file.appendLine');
    await flushPromises();
    const step = docOf(w).steps!.find((s) => s.id === id)!;
    expect(step).toMatchObject({ type: 'file.appendLine', path: '', text: '' });
    expect(node(w, id).attributes('data-kind')).toBe('file');
  });

  it('Move a file: To and a Replace checkbox (off, then saved as overwrite: true); List files: a pattern hint and a recursive checkbox', async () => {
    const w = await editor(fakeApi().api);
    await w.get('[data-test=palette-file]').trigger('click');
    await flushPromises();
    const id = docOf(w).steps!.at(-1)!.id;
    const stepOf = () => docOf(w).steps!.find((s) => s.id === id)!;
    await w.get('[data-test=form-file-op]').setValue('file.move');
    await flushPromises();
    expect(stepOf()).toEqual({ id, type: 'file.move', path: '', to: '', next: null });
    expect(w.find('[data-test=expr-field][data-name=to]').exists()).toBe(true);
    expect(w.find('[data-test=expr-field][data-name=text]').exists()).toBe(false);
    expect(w.find('[data-test=form-into]').exists()).toBe(true);
    const overwrite = w.get('[data-test=form-file-overwrite]');
    expect((overwrite.element as HTMLInputElement).checked).toBe(false);
    await overwrite.setValue(true);
    await flushPromises();
    expect(stepOf().overwrite).toBe(true);
    await w.get('[data-test=form-file-overwrite]').setValue(false);
    await flushPromises();
    expect('overwrite' in stepOf()).toBe(false);

    await w.get('[data-test=form-file-op]').setValue('file.list');
    await flushPromises();
    expect(stepOf()).toEqual({ id, type: 'file.list', path: '', pattern: '*', next: null });
    expect(w.get('[data-test=expr-field][data-name=pattern] input').attributes('placeholder')).toBe('*.jpg;*.jpeg;*.heic;*.png');
    expect(w.find('[data-test=form-file-overwrite]').exists()).toBe(false);
    await w.get('[data-test=form-file-recursive]').setValue(true);
    await flushPromises();
    expect(stepOf().recursive).toBe(true);
  });

  it('the Start node opens its triggers, with a way to the whole workflow', async () => {
    const w = await editor(fakeApi().api);
    await w.get('.vf-node[data-id="__start"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=start-pane]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-accepts]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-name]').exists()).toBe(false);
    await w.get('[data-test=start-pane-workflow]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=start-pane]').exists()).toBe(false);
    expect(w.find('[data-test=workflow-name]').exists()).toBe(true);
  });

  it('undo and redo cover more than 20 edits, from the toolbar and with Ctrl+Z / Ctrl+Y', async () => {
    const w = await editor(fakeApi().api);
    const original = jsonText(w);
    const texts = [original];
    for (let i = 0; i < 22; i++) {
      await w.get('[data-test=palette-set]').trigger('click');
      texts.push(jsonText(w));
    }
    expect(docOf(w).steps).toHaveLength(11 + 22);
    const chart = w.get('[data-test=flowchart]');
    for (let i = 21; i >= 11; i--) {
      await w.get('[data-test=flow-undo]').trigger('click');
      expect(jsonText(w)).toBe(texts[i]);
    }
    for (let i = 10; i >= 0; i--) {
      await chart.trigger('keydown', { key: 'z', ctrlKey: true });
      expect(jsonText(w)).toBe(texts[i]);
    }
    expect(jsonText(w)).toBe(original);
    expect((w.get('[data-test=flow-undo]').element as HTMLButtonElement).disabled).toBe(true);
    await chart.trigger('keydown', { key: 'y', ctrlKey: true });
    expect(jsonText(w)).toBe(texts[1]);
    await w.get('[data-test=flow-redo]').trigger('click');
    expect(jsonText(w)).toBe(texts[2]);
    await chart.trigger('keydown', { key: 'Z', ctrlKey: true, shiftKey: true });
    expect(jsonText(w)).toBe(texts[3]);
  });

  it('a node with problems gets a badge; the document\'s own problems go on the start node', async () => {
    const check: Check = { ok: false, schemaReferences: [], issues: [
      { stepId: 'text', code: 'unknown_ref', message: 'm.subjekt is not a field of EmailMessage' },
      { stepId: 'text', code: 'bad_arg', message: 'Body is too long' },
      { stepId: null, code: 'bad_setting', message: 'Setting folder has no type' },
    ] };
    const w = await editor(fakeApi({ check }).api);
    expect(node(w, 'text').get('[data-test=flow-node-badge]').text()).toContain('2');
    expect(node(w, '__start').get('[data-test=flow-node-badge]').text()).toContain('1');
    expect(node(w, 'list').find('[data-test=flow-node-badge]').exists()).toBe(false);
  });

  it('a click on a problem in the list selects its node while the Flowchart tab is open', async () => {
    const check: Check = { ok: false, schemaReferences: [], issues: [{ stepId: 'text', code: 'unknown_ref', message: 'm.subjekt is not a field' }] };
    const w = await editor(fakeApi({ check }).api);
    await w.get('[data-test=check-step][data-step=text]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=editor-tab-flowchart]').attributes('aria-selected')).toBe('true');
    expect(w.get('[data-test=flow-panel]').attributes('data-node')).toBe('text');
    expect(node(w, 'text').classes()).toContain('picked');
    await w.get('[data-test=check-issue][data-step=text] button').trigger('click');
    expect(w.get('[data-test=editor-tab-flowchart]').attributes('aria-selected')).toBe('true');
    // On the JSON tab the same click still jumps to the step's JSON.
    await w.get('[data-test=editor-tab-json]').trigger('click');
    await w.get('[data-test=check-step][data-step=text]').trigger('click');
    await flushPromises();
    const area = w.get('[data-test=json-text]').element as HTMLTextAreaElement;
    expect(area.value.slice(area.selectionStart, area.selectionEnd)).toBe('"id": "text"');
  });

  it('a dragged node\'s position is written into layout.nodes and saved; Auto layout clears it', async () => {
    const f = fakeApi();
    const w = await editor(f.api);
    expect(docOf(w).layout).toBeUndefined();
    const listNode = (canvas(w).props('nodes') as any[]).find((n) => n.id === 'list');
    canvas(w).vm.$emit('node-drag-stop', { event: {}, node: { ...listNode, position: { x: 40.4, y: 300.6 } }, nodes: [{ ...listNode, position: { x: 40.4, y: 300.6 } }] });
    await flushPromises();
    expect(docOf(w).layout).toEqual({ nodes: { list: { x: 40, y: 301 } } });
    expect((canvas(w).props('nodes') as any[]).find((n) => n.id === 'list').position).toEqual({ x: 40, y: 301 });
    await w.get('[data-test=editor-save]').trigger('click');
    await flushPromises();
    expect(JSON.parse(f.of('workflows.save')[0]!.params.document).layout).toEqual({ nodes: { list: { x: 40, y: 301 } } });
    await w.get('[data-test=flow-auto-layout]').trigger('click');
    await flushPromises();
    expect(docOf(w).layout).toBeUndefined();
    expect((canvas(w).props('nodes') as any[]).find((n) => n.id === 'list').position).not.toEqual({ x: 40, y: 301 });
  });

  it('a connection from an if asks which branch first; from a one-way step it connects at once', async () => {
    const w = await editor(fakeApi().api);
    canvas(w).vm.$emit('connect', { source: 'any', target: 'log', sourceHandle: null, targetHandle: null });
    await flushPromises();
    const ask = w.get('[data-test=flow-branch-ask]');
    expect(ask.findAll('[data-test=flow-branch]').map((b) => b.attributes('data-branch'))).toEqual(['then', 'else']);
    expect(stepOf(w, 'any')!.else).toBe('done');
    await ask.get('[data-test=flow-branch][data-branch=else]').trigger('click');
    await flushPromises();
    expect(stepOf(w, 'any')!.else).toBe('log');
    expect(w.find('[data-test=flow-branch-ask]').exists()).toBe(false);
    // Cancel leaves it.
    canvas(w).vm.$emit('connect', { source: 'any', target: 'keep' });
    await flushPromises();
    await w.get('[data-test=flow-branch-cancel]').trigger('click');
    expect(stepOf(w, 'any')!.then).toBe('each');
    // set has only next; to the end node it writes null.
    canvas(w).vm.$emit('connect', { source: 'mark', target: '__end' });
    await flushPromises();
    expect(w.find('[data-test=flow-branch-ask]').exists()).toBe(false);
    expect(stepOf(w, 'mark')!.next).toBeNull();
  });

  it('asks which switch branch to connect and preserves the other branches', async () => {
    const doc: WorkflowDocument = { start: 'pick', steps: [
      { id: 'pick', type: 'switch', on: 'mode', cases: { quiet: 'a', loud: 'a' }, default: 'a' },
      { id: 'a', type: 'log', text: 'a', next: 'b' }, { id: 'b', type: 'end' },
    ] };
    const w = await editor(fakeApi({ doc }).api);
    canvas(w).vm.$emit('connect', { source: 'pick', target: 'b' });
    await flushPromises();
    const ask = w.get('[data-test=flow-branch-ask]');
    expect(ask.findAll('[data-test=flow-branch]').map((b) => b.attributes('data-branch'))).toEqual(['case:quiet', 'case:loud', 'default']);
    expect(stepOf(w, 'pick')!.cases).toEqual({ quiet: 'a', loud: 'a' });
    await ask.get('[data-test=flow-branch][data-branch="case:loud"]').trigger('click');
    await flushPromises();
    expect(stepOf(w, 'pick')!.cases).toEqual({ quiet: 'a', loud: 'b' });
    expect(stepOf(w, 'pick')!.default).toBe('a');
  });

  it('deletes a connection; deleting a step asks first, and the check after it shows the refs left behind', async () => {
    const f = fakeApi({ recheck: (doc) => ({ ok: false, schemaReferences: [], issues: doc.steps!.some((s) => s.id === 'keep') ? [] : [
      { stepId: 'each', code: 'unknown_step', message: 'then goes to keep, which is not a step' },
    ] }) });
    const w = await editor(f.api);
    await w.get('[data-test=vf-edge][data-id="mark->next"]').trigger('click');
    await w.get('[data-test=flow-delete-edge]').trigger('click');
    await flushPromises();
    expect(stepOf(w, 'mark')!.next).toBeNull();
    expect(edgeIds(w)).toContain('mark->next'); // a null next outside a loop ends the run: the edge goes to the end node
    expect(w.get('[data-test=vf-edge][data-id="mark->next"]').attributes('data-target')).toBe('__end');

    await pick(w, 'keep');
    await w.get('[data-test=flow-delete-node]').trigger('click');
    await answerConfirm(false);
    expect(stepOf(w, 'keep')).toBeTruthy();
    await w.get('[data-test=flowchart]').trigger('keydown', { key: 'Delete' });
    expect(confirmState.value?.message).toContain('Delete step keep?');
    await answerConfirm(true);
    expect(stepOf(w, 'keep')).toBeUndefined();
    expect(stepOf(w, 'each')!.then).toBe('keep'); // the ref stays, so the check reports it
    const before = f.of('workflows.check').length;
    await wait(500);
    await flushPromises();
    expect(f.of('workflows.check').length).toBe(before + 1);
    expect(node(w, 'each').find('[data-test=flow-node-badge]').exists()).toBe(true);
    expect(w.get('[data-test=check-issue][data-step=each]').text()).toContain('which is not a step');
  });

  it('a Visitor can only look: no palette, no tools, no drag or connect, a read-only panel, and edits are refused', async () => {
    applyRemoteAccess('read');
    const w = await editor(fakeApi().api);
    const before = jsonText(w);
    expect(w.find('[data-test=flow-palette]').exists()).toBe(false);
    expect(w.find('[data-test=flow-undo]').exists()).toBe(false);
    expect(w.find('[data-test=flow-auto-layout]').exists()).toBe(false);
    expect(w.find('[data-test=flow-readonly]').exists()).toBe(true);
    expect(canvas(w).props('nodesDraggable')).toBe(false);
    expect(canvas(w).props('nodesConnectable')).toBe(false);
    expect(w.findAll('.vf-node').every((n) => n.attributes('data-draggable') === 'false')).toBe(true);
    // Pan and zoom: the controls are still there.
    expect(w.find('[data-test=vf-controls]').exists()).toBe(true);
    await pick(w, 'mark');
    await w.get('[data-test=flow-panel-tab-json]').trigger('click');
    const area = w.get('[data-test=flow-panel-json]');
    expect(area.attributes('readonly')).toBeDefined();
    expect(w.find('[data-test=flow-panel-delete]').exists()).toBe(false);
    await area.setValue(pretty({ id: 'mark', type: 'set', var: 'x', value: 1, next: null }));
    canvas(w).vm.$emit('connect', { source: 'any', target: 'log' });
    canvas(w).vm.$emit('node-drag-stop', { event: {}, node: { id: 'list', position: { x: 1, y: 2 } }, nodes: [] });
    await w.get('[data-test=flowchart]').trigger('keydown', { key: 'Delete' });
    await w.get('[data-test=flowchart]').trigger('keydown', { key: 'z', ctrlKey: true });
    await flushPromises();
    expect(confirmState.value).toBeNull();
    expect(w.find('[data-test=flow-branch-ask]').exists()).toBe(false);
    expect(jsonText(w)).toBe(before);
  });
});

describe('the run view\'s flowchart', () => {
  function run(over: Partial<RunDetail> = {}): RunDetail {
    return {
      id: 41, teamId: 1, team: 'Support', memberId: 9, member: 'InboxWatcher', workflowId: 1, workflow: 'inbox-sms-alert', version: 2, trigger: 'trigger:7',
      status: 'failed', dryRun: false, test: false, input: {}, outputs: null, failedStepId: 'text', reason: 'sms refused', startedAt: at, endedAt: at,
      steps: [
        { seq: 1, stepId: 'list', type: 'integration', input: {}, output: {}, status: 'succeeded', startedAt: at, endedAt: at },
        { seq: 2, stepId: 'any', type: 'if', input: {}, output: true, status: 'succeeded', startedAt: at, endedAt: at },
        { seq: 3, stepId: 'each', type: 'forEach', input: {}, output: null, status: 'succeeded', startedAt: at, endedAt: at },
        { seq: 4, stepId: 'note', type: 'file.appendLine', input: {}, output: null, status: 'succeeded', startedAt: at, endedAt: at },
        { seq: 5, stepId: 'phones', type: 'forEach', input: {}, output: null, status: 'succeeded', startedAt: at, endedAt: at },
        { seq: 6, stepId: 'text', type: 'integration', input: {}, output: null, status: 'failed', startedAt: at, endedAt: at },
      ],
      log: [], ...over,
    };
  }

  it('the Flowchart toggle loads the run\'s version and shows it read-only, the steps taken highlighted and the failed step marked', async () => {
    const f = fakeApi();
    const w = mount(WorkflowRunView, { props: { run: run(), api: f.api }, attachTo: document.body });
    expect(w.find('[data-test=run-flowchart]').exists()).toBe(false);
    await w.get('[data-test=run-flowchart-toggle]').trigger('click');
    await flushPromises();
    expect(f.of('workflows.get')[0]!.params).toEqual({ workflow: 1, version: 2 });
    const chart = w.get('[data-test=run-flowchart] [data-test=flowchart]');
    expect(chart.attributes('data-readonly')).toBe('true');
    expect(w.find('[data-test=flow-palette]').exists()).toBe(false);
    const runOf = (id: string) => w.get(`[data-test=flow-node][data-node="${id}"]`).attributes('data-run');
    for (const id of ['list', 'any', 'each', 'note', 'phones']) expect(runOf(id), id).toBe('taken');
    expect(runOf('text')).toBe('failed');
    expect(runOf('keep')).toBe('');
    expect(runOf('done')).toBe('');
    await w.get('[data-test=run-flowchart-toggle]').trigger('click');
    expect(w.find('[data-test=run-flowchart]').exists()).toBe(false);
  });

  it('a test run\'s view uses the document it is given, without a call', async () => {
    const f = fakeApi();
    const result = { runId: 77, status: 'succeeded' as const, failedStepId: null, reason: null, outputs: null, log: [],
      steps: [{ seq: 1, stepId: 'list', type: 'integration', input: {}, output: [], status: 'succeeded', startedAt: at, endedAt: at }] };
    const w = mount(WorkflowRunView, { props: { run: result, doc: INBOX_SMS_ALERT }, attachTo: document.body });
    await w.get('[data-test=run-flowchart-toggle]').trigger('click');
    await flushPromises();
    expect(f.calls).toHaveLength(0);
    expect(w.get('[data-test=flow-node][data-node=list]').attributes('data-run')).toBe('taken');
    expect(w.get('[data-test=flow-node][data-node=any]').attributes('data-run')).toBe('');
  });

  it('without a document or an api there is no toggle', () => {
    const w = mount(WorkflowRunView, { props: { run: run() } });
    expect(w.find('[data-test=run-flowchart-toggle]').exists()).toBe(false);
  });
});
