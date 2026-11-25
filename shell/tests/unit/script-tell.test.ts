// @vitest-environment happy-dom
// The Tell step in the shell: its helpers (typed in and out, the team's members, the timeout), the Tell block and its
// node on the canvas, its form (the defaults, the member list, the fields only a waiting tell has), a waiting run in the
// run view, the Runs tab and the member card with the link to its workflow, Settings > Scripts, a member's tells per
// run and the test run's made-up replies. Vue Flow's canvas is replaced by a stub that renders each node through the
// component's own node slot. Fakes only: no core; made-up names and values only.
import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AgentCard, TeamSummary, YawbleApi } from '../../src/shared/api';
import ScriptMemberPane from '../../src/renderer/src/components/ScriptMemberPane.vue';
import ScriptsSettings from '../../src/renderer/src/components/ScriptsSettings.vue';
import RunsTab from '../../src/renderer/src/components/scripts/RunsTab.vue';
import ScriptFlowchart from '../../src/renderer/src/components/scripts/ScriptFlowchart.vue';
import ScriptRunView from '../../src/renderer/src/components/scripts/ScriptRunView.vue';
import ScriptTestRun from '../../src/renderer/src/components/scripts/ScriptTestRun.vue';
import StepForm from '../../src/renderer/src/components/scripts/StepForm.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { pretty } from '../../src/renderer/src/scripts/api';
import {
  ioInText, ioOutText, schemaFields, stepIO, tellMembers, tellTeamOf, timeoutHours, timeoutProblem, type ScriptCatalog, type SchemaFields,
} from '../../src/renderer/src/scripts/catalog';
import { workflowsRequest } from '../../src/renderer/src/workflows/page';
import type { RunDetail, ScriptDocument, ScriptStep, ScriptVariable } from '../../src/renderer/src/scripts/types';

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

const at = '2026-11-24T10:00:00Z';

/** scripts.catalog with a teamId: the own team first (Manager first), then another team. */
const CATALOG: ScriptCatalog = {
  integrations: [],
  functions: [],
  tellTargets: [
    { teamId: 1, team: 'Support', members: ['Manager', 'Ava', 'Ben'] },
    { teamId: 2, team: 'Billing', members: ['Manager', 'Cleo'] },
  ],
  tellOtherTeams: true,
};
const FIELDS: SchemaFields = {
  TriageResult: schemaFields({ type: 'object', properties: { refund: { type: 'boolean' }, reason: { type: 'string' } } }),
  TellResult: schemaFields({ type: 'object', properties: { outcome: { type: 'string' }, workflow: { type: 'integer' }, reply: { type: 'string' }, data: {} } }),
};

const blankTell = (extra: Partial<ScriptStep> = {}): ScriptStep => ({ id: 'ask', type: 'tell', member: 'Manager', text: '', next: null, ...extra });

/** The form on one step; each change is applied (as the flowchart does) and kept in `changes`. */
function form(step: ScriptStep, opts: { catalog?: ScriptCatalog; variables?: ScriptVariable[]; readonly?: boolean } = {}) {
  const state = { step, variables: opts.variables ?? ([] as ScriptVariable[]), changes: [] as ScriptStep[], added: [] as ScriptVariable[] };
  const ctx = () => ({ doc: { steps: [state.step], variables: state.variables } as ScriptDocument, catalog: opts.catalog ?? CATALOG, schemas: FIELDS });
  const w: VueWrapper = mount(StepForm, {
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
const options = (w: VueWrapper, sel: string) => w.get(sel).findAll('option').map((o) => [o.attributes('value'), o.text()]);
const valueOf = (w: VueWrapper, sel: string) => (w.get(sel).element as HTMLSelectElement | HTMLInputElement).value;
async function changeTo(w: VueWrapper, sel: string, value: string) { const el = w.get(sel); await el.setValue(value); await el.trigger('change'); await flushPromises(); }

interface Call { method: string; params: any }
function fakeApi(over: Record<string, (p: any) => unknown> = {}) {
  const calls: Call[] = [];
  const api = {
    getInfo: async () => ({}), getAvailableBackends: async () => [], onEvent: () => () => {}, openTerminal: async () => {}, pickFolder: async () => null,
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      const h = over[method];
      if (!h) throw new Error(`no fake for ${method}`);
      return h(params);
    },
  } as unknown as YawbleApi;
  return { api, calls, of: (m: string) => calls.filter((c) => c.method === m) };
}

const run = (id: number, status: string, extra: Record<string, unknown> = {}) => ({
  id, teamId: 1, team: 'Support', memberId: 9, member: 'Triage', scriptId: 5, script: 'refund-triage', version: 2, trigger: 'trigger:4', status,
  dryRun: false, test: false, input: null, outputs: null, failedStepId: null, reason: null, startedAt: at, endedAt: status === 'waiting' ? null : at,
  waitWorkflowId: null, ...extra,
});

beforeEach(() => { localStorage.clear(); applyRemoteAccess('full'); workflowsRequest.value = null; });
afterEach(() => { applyRemoteAccess('full'); document.body.innerHTML = ''; });

// ---- the helpers

describe('the tell helpers', () => {
  it('a tell takes its text in and puts a TellResult into its variable', () => {
    const doc: ScriptDocument = { steps: [blankTell({ into: 'triage' })] };
    const io = stepIO({ doc, catalog: CATALOG, schemas: FIELDS }, doc.steps![0]!);
    expect(ioInText(io)).toBe('text: string');
    expect(ioOutText(io)).toBe('TellResult → triage');
    expect(ioOutText(stepIO({ doc }, blankTell()))).toBe('TellResult');
  });

  it('lists the team\'s agent members with the Manager first; the own team when the step names none', () => {
    expect(tellTeamOf(CATALOG, blankTell())?.team).toBe('Support');
    expect(tellTeamOf(CATALOG, blankTell({ team: 'billing' }))?.team).toBe('Billing');
    expect(tellTeamOf(CATALOG, blankTell({ team: 'Nowhere' }))).toBeNull();
    expect(tellMembers(CATALOG, blankTell())).toEqual(['Manager', 'Ava', 'Ben']);
    expect(tellMembers(CATALOG, blankTell({ team: 'Billing' }))).toEqual(['Manager', 'Cleo']);
    // A list that does not start with the Manager still has it first; no catalog: only the Manager.
    expect(tellMembers({ ...CATALOG, tellTargets: [{ teamId: 1, team: 'Support', members: ['Ava', 'Manager'] }] }, blankTell())).toEqual(['Manager', 'Ava']);
    expect(tellMembers(null, blankTell())).toEqual(['Manager']);
  });

  it('reads a timeout in minutes, hours or days, at most 7d', () => {
    expect(timeoutHours('30m')).toBe(0.5);
    expect(timeoutHours('4h')).toBe(4);
    expect(timeoutHours('2d')).toBe(48);
    expect(timeoutHours('soon')).toBeNull();
    expect(timeoutProblem('')).toBeNull();
    expect(timeoutProblem('7d')).toBeNull();
    expect(timeoutProblem('8d')).toBe('A tell waits at most 7d.');
    expect(timeoutProblem('0h')).toBe('A timeout is more than 0.');
    expect(timeoutProblem('4 hours')).toBe('A timeout is a number and m, h or d, e.g. 4h.');
  });
});

// ---- the block and its node

describe('the Tell block on the flowchart', () => {
  const doc: ScriptDocument = {
    format: 'yawble-script/1', id: 'refund-triage', name: 'Refund triage', start: 'note',
    steps: [
      { id: 'note', type: 'tell', text: 'Look at {input.text}', next: 'ask' },
      { id: 'ask', type: 'tell', member: 'Ava', text: 'Is this a refund?', wait: true, next: 'other' },
      { id: 'other', type: 'tell', member: 'Manager', team: 'Billing', text: 'FYI', next: 'done' },
      { id: 'done', type: 'end' },
    ],
  };

  it('each tell node says whom it tells, the team when it is another, and when it waits', async () => {
    const w = mount(ScriptFlowchart, { props: { modelValue: pretty(doc), catalog: CATALOG, schemas: FIELDS } });
    await flushPromises();
    const node = (id: string) => w.get(`[data-test=flow-node][data-node="${id}"]`);
    expect(node('note').attributes('data-kind')).toBe('tell');
    expect(node('note').get('[data-test=flow-node-detail]').text()).toBe('tell Manager');
    expect(node('ask').get('[data-test=flow-node-detail]').text()).toBe('tell Ava (waits)');
    expect(node('other').get('[data-test=flow-node-detail]').text()).toBe('tell Manager of Billing');
    expect(node('ask').get('[data-test=flow-node-out]').text()).toBe('out TellResult');
  });

  it('the Do group\'s Tell block adds a tell to the Manager and opens its form', async () => {
    const w = mount(ScriptFlowchart, { props: { modelValue: pretty(doc), catalog: CATALOG, schemas: FIELDS, 'onUpdate:modelValue': (t: string) => w.setProps({ modelValue: t }) } as any });
    await flushPromises();
    const tell = w.get('[data-test=palette-group-do] [data-test=palette-tell]');
    expect(tell.text()).toBe('Tell');
    expect(tell.attributes('data-kind')).toBe('tell');
    await tell.trigger('click');
    await flushPromises();
    const added = (JSON.parse(w.props('modelValue') as string) as ScriptDocument).steps!.at(-1)!;
    expect(added).toEqual({ id: 'step5', type: 'tell', member: 'Manager', text: '', next: null });
    expect(w.get('[data-test=step-form]').attributes('data-type')).toBe('tell');
    expect(w.get('[data-test=flow-node][data-node="step5"] [data-test=flow-node-detail]').text()).toBe('tell Manager');
  });
});

// ---- the form

describe('the Tell form', () => {
  it('defaults: the own team, the Manager, Wait off, and no timeout or reply fields', () => {
    const { w } = form(blankTell());
    expect(options(w, '[data-test=form-tell-team]')).toEqual([['', 'Support (own team)'], ['Billing', 'Billing']]);
    expect(valueOf(w, '[data-test=form-tell-team]')).toBe('');
    expect(options(w, '[data-test=form-tell-member]')).toEqual([['Manager', 'Manager'], ['Ava', 'Ava'], ['Ben', 'Ben']]);
    expect(valueOf(w, '[data-test=form-tell-member]')).toBe('Manager');
    expect(w.find('[data-test=expr-field][data-name="tell-text"]').exists()).toBe(true);
    expect((w.get('[data-test=form-tell-wait]').element as HTMLInputElement).checked).toBe(false);
    expect(w.find('[data-test=form-tell-timeout]').exists()).toBe(false);
    expect(w.find('[data-test=form-tell-reply]').exists()).toBe(false);
    expect(w.find('[data-test=form-into]').exists()).toBe(true);
    expect(w.find('[data-test=form-next]').exists()).toBe(true);
    expect(w.get('[data-test=form-io-out]').text()).toBe('TellResult');
  });

  it('a step with no member shows the Manager; picking a member and the text write the step', async () => {
    const { w, state } = form({ id: 'ask', type: 'tell', text: '', next: null });
    expect(valueOf(w, '[data-test=form-tell-member]')).toBe('Manager');
    await changeTo(w, '[data-test=form-tell-member]', 'Ava');
    expect(state.step.member).toBe('Ava');
    await w.get('[data-test=expr-field][data-name="tell-text"] [data-test=expr-input]').setValue('Look at {input.text}');
    await flushPromises();
    expect(state.step.text).toBe('Look at {input.text}');
  });

  it('another team lists its own members; a member it does not have goes back to the Manager; the own team removes team', async () => {
    const { w, state } = form(blankTell({ member: 'Ava' }));
    await changeTo(w, '[data-test=form-tell-team]', 'Billing');
    expect(state.step.team).toBe('Billing');
    expect(state.step.member).toBe('Manager');
    expect(options(w, '[data-test=form-tell-member]').map(([v]) => v)).toEqual(['Manager', 'Cleo']);
    await changeTo(w, '[data-test=form-tell-member]', 'Cleo');
    await changeTo(w, '[data-test=form-tell-team]', '');
    expect(state.step).not.toHaveProperty('team');
    expect(state.step.member).toBe('Manager');
  });

  it('a member that is not an agent member of the team, and a team that is not one here, are shown as such', () => {
    const { w } = form(blankTell({ member: 'Poller', team: 'Elsewhere' }));
    expect(options(w, '[data-test=form-tell-team]')).toContainEqual(['Elsewhere', 'Elsewhere (not a team here)']);
    const { w: w2 } = form(blankTell({ member: 'Poller' }));
    expect(options(w2, '[data-test=form-tell-member]')[0]).toEqual(['Poller', 'Poller (not an agent member of the team)']);
    // The own team named in the step is the own team.
    const { w: w3 } = form(blankTell({ team: 'Support' }));
    expect(valueOf(w3, '[data-test=form-tell-team]')).toBe('');
  });

  it('Wait shows timeout and reply; turning it off removes them', async () => {
    const { w, state } = form(blankTell());
    await w.get('[data-test=form-tell-wait]').setValue(true);
    await flushPromises();
    expect(state.step.wait).toBe(true);
    expect((w.get('[data-test=form-tell-timeout]').element as HTMLInputElement).placeholder).toBe('24h (at most 7d)');
    expect(options(w, '[data-test=form-tell-reply]').map(([v]) => v)).toEqual(['', 'TriageResult', 'TellResult']);
    await changeTo(w, '[data-test=form-tell-timeout]', '4h');
    expect(state.step.timeout).toBe('4h');
    expect(w.find('[data-test=form-tell-timeout-problem]').exists()).toBe(false);
    await changeTo(w, '[data-test=form-tell-reply]', 'TriageResult');
    expect(state.step.reply).toBe('TriageResult');
    await changeTo(w, '[data-test=form-tell-timeout]', '9d');
    expect(w.get('[data-test=form-tell-timeout-problem]').text()).toBe('A tell waits at most 7d.');
    await changeTo(w, '[data-test=form-tell-timeout]', '');
    expect(state.step).not.toHaveProperty('timeout');

    await w.get('[data-test=form-tell-wait]').setValue(false);
    await flushPromises();
    expect(state.step).not.toHaveProperty('wait');
    expect(state.step).not.toHaveProperty('reply');
    expect(w.find('[data-test=form-tell-timeout]').exists()).toBe(false);
    expect(w.find('[data-test=form-tell-reply]').exists()).toBe(false);
  });

  it('saves into a new variable typed TellResult', async () => {
    const { w, state } = form(blankTell(), { variables: [{ name: 'count', type: 'int', scope: 'run' }] });
    await changeTo(w, '[data-test=form-into]', '__new');
    expect(valueOf(w, '[data-test=form-into-new-type]')).toBe('TellResult');
    await w.get('[data-test=form-into-new-name]').setValue('triage');
    await w.get('[data-test=form-into-new-add]').trigger('click');
    await flushPromises();
    expect(state.step.into).toBe('triage');
    expect(state.added).toEqual([{ name: 'triage', type: 'TellResult', scope: 'run' }]);
  });

  it('with telling other teams off, the other teams cannot be picked and the form says why', () => {
    const { w } = form(blankTell(), { catalog: { ...CATALOG, tellOtherTeams: false } });
    const billing = w.get('[data-test=form-tell-team] option[value="Billing"]');
    expect(billing.attributes('disabled')).toBeDefined();
    expect(w.get('[data-test=form-tell-no-other-teams]').text()).toContain('Settings > Scripts turns off telling other teams');
    expect(form(blankTell()).w.find('[data-test=form-tell-no-other-teams]').exists()).toBe(false);
  });

  it('opened without a team (no tell targets), team and member are typed', async () => {
    const { w, state } = form(blankTell(), { catalog: { integrations: [], functions: [] } });
    expect(w.find('[data-test=form-tell-team]').exists()).toBe(false);
    expect((w.get('[data-test=form-tell-member-text]').element as HTMLInputElement).value).toBe('Manager');
    await changeTo(w, '[data-test=form-tell-member-text]', 'Ava');
    expect(state.step.member).toBe('Ava');
    await changeTo(w, '[data-test=form-tell-team-text]', 'Billing');
    expect(state.step.team).toBe('Billing');
    await changeTo(w, '[data-test=form-tell-team-text]', '');
    expect(state.step).not.toHaveProperty('team');
  });

  it('read-only: every field shows and none changes', async () => {
    const { w, state } = form(blankTell({ wait: true, timeout: '4h' }), { readonly: true });
    expect(w.get('[data-test=form-tell-member]').attributes('disabled')).toBeDefined();
    expect(w.get('[data-test=form-tell-wait]').attributes('disabled')).toBeDefined();
    expect(valueOf(w, '[data-test=form-tell-timeout]')).toBe('4h');
    await changeTo(w, '[data-test=form-tell-member]', 'Ava');
    expect(state.changes).toEqual([]);
  });
});

// ---- a waiting run

describe('a waiting run', () => {
  const waitingDetail = (): RunDetail => ({
    ...run(52, 'waiting', { waitWorkflowId: 812 }) as any,
    steps: [{ seq: 1, stepId: 'ask', type: 'tell', input: { member: 'Ava', text: 'Is this a refund?' }, output: null, status: 'waiting', startedAt: at, endedAt: null }],
    log: [{ at, line: 'told Ava of Support: workflow 812' }],
  });

  it('the run view says waiting and links the workflow it waits on', async () => {
    const w = mount(ScriptRunView, { props: { run: waitingDetail() } });
    expect(w.get('[data-test=run-status]').text()).toBe('waiting');
    expect(w.get('[data-test=run-status]').attributes('data-status')).toBe('waiting');
    const link = w.get('[data-test=run-wait-workflow]');
    expect(link.text()).toBe('workflow #812');
    await link.trigger('click');
    expect(workflowsRequest.value).toMatchObject({ teamId: null, workflow: 812 });
    // A run that does not wait shows no link; a waiting run without a workflow id still says it waits.
    expect(mount(ScriptRunView, { props: { run: { ...waitingDetail(), status: 'succeeded' } } }).find('[data-test=run-waiting]').exists()).toBe(false);
    const older = mount(ScriptRunView, { props: { run: { ...waitingDetail(), waitWorkflowId: undefined } } });
    expect(older.get('[data-test=run-waiting]').text()).toBe('waiting on a tell');
    expect(older.find('[data-test=run-wait-workflow]').exists()).toBe(false);
  });

  it('the Runs tab shows it with the link; the link opens the workflow, not the run', async () => {
    const f = fakeApi({ 'scripts.runs': () => [run(52, 'waiting', { waitWorkflowId: 812 }), run(51, 'succeeded')], 'scripts.run': () => waitingDetail() });
    const w = mount(RunsTab, { props: { api: f.api } });
    await flushPromises();
    const rows = w.findAll('[data-test=run-row]');
    expect(rows[0]!.get('[data-test=run-wait-workflow]').text()).toBe('workflow #812');
    expect(rows[1]!.find('[data-test=run-waiting]').exists()).toBe(false);
    await rows[0]!.get('[data-test=run-wait-workflow]').trigger('click');
    await flushPromises();
    expect(workflowsRequest.value).toMatchObject({ workflow: 812 });
    expect(f.of('scripts.run')).toHaveLength(0);
    await rows[0]!.trigger('click');
    await flushPromises();
    expect(f.of('scripts.run')).toHaveLength(1);
    expect(w.get('[data-test=run-wait-workflow]').text()).toBe('workflow #812');
  });

  const card = {
    member: { id: 9, teamId: 1, team: 'Support', name: 'Triage', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
    kind: 'script', state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
    runStartedAt: null, lastRunState: null, lastRunReason: null,
  } as unknown as AgentCard;
  const member = (extra: Record<string, unknown> = {}) => ({
    memberId: 9, teamId: 1, team: 'Support', name: 'Triage', scriptId: 5, script: 'refund-triage', version: null, runsVersion: 2,
    settings: {}, bindings: {}, timeoutMinutes: 10, consecutiveFailures: 0, running: false,
    lastRun: run(52, 'waiting', { waitWorkflowId: 812 }), nextTriggerAt: null, ...extra,
  });
  const paneApi = (over: Record<string, (p: any) => unknown> = {}) => fakeApi({
    'scripts.memberGet': () => member(),
    'scripts.runs': () => [run(52, 'waiting', { waitWorkflowId: 812 }), run(51, 'succeeded')],
    'scripts.memberState': () => ({ memberId: 9, variables: {}, consecutiveFailures: 0, updatedAt: null }),
    'scripts.memberFiles': () => ({ memberId: 9, files: [] }),
    'triggers.list': () => [],
    ...over,
  });

  it('the member card shows the last run waiting with the link, and each waiting run in its list', async () => {
    const w = mount(ScriptMemberPane, { props: { card, api: paneApi().api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.get('[data-test=script-member-last-status]').text()).toBe('Waiting');
    expect(w.get('[data-test=script-member-last-run] [data-test=run-wait-workflow]').text()).toBe('workflow #812');
    const runs = w.findAll('[data-test=script-member-run]');
    expect(runs[0]!.text()).toContain('Waiting');
    expect(runs[0]!.get('[data-test=run-wait-workflow]').attributes('data-workflow')).toBe('812');
    expect(runs[1]!.find('[data-test=run-waiting]').exists()).toBe(false);
    await runs[0]!.get('[data-test=run-wait-workflow]').trigger('click');
    expect(workflowsRequest.value).toMatchObject({ workflow: 812 });
  });

  it('the member card sets its tells per run (default 5, 0 to 50)', async () => {
    const f = paneApi({ 'scripts.memberUpdate': (p) => member({ maxTells: p.maxTells }) });
    const w = mount(ScriptMemberPane, { props: { card, api: f.api, pollMs: 60_000 } });
    await flushPromises();
    const input = w.get('[data-test=script-member-max-tells]');
    expect((input.element as HTMLInputElement).value).toBe('5');
    await input.setValue('12');
    await input.trigger('change');
    await flushPromises();
    expect(f.of('scripts.memberUpdate').map((c) => c.params)).toEqual([{ memberId: 9, maxTells: 12 }]);
    expect((input.element as HTMLInputElement).value).toBe('12');
    await input.setValue('51');
    await input.trigger('change');
    await flushPromises();
    expect(f.of('scripts.memberUpdate')).toHaveLength(1);
    expect(w.get('[data-test=script-member-pane-error]').text()).toBe('Tells per run is a whole number from 0 to 50.');
    expect((input.element as HTMLInputElement).value).toBe('12');
  });

  it('a Visitor sees the tells per run and cannot change it', async () => {
    applyRemoteAccess('read');
    const w = mount(ScriptMemberPane, { props: { card, api: paneApi({ 'scripts.memberGet': () => member({ maxTells: 3 }) }).api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=script-member-max-tells]').exists()).toBe(false);
    expect(w.get('[data-test=script-member-max-tells-text]').text()).toBe('3');
  });
});

// ---- Settings > Scripts

describe('Settings > Scripts', () => {
  it('shows "Scripts may tell other teams" and sets it', async () => {
    let on = true;
    const f = fakeApi({ 'scripts.appSettings': () => ({ tellOtherTeams: on }), 'scripts.setAppSettings': (p) => { on = p.tellOtherTeams; return { tellOtherTeams: on }; } });
    const w = mount(ScriptsSettings, { props: { api: f.api } });
    await flushPromises();
    const box = w.get('[data-test=scripts-tell-other-teams]');
    expect((box.element as HTMLInputElement).checked).toBe(true);
    expect(w.text()).toContain('Scripts may tell other teams');
    await box.setValue(false);
    await flushPromises();
    expect(f.of('scripts.setAppSettings').map((c) => c.params)).toEqual([{ tellOtherTeams: false }]);
    expect((box.element as HTMLInputElement).checked).toBe(false);
  });

  it('a refused change says why and the box goes back', async () => {
    const f = fakeApi({ 'scripts.appSettings': () => ({ tellOtherTeams: true }), 'scripts.setAppSettings': () => { throw new Error('not allowed here'); } });
    const w = mount(ScriptsSettings, { props: { api: f.api } });
    await flushPromises();
    const box = w.get('[data-test=scripts-tell-other-teams]');
    await box.setValue(false);
    await flushPromises();
    expect(w.get('[data-test=settings-scripts-error]').text()).toContain('not allowed here');
    expect((box.element as HTMLInputElement).checked).toBe(true);
  });

  it('a Visitor cannot change it', async () => {
    applyRemoteAccess('read');
    const w = mount(ScriptsSettings, { props: { api: fakeApi({ 'scripts.appSettings': () => ({ tellOtherTeams: true }) }).api } });
    await flushPromises();
    expect(w.get('[data-test=scripts-tell-other-teams]').attributes('disabled')).toBeDefined();
  });
});

// ---- the test run

describe('the test run of a script that tells', () => {
  const teams: TeamSummary[] = [{ id: 1, name: 'Support', repoPath: null, members: 2, running: 0, queued: 0 }];
  const doc: ScriptDocument = {
    format: 'yawble-script/1', id: 'refund-triage', trigger: { accepts: ['once'] }, start: 'note',
    steps: [
      { id: 'note', type: 'tell', text: 'FYI', next: 'ask' },
      { id: 'ask', type: 'tell', member: 'Ava', text: 'Refund?', wait: true, reply: 'TriageResult', into: 'triage', next: 'sum' },
      { id: 'sum', type: 'tell', text: 'Sum it up', wait: true, next: null },
    ],
  };
  const testApi = () => fakeApi({
    'integrations.apiList': () => [], 'integrations.emailList': () => [],
    'scripts.testRun': () => ({ runId: 7, status: 'succeeded', failedStepId: null, reason: null, outputs: null, steps: [], log: ['would tell Ava of Support: Refund?'] }),
  });

  it('has a made-up reply field for each tell that waits, and sends them as tellReplies (JSON or text)', async () => {
    const f = testApi();
    const w = mount(ScriptTestRun, { props: { api: f.api, script: { id: 5, key: 'refund-triage' }, doc, teams } });
    await flushPromises();
    expect(w.findAll('[data-test=test-tell-reply]')).toHaveLength(2);
    expect(w.find('[data-test=test-tell-reply-note]').exists()).toBe(false);
    expect(w.get('[data-test=test-tell-reply-ask]').text()).toContain('(JSON of TriageResult)');
    expect(w.get('[data-test=test-tell-reply-sum]').text()).toContain('(text)');
    await w.get('[data-test=test-tell-reply-ask] textarea').setValue('{ "refund": true, "reason": "double charge" }');
    await w.get('[data-test=test-tell-reply-sum] textarea').setValue('All done.');
    await w.get('form').trigger('submit');
    await flushPromises();
    expect(f.of('scripts.testRun')[0]!.params.tellReplies).toEqual({ ask: { refund: true, reason: 'double charge' }, sum: 'All done.' });
    expect(f.of('scripts.testRun')[0]!.params.dryRun).toBe(true);
    expect(w.get('[data-test=run-log]').text()).toContain('would tell Ava of Support: Refund?');
  });

  it('empty replies send no tellReplies; a script without a waiting tell has no reply fields', async () => {
    const f = testApi();
    const w = mount(ScriptTestRun, { props: { api: f.api, script: { id: 5, key: 'refund-triage' }, doc, teams } });
    await flushPromises();
    await w.get('form').trigger('submit');
    await flushPromises();
    expect(f.of('scripts.testRun')[0]!.params).not.toHaveProperty('tellReplies');
    const plain = mount(ScriptTestRun, { props: { api: testApi().api, script: { id: 5, key: 'refund-triage' }, doc: { ...doc, steps: [doc.steps![0]!] }, teams } });
    await flushPromises();
    expect(plain.find('[data-test=test-tell-replies]').exists()).toBe(false);
  });
});
