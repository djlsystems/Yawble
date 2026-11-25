// @vitest-environment happy-dom
// The Code and Loop (while) steps in the shell: their blocks in the palette, their nodes and edges, their forms (Code:
// inputs, the code area where Tab indents, returns and save into; Loop: the If condition builder, max, body and then),
// the connections and the round trip back into the script, and the check's problems on each step. Vue Flow's canvas is
// replaced by a stub that renders each node through the component's own node slot and passes connections on. Fakes
// only: no core; made-up names and values only.
import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ScriptFlowchart from '../../src/renderer/src/components/scripts/ScriptFlowchart.vue';
import StepForm from '../../src/renderer/src/components/scripts/StepForm.vue';
import { pretty } from '../../src/renderer/src/scripts/api';
import { ioInText, ioOutText, NATIVE_TYPES, schemaFields, stepIO, type SchemaFields } from '../../src/renderer/src/scripts/catalog';
import {
  END_ID, PALETTE, addStep, branchesOf, codeDetail, connect, docToGraph, removeEdge, replaceStep,
} from '../../src/renderer/src/scripts/flowchart';
import type { CheckIssue, ScriptDocument, ScriptStep, ScriptVariable } from '../../src/renderer/src/scripts/types';

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
  const { defineComponent } = await import('vue');
  return { Controls: defineComponent({ name: 'ControlsStub', setup: () => () => null }) };
});
vi.mock('@vue-flow/minimap', async () => {
  const { defineComponent } = await import('vue');
  return { MiniMap: defineComponent({ name: 'MiniMapStub', setup: () => () => null }) };
});

enableAutoUnmount(afterEach);
afterEach(() => { document.body.innerHTML = ''; });

const FIELDS: SchemaFields = {
  Staleness: schemaFields({ type: 'object', properties: { stale: { type: 'boolean' }, age: { type: 'number' } } }),
};

/** A paging script: a loop that fetches while there is a cursor, then scores the result in code. */
function paging(): ScriptDocument {
  return {
    format: 'yawble-script/1', id: 'paging', name: 'Paging', start: 'page',
    settings: [{ name: 'maxAge', type: 'int', default: 24 }],
    variables: [{ name: 'cursor', type: 'string', initial: 'first' }, { name: 'staleness', type: 'Staleness' }],
    steps: [
      { id: 'page', type: 'while', when: { left: '{vars.cursor}', op: 'exists' }, body: 'fetch', then: 'score', max: 50 },
      { id: 'fetch', type: 'set', var: 'cursor', value: '', next: null },
      { id: 'score', type: 'code', inputs: { max: '{settings.maxAge}', label: 'page' }, code: 'return { stale: inputs.max > 1, age: 1 };',
        returns: 'Staleness', into: 'staleness', next: 'done' },
      { id: 'done', type: 'end' },
    ],
    custom: { kept: true },
  };
}

/** The form on one step; each change is applied (as the flowchart does) and kept in `changes`. */
function form(step: ScriptStep, opts: { doc?: ScriptDocument; readonly?: boolean } = {}) {
  const base = opts.doc ?? { steps: [], variables: [] };
  const state = { step, variables: [...(base.variables ?? [])] as ScriptVariable[], changes: [] as ScriptStep[], added: [] as ScriptVariable[] };
  const ctx = () => ({
    doc: { ...base, steps: [...(base.steps ?? []).filter((s) => s.id !== state.step.id), state.step], variables: state.variables } as ScriptDocument,
    catalog: null, schemas: FIELDS,
  });
  const w: VueWrapper = mount(StepForm, {
    attachTo: document.body,
    props: {
      step, ctx: ctx(), readonly: !!opts.readonly,
      onChange: (s: ScriptStep, v?: ScriptVariable) => {
        state.step = s;
        state.changes.push(s);
        if (v) { state.added.push(v); state.variables = [...state.variables, v]; }
        void w.setProps({ step: s, ctx: ctx() });
      },
    } as any,
  });
  return { w, state };
}
async function changeTo(w: VueWrapper, sel: string, value: string) { const el = w.get(sel); await el.setValue(value); await el.trigger('change'); await flushPromises(); }
const valueOf = (w: VueWrapper, sel: string) => (w.get(sel).element as HTMLInputElement).value;
/** An ExpressionField's text box, by the field's name. */
const expr = (name: string) => `[data-test=expr-field][data-name="${name}"] [data-test=expr-input]`;

function chart(doc: ScriptDocument, extra: Record<string, unknown> = {}) {
  const w = mount(ScriptFlowchart, {
    props: { modelValue: pretty(doc), schemas: FIELDS, 'onUpdate:modelValue': (t: string) => w.setProps({ modelValue: t }), ...extra } as any,
  });
  return w;
}
const docOf = (w: VueWrapper) => JSON.parse(w.props('modelValue') as string) as ScriptDocument;
const node = (w: VueWrapper, id: string) => w.get(`[data-test=flow-node][data-node="${id}"]`);

// ---- the blocks and their nodes

describe('the Code and Loop blocks', () => {
  it('Loop is in the Flow group right after For each; Code is in the Do group', () => {
    const keys = (g: string) => PALETTE.find((x) => x.group === g)!.blocks.map((b) => b.key);
    expect(keys('Flow')).toEqual(['if', 'switch', 'forEach', 'while', 'end']);
    expect(keys('Do')).toEqual(['integration', 'file', 'log', 'tell', 'code']);
    expect(PALETTE.flatMap((g) => g.blocks).find((b) => b.key === 'while')).toEqual({ key: 'while', type: 'while', kind: 'while', label: 'Loop' });
    expect(PALETTE.flatMap((g) => g.blocks).find((b) => b.key === 'code')).toEqual({ key: 'code', type: 'code', kind: 'code', label: 'Code' });
  });

  it('the blocks add a blank loop and a blank code step', () => {
    const empty: ScriptDocument = { start: 'a', steps: [{ id: 'a', type: 'end' }] };
    const loop = addStep(empty, 'while');
    expect(loop.doc.steps!.at(-1)).toEqual({ id: loop.id, type: 'while', when: { left: '', op: 'eq', right: '' }, body: null, then: null });
    const code = addStep(empty, 'code');
    expect(code.doc.steps!.at(-1)).toEqual({ id: code.id, type: 'code', inputs: {}, code: '', returns: 'any', next: null });
  });

  it('a code node reads "code → <returns>" (any when none is given); a loop node shows its condition as an If does', () => {
    const { nodes } = docToGraph(paging());
    const byId = Object.fromEntries(nodes.map((n) => [n.id, n]));
    expect(byId.page.kind).toBe('while');
    expect(byId.page.detail).toBe('{vars.cursor} exists');
    expect(byId.score.kind).toBe('code');
    expect(byId.score.detail).toBe('code → Staleness');
    expect(codeDetail({ id: 'x', type: 'code', code: 'return 1;' })).toBe('code → any');
    expect(codeDetail({ id: 'x', type: 'code', code: '', returns: '  ' })).toBe('code → any');
    const ifDetail = docToGraph({ start: 'i', steps: [{ id: 'i', type: 'if', when: { left: '{vars.cursor}', op: 'exists' }, then: null, else: null }] })
      .nodes.find((n) => n.id === 'i')!.detail;
    expect(byId.page.detail).toBe(ifDetail);
  });

  it('a loop is drawn as For each: a dashed body edge (repeat) and a then edge (done); its body ends the pass, not the run', () => {
    const { edges } = docToGraph(paging());
    expect(edges.find((e) => e.id === 'page->body')).toMatchObject({ target: 'fetch', kind: 'body', label: 'repeat', dashed: true });
    expect(edges.find((e) => e.id === 'page->then')).toMatchObject({ target: 'score', kind: 'then', label: 'done', dashed: false });
    // fetch's null next ends the pass: no edge to the end node.
    expect(edges.some((e) => e.source === 'fetch')).toBe(false);
    expect(edges.find((e) => e.id === 'score->next')).toMatchObject({ target: 'done', kind: 'next', dashed: false });
  });

  it('on the canvas: both blocks in their groups, the nodes with their kind, detail and typed in and out', async () => {
    const w = chart(paging());
    await flushPromises();
    const group = (g: string) => w.get(`[data-test=palette-group-${g}]`).findAll('button').map((b) => b.attributes('data-test'));
    expect(group('flow')).toContain('palette-while');
    expect(group('do')).toContain('palette-code');
    expect(w.get('[data-test=palette-while]').text()).toBe('Loop');
    expect(w.get('[data-test=palette-code]').text()).toBe('Code');
    expect(node(w, 'page').attributes('data-kind')).toBe('while');
    expect(node(w, 'page').get('[data-test=flow-node-detail]').text()).toBe('{vars.cursor} exists');
    expect(node(w, 'score').attributes('data-kind')).toBe('code');
    expect(node(w, 'score').get('[data-test=flow-node-detail]').text()).toBe('code → Staleness');
    expect(node(w, 'score').get('[data-test=flow-node-in]').text()).toBe('in max: int, label: string');
    expect(node(w, 'score').get('[data-test=flow-node-out]').text()).toBe('out Staleness → staleness');

    await w.get('[data-test=palette-code]').trigger('click');
    await flushPromises();
    const added = docOf(w).steps!.at(-1)!;
    expect(added.type).toBe('code');
    expect(w.get('[data-test=step-form]').attributes('data-type')).toBe('code');
    expect(node(w, added.id).get('[data-test=flow-node-detail]').text()).toBe('code → any');
  });
});

// ---- the Code form

describe('the Code form', () => {
  const blank = (extra: Partial<ScriptStep> = {}): ScriptStep => ({ id: 'score', type: 'code', inputs: {}, code: '', returns: 'any', next: null, ...extra });

  it('adds input rows, each a name and a value, renames and removes them', async () => {
    const { w, state } = form(blank());
    expect(w.findAll('[data-test=form-code-input]')).toHaveLength(0);
    await w.get('[data-test=form-code-input-add]').trigger('click');
    await w.get('[data-test=form-code-input-add]').trigger('click');
    await flushPromises();
    expect(state.step.inputs).toEqual({ input1: '', input2: '' });
    expect(w.findAll('[data-test=form-code-input]').map((r) => r.attributes('data-name'))).toEqual(['input1', 'input2']);

    const first = w.findAll('[data-test=form-code-input-name]')[0]!;
    await first.setValue('msg');
    await first.trigger('change');
    await flushPromises();
    expect(Object.keys(state.step.inputs as object)).toEqual(['msg', 'input2']);
    // A name that is not an identifier, or one already taken, is not used.
    for (const bad of ['two words', 'input2', '']) {
      const el = w.findAll('[data-test=form-code-input-name]')[0]!;
      await el.setValue(bad);
      await el.trigger('change');
      await flushPromises();
      expect(Object.keys(state.step.inputs as object)).toEqual(['msg', 'input2']);
    }

    await w.findAll('[data-test=form-code-input-remove]')[1]!.trigger('click');
    await flushPromises();
    expect(state.step.inputs).toEqual({ msg: '' });
  });

  it("an input's value is an expression field, typed by its expression in the step's in", async () => {
    const doc = paging();
    const { w, state } = form(blank({ inputs: { max: '' } }), { doc });
    await w.get(expr('input-max')).setValue('{settings.maxAge}');
    await flushPromises();
    expect(state.step.inputs).toEqual({ max: '{settings.maxAge}' });
    expect(w.get('[data-test=form-io-in]').text()).toBe('max: int');
  });

  it('the code area is monospace; Tab indents at the cursor and Shift+Tab is left alone', async () => {
    const { w, state } = form(blank({ code: 'if (x) {\nreturn 1;\n}' }));
    const area = w.get('[data-test=form-code]');
    expect(area.element.tagName).toBe('TEXTAREA');
    expect(area.classes()).toContain('code');
    const el = area.element as HTMLTextAreaElement;
    el.selectionStart = el.selectionEnd = 9; // the start of "return 1;"
    const tab = new KeyboardEvent('keydown', { key: 'Tab', cancelable: true, bubbles: true });
    el.dispatchEvent(tab);
    await flushPromises();
    expect(tab.defaultPrevented).toBe(true);
    expect(state.step.code).toBe('if (x) {\n  return 1;\n}');
    expect(el.selectionStart).toBe(11);

    const shiftTab = new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, cancelable: true, bubbles: true });
    el.dispatchEvent(shiftTab);
    await flushPromises();
    expect(shiftTab.defaultPrevented).toBe(false);
    expect(state.changes).toHaveLength(1);

    await area.setValue('return inputs.max;');
    await flushPromises();
    expect(state.step.code).toBe('return inputs.max;');
  });

  it('Tab replaces a selection with the indent; read-only, Tab does nothing', async () => {
    const { w, state } = form(blank({ code: 'abcdef' }));
    const el = w.get('[data-test=form-code]').element as HTMLTextAreaElement;
    el.selectionStart = 2; el.selectionEnd = 4;
    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', cancelable: true, bubbles: true }));
    await flushPromises();
    expect(state.step.code).toBe('ab  ef');

    const ro = form(blank({ code: 'x' }), { readonly: true });
    const roEl = ro.w.get('[data-test=form-code]').element as HTMLTextAreaElement;
    const tab = new KeyboardEvent('keydown', { key: 'Tab', cancelable: true, bubbles: true });
    roEl.dispatchEvent(tab);
    await flushPromises();
    expect(tab.defaultPrevented).toBe(false);
    expect(ro.state.changes).toHaveLength(0);
  });

  it('returns offers the native types and the schemas; empty means any', async () => {
    const { w, state } = form(blank());
    const returns = w.get('[data-test=form-code-returns]');
    expect(returns.attributes('list')).toBe('step-form-types');
    const offered = w.get('datalist#step-form-types').findAll('option').map((o) => o.attributes('value'));
    expect(offered).toEqual([...NATIVE_TYPES, 'Staleness']);
    expect(valueOf(w, '[data-test=form-code-returns]')).toBe('any');

    await changeTo(w, '[data-test=form-code-returns]', 'Staleness');
    expect(state.step.returns).toBe('Staleness');
    expect(w.get('[data-test=form-io-out]').text()).toBe('Staleness');
    await changeTo(w, '[data-test=form-code-returns]', '  ');
    expect('returns' in state.step).toBe(false);
    expect(w.get('[data-test=form-io-out]').text()).toBe('any');
  });

  it('saves the result into a variable, or a new one typed by returns', async () => {
    const doc: ScriptDocument = { steps: [], variables: [{ name: 'result', type: 'any' }] };
    const { w, state } = form(blank({ returns: 'Staleness' }), { doc });
    const choices = w.get('[data-test=form-into]').findAll('option').map((o) => o.attributes('value'));
    expect(choices).toEqual(['', 'result', '__new']);
    await changeTo(w, '[data-test=form-into]', 'result');
    expect(state.step.into).toBe('result');

    await changeTo(w, '[data-test=form-into]', '__new');
    expect(valueOf(w, '[data-test=form-into-new-type]')).toBe('Staleness');
    await w.get('[data-test=form-into-new-name]').setValue('staleness');
    await w.get('[data-test=form-into-new-add]').trigger('click');
    await flushPromises();
    expect(state.step.into).toBe('staleness');
    expect(state.added).toEqual([{ name: 'staleness', type: 'Staleness', scope: 'run' }]);
    expect(w.get('[data-test=form-io-out]').text()).toBe('Staleness → staleness');

    await changeTo(w, '[data-test=form-into]', '');
    expect('into' in state.step).toBe(false);
  });

  it('has a Next, and says what the code has and its limits', () => {
    const { w } = form(blank());
    expect(w.find('[data-test=form-next]').exists()).toBe(true);
    expect(w.text()).toContain('inputs.<name> and log(text)');
    expect(w.text()).toContain('2 seconds and use 64 MB');
  });
});

// ---- the Loop form

describe('the Loop form', () => {
  const blank = (extra: Partial<ScriptStep> = {}): ScriptStep => ({ id: 'page', type: 'while', when: { left: '', op: 'eq', right: '' }, body: null, then: null, ...extra });
  const doc: ScriptDocument = {
    steps: [{ id: 'fetch', type: 'set', var: 'cursor', value: '', next: null }, { id: 'done', type: 'end' }],
    variables: [{ name: 'cursor', type: 'string' }],
  };

  it("uses the If's condition builder: left, operator, right; exists and empty take no right", async () => {
    const { w, state } = form(blank(), { doc });
    expect(w.find('[data-test=form-when-op]').exists()).toBe(true);
    await w.get(expr('when-left')).setValue('{vars.cursor}');
    await flushPromises();
    expect((state.step.when as Record<string, unknown>).left).toBe('{vars.cursor}');
    expect(w.find(expr('when-right')).exists()).toBe(true);
    await changeTo(w, '[data-test=form-when-op]', 'exists');
    expect(state.step.when).toEqual({ left: '{vars.cursor}', op: 'exists' });
    expect(w.find(expr('when-right')).exists()).toBe(false);
    await changeTo(w, '[data-test=form-when-op]', 'ne');
    expect(state.step.when).toEqual({ left: '{vars.cursor}', op: 'ne', right: '' });
  });

  it('a combined condition is edited on the JSON tab', () => {
    const { w } = form(blank({ when: { all: [{ left: '{vars.cursor}', op: 'exists' }] } }), { doc });
    expect(w.find('[data-test=form-when-combined]').exists()).toBe(true);
    expect(w.find('[data-test=form-when-op]').exists()).toBe(false);
  });

  it('max: empty is the default 100, a whole number from 1 to 10000 is kept, anything else shows a problem and changes nothing', async () => {
    const { w, state } = form(blank(), { doc });
    const max = w.get('[data-test=form-while-max]');
    expect(max.attributes('type')).toBe('number');
    expect(max.attributes('placeholder')).toBe('100');
    expect(max.attributes('min')).toBe('1');
    expect(max.attributes('max')).toBe('10000');
    expect(w.text()).toContain('Empty: 100. At most 10000');

    await changeTo(w, '[data-test=form-while-max]', '50');
    expect(state.step.max).toBe(50);
    await changeTo(w, '[data-test=form-while-max]', '10000');
    expect(state.step.max).toBe(10000);
    const kept = state.changes.length;
    for (const bad of ['0', '10001', '2.5', '-3']) {
      await changeTo(w, '[data-test=form-while-max]', bad);
      expect(w.get('[data-test=form-while-max-problem]').text()).toBe('Max is a whole number from 1 to 10000.');
      expect(state.step.max).toBe(10000);
    }
    expect(state.changes).toHaveLength(kept);
    await changeTo(w, '[data-test=form-while-max]', '');
    expect('max' in state.step).toBe(false);
    expect(w.find('[data-test=form-while-max-problem]').exists()).toBe(false);
  });

  it('shows the max it has, and picks body and then; no Next', async () => {
    const { w, state } = form(blank({ max: 50 }), { doc });
    expect(valueOf(w, '[data-test=form-while-max]')).toBe('50');
    expect(w.find('[data-test=form-next]').exists()).toBe(false);
    expect(w.find('[data-test=form-else]').exists()).toBe(false);
    await changeTo(w, '[data-test=form-body]', 'fetch');
    await changeTo(w, '[data-test=form-then]', 'done');
    expect(state.step).toMatchObject({ body: 'fetch', then: 'done' });
    await changeTo(w, '[data-test=form-body]', '');
    expect(state.step.body).toBeNull();
  });

  it("the step's in is the condition's sides, typed", () => {
    const d: ScriptDocument = { ...doc, steps: [...doc.steps!, blank({ when: { left: '{vars.cursor}', op: 'ne', right: 'last' } })] };
    expect(ioInText(stepIO({ doc: d }, d.steps!.at(-1)!))).toBe('left: string, right: string');
    expect(ioOutText(stepIO({ doc: d }, d.steps!.at(-1)!))).toBe('');
  });
});

// ---- connections and the round trip

describe('connecting and the round trip', () => {
  it('branchesOf: a loop has body and then, as For each; a code step has next', () => {
    const doc = paging();
    expect(branchesOf(doc, 'page')).toEqual([{ kind: 'body' }, { kind: 'then' }]);
    expect(branchesOf(doc, 'score')).toEqual([{ kind: 'next' }]);
  });

  it('connect writes body, then and next into the steps; the graph shows them and every other field stays', () => {
    const bare = paging();
    const page = bare.steps!.find((s) => s.id === 'page')!;
    const score = bare.steps!.find((s) => s.id === 'score')!;
    page.body = null; page.then = null; score.next = null;
    let doc = connect(bare, 'page', 'fetch', { kind: 'body' });
    doc = connect(doc, 'page', 'score', { kind: 'then' });
    doc = connect(doc, 'score', 'done');
    expect(doc).toEqual(paging());
    expect(bare.steps!.find((s) => s.id === 'page')!.body).toBeNull();

    const { edges } = docToGraph(doc);
    expect(edges.filter((e) => e.source === 'page').map((e) => [e.kind, e.target])).toEqual([['body', 'fetch'], ['then', 'score']]);
    // then to the end node writes null; removing the body edge sets it to null.
    const toEnd = connect(doc, 'page', END_ID, { kind: 'then' });
    expect(toEnd.steps!.find((s) => s.id === 'page')!.then).toBeNull();
    expect(docToGraph(toEnd).edges.find((e) => e.id === 'page->then')).toMatchObject({ target: END_ID });
    const noBody = removeEdge(doc, docToGraph(doc).edges.find((e) => e.id === 'page->body')!);
    expect(noBody.steps!.find((s) => s.id === 'page')).toEqual({ ...page, body: null, then: 'score' });
  });

  it('graph to script keeps both steps whole: a JSON round trip and replaceStep keep inputs, code, returns, into and max', () => {
    const doc = paging();
    expect(JSON.parse(pretty(doc))).toEqual(doc);
    const score = doc.steps!.find((s) => s.id === 'score')!;
    const renamed = replaceStep(doc, 'score', { ...score, id: 'rate' });
    expect(renamed.steps!.find((s) => s.id === 'rate')).toEqual({ ...score, id: 'rate' });
    expect(renamed.custom).toEqual({ kept: true });
  });

  it('on the canvas a connection from a loop asks body (repeat) or then (when done); from code it goes straight to next', async () => {
    const bare = paging();
    bare.steps!.find((s) => s.id === 'page')!.body = null;
    bare.steps!.find((s) => s.id === 'score')!.next = null;
    const w = chart(bare);
    await flushPromises();
    const flow = w.findComponent({ name: 'VueFlowStub' });
    flow.vm.$emit('connect', { source: 'page', target: 'fetch', sourceHandle: null, targetHandle: null });
    await flushPromises();
    const picks = w.findAll('[data-test=flow-branch]');
    expect(picks.map((b) => b.text())).toEqual(['body (repeat)', 'then (when done)']);
    await picks[0]!.trigger('click');
    await flushPromises();
    expect(docOf(w).steps!.find((s) => s.id === 'page')!.body).toBe('fetch');

    flow.vm.$emit('connect', { source: 'score', target: 'done', sourceHandle: null, targetHandle: null });
    await flushPromises();
    expect(w.find('[data-test=flow-branch-ask]').exists()).toBe(false);
    expect(docOf(w)).toEqual(paging());
  });
});

// ---- problems

describe("the check's problems on each step", () => {
  const issues: CheckIssue[] = [
    { severity: 'error', code: 'BadCode', message: "score: the code does not parse at line 2, column 5: Unexpected token '}'", stepId: 'score' } as CheckIssue,
    { severity: 'error', code: 'BadMax', message: 'page: max is at most 10000', stepId: 'page' } as CheckIssue,
  ];

  it('the nodes carry their problems and show a badge', async () => {
    const graph = docToGraph(paging(), { issues });
    expect(graph.nodes.find((n) => n.id === 'score')!.problems).toEqual([issues[0]!.message]);
    expect(graph.nodes.find((n) => n.id === 'page')!.problems).toEqual([issues[1]!.message]);
    const w = chart(paging(), { issues });
    await flushPromises();
    expect(node(w, 'score').get('[data-test=flow-node-badge]').text()).toBe('⚠ 1');
    expect(node(w, 'score').attributes('title')).toContain('line 2, column 5');
    expect(node(w, 'page').get('[data-test=flow-node-badge]').text()).toBe('⚠ 1');
  });

  it("the step's panel lists them above its form", async () => {
    const w = chart(paging(), { issues });
    await flushPromises();
    await w.get('.vf-node[data-id=score]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=flow-panel-problem]').map((p) => p.text())).toEqual([issues[0]!.message]);
    expect(w.get('[data-test=step-form]').attributes('data-type')).toBe('code');
    await w.get('.vf-node[data-id=page]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=flow-panel-problem]').map((p) => p.text())).toEqual([issues[1]!.message]);
    expect(w.get('[data-test=step-form]').attributes('data-type')).toBe('while');
  });
});
