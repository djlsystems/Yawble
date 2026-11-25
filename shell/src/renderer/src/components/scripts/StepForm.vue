<script setup lang="ts">
import { computed, ref } from 'vue';
import {
  actionOf, CONDITION_OPS, expressionSuggestions, integrationOf, ioInText, ioOutText, isUnaryOp, NATIVE_TYPES, ownTellTeam, stepIO,
  TELL_DEFAULT_MEMBER, TELL_DEFAULT_TIMEOUT, TELL_TIMEOUTS, tellMembers, tellTeamOf, timeoutProblem,
  type DesignContext, type MemberIntegration,
} from '../../scripts/catalog';
import type { ScriptStep, ScriptVariable } from '../../scripts/types';
import { FILE_OPERATIONS, withFileOperation } from '../../scripts/flowchart';
import ExpressionField from './ExpressionField.vue';

// The Design form of one step, by its type. Every change emits the whole step; a new variable made for "save result
// into" comes with it, so the two land in the document as one edit. Read-only: every field shows, none changes.
const props = defineProps<{
  step: ScriptStep;
  ctx: DesignContext;
  readonly: boolean;
  /** Opened from a member: the integrations it uses, overrides marked. */
  integrations?: MemberIntegration[] | null;
}>();
const emit = defineEmits<{ change: [step: ScriptStep, variable?: ScriptVariable] }>();

const copy = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;
const str = (v: unknown) => (typeof v === 'string' ? v : '');
const obj = (v: unknown): Record<string, unknown> => (v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : {});

function change(edit: (s: ScriptStep) => void, variable?: ScriptVariable) {
  if (props.readonly) return;
  const s = copy(props.step);
  edit(s);
  emit('change', s, variable);
}

const suggestions = computed(() => expressionSuggestions(props.ctx, props.step.id));
const io = computed(() => stepIO(props.ctx, props.step));
const targets = computed(() => (props.ctx.doc.steps ?? []).map((s) => s.id).filter((id) => id !== props.step.id));
const variables = computed(() => (props.ctx.doc.variables ?? []).filter((v) => v && v.name));
const schemaNames = computed(() => Object.keys(props.ctx.schemas ?? {}));
const types = computed(() => [...NATIVE_TYPES, ...schemaNames.value]);
const isFile = computed(() => props.step.type.startsWith('file.'));
const fileWrites = computed(() => props.step.type === 'file.writeAllText' || props.step.type === 'file.appendLine');
const fileReads = computed(() => ['file.readAllText', 'file.readLines', 'file.exists'].includes(props.step.type));
/** The File block's operation: the step becomes that file step type (see withFileOperation). */
function pickFileOp(e: Event) {
  if (props.readonly) return;
  emit('change', withFileOperation(copy(props.step), (e.target as HTMLSelectElement).value));
}
const hasNext = computed(() => !['if', 'switch', 'forEach', 'while', 'end'].includes(props.step.type));
/** The steps whose result can be saved into a variable from this form (a new one too). */
const hasInto = computed(() => ['integration', 'tell', 'code'].includes(props.step.type));

const setRef = (key: string) => (e: Event) => change((s) => { const v = (e.target as HTMLSelectElement).value; s[key] = v === '' ? null : v; });

function rename(e: Event) {
  const id = (e.target as HTMLInputElement).value.trim();
  if (!id || id === props.step.id) return;
  change((s) => { s.id = id; });
}

// ---- integration
const integrations = computed(() => props.ctx.catalog?.integrations ?? []);
const use = computed(() => str(props.step.use));
const integration = computed(() => integrationOf(props.ctx.catalog, use.value) ?? integrationOf(props.ctx.catalog, props.ctx.bindings?.[use.value] ?? ''));
const useKnown = computed(() => integrations.value.some((i) => i.name === use.value));
const override = computed(() => props.integrations?.find((i) => i.name === use.value && i.overridden) ?? null);
const action = computed(() => actionOf(props.ctx, props.step));
const args = computed(() => obj(props.step.args));
const extraArgs = computed(() => Object.keys(args.value).filter((k) => !action.value?.params.some((p) => p.name === k)));

function pickUse(e: Event) {
  const name = (e.target as HTMLSelectElement).value;
  change((s) => {
    s.use = name;
    const next = integrationOf(props.ctx.catalog, name);
    if (next && !next.actions.some((a) => a.name === s.action)) { s.action = ''; s.args = {}; }
  });
}
function pickAction(e: Event) {
  const name = (e.target as HTMLSelectElement).value;
  change((s) => {
    s.action = name;
    const a = integration.value?.actions.find((x) => x.name === name);
    if (a) {
      const keep = obj(s.args);
      s.args = Object.fromEntries(Object.entries(keep).filter(([k]) => a.params.some((p) => p.name === k)));
    }
  });
}
function setArg(name: string, v: unknown, required: boolean) {
  change((s) => {
    const a = obj(s.args);
    if ((v === '' || v === undefined) && !required) delete a[name];
    else a[name] = v;
    s.args = a;
  });
}

// "save result into": a variable, none, or a new one.
const NEW = '__new';
const intoChoice = ref<string | null>(null);
const newName = ref('');
const newType = ref('');
const intoValue = computed(() => intoChoice.value ?? str(props.step.into));
function pickInto(e: Event) {
  const v = (e.target as HTMLSelectElement).value;
  if (v === NEW) {
    intoChoice.value = NEW;
    newName.value = '';
    newType.value = io.value.out?.type && io.value.out.type !== 'any' ? io.value.out.type : 'object';
    return;
  }
  intoChoice.value = null;
  change((s) => { if (v === '') delete s.into; else s.into = v; });
}
const newProblem = computed(() => {
  const n = newName.value.trim();
  if (!n) return 'Name the variable.';
  if (!/^[A-Za-z_]\w*$/.test(n)) return 'A name is letters, digits and _.';
  if (variables.value.some((v) => v.name === n)) return `${n} is already a variable.`;
  if (!newType.value.trim()) return 'Give it a type.';
  return null;
});
function addInto() {
  if (newProblem.value) return;
  const name = newName.value.trim();
  intoChoice.value = null;
  change((s) => { s.into = name; }, { name, type: newType.value.trim(), scope: 'run' });
}

// ---- tell
// The team ('' = the script member's own team, so the step names none), the member (the team's agent members from the
// catalog, the Manager first and by default), the text, and Wait; timeout and reply only mean something while it waits,
// so turning Wait off removes them.
const tellTargets = computed(() => props.ctx.catalog?.tellTargets ?? []);
const ownTeam = computed(() => ownTellTeam(props.ctx.catalog));
const otherTeams = computed(() => tellTargets.value.slice(1));
const otherTeamsAllowed = computed(() => props.ctx.catalog?.tellOtherTeams !== false);
/** The team select's value: '' for none named (or the own team named). */
const tellTeam = computed(() => {
  const t = str(props.step.team).trim();
  return t && ownTeam.value && t.toLowerCase() === ownTeam.value.team.toLowerCase() ? '' : t;
});
const tellTeamKnown = computed(() => !tellTeam.value || otherTeams.value.some((t) => t.team.toLowerCase() === tellTeam.value.toLowerCase()));
const tellMember = computed(() => str(props.step.member).trim() || TELL_DEFAULT_MEMBER);
const memberChoices = computed(() => tellMembers(props.ctx.catalog, props.step));
/** The catalog lists the step's team's agent members (it may not, opened from the library without a team). */
const membersListed = computed(() => tellTeamOf(props.ctx.catalog, props.step) !== null);
const waits = computed(() => props.step.wait === true);
const timeoutText = computed(() => str(props.step.timeout));
const timeoutError = computed(() => timeoutProblem(timeoutText.value));
const replyName = computed(() => str(props.step.reply));

function pickTeam(e: Event) {
  const team = (e.target as HTMLSelectElement).value;
  change((s) => {
    if (team === '') delete s.team; else s.team = team;
    // A member the new team does not have goes back to the Manager.
    const listed = tellTeamOf(props.ctx.catalog, s);
    const member = str(s.member).trim() || TELL_DEFAULT_MEMBER;
    if (listed && member !== TELL_DEFAULT_MEMBER && !listed.members.includes(member)) s.member = TELL_DEFAULT_MEMBER;
  });
}
function pickMember(e: Event) {
  const member = (e.target as HTMLSelectElement).value;
  change((s) => { s.member = member; });
}
function setWait(e: Event) {
  const on = (e.target as HTMLInputElement).checked;
  change((s) => {
    if (on) { s.wait = true; return; }
    delete s.wait; delete s.timeout; delete s.reply;
  });
}
function setTimeoutText(e: Event) {
  const t = (e.target as HTMLInputElement).value.trim();
  change((s) => { if (t) s.timeout = t; else delete s.timeout; });
}
function pickReply(e: Event) {
  const name = (e.target as HTMLSelectElement).value;
  change((s) => { if (name) s.reply = name; else delete s.reply; });
}

// ---- code
// The inputs (a name and an expression each; in the code they are inputs.<name>), the function body, the type it
// returns (none given: any) and save into.
const codeInputs = computed(() => Object.entries(obj(props.step.inputs)));
function setInputs(list: [string, unknown][]) {
  change((s) => { s.inputs = Object.fromEntries(list); });
}
const inputNameOk = (name: string, i: number) => /^[A-Za-z_$][\w$]*$/.test(name) && !codeInputs.value.some(([k], j) => j !== i && k === name);
function renameInput(i: number, e: Event) {
  const name = (e.target as HTMLInputElement).value.trim();
  if (!inputNameOk(name, i)) { (e.target as HTMLInputElement).value = codeInputs.value[i]?.[0] ?? ''; return; }
  setInputs(codeInputs.value.map(([k, v], j) => [j === i ? name : k, v]));
}
function addInput() {
  let n = codeInputs.value.length + 1;
  while (codeInputs.value.some(([k]) => k === `input${n}`)) n++;
  setInputs([...codeInputs.value, [`input${n}`, '']]);
}
const removeInput = (i: number) => setInputs(codeInputs.value.filter((_, j) => j !== i));
const INDENT = '  ';
/** Tab in the code area indents at the cursor (or the selection) instead of leaving the field; Shift+Tab still leaves it. */
function codeKey(e: KeyboardEvent) {
  if (e.key !== 'Tab' || e.shiftKey || e.altKey || e.ctrlKey || e.metaKey || props.readonly) return;
  e.preventDefault();
  const area = e.target as HTMLTextAreaElement;
  const { selectionStart: a, selectionEnd: b, value } = area;
  area.value = value.slice(0, a) + INDENT + value.slice(b);
  area.selectionStart = area.selectionEnd = a + INDENT.length;
  change((s) => { s.code = area.value; });
}
function setReturns(e: Event) {
  const t = (e.target as HTMLInputElement).value.trim();
  change((s) => { if (t) s.returns = t; else delete s.returns; });
}

// ---- while
/** The default and the largest number of passes a loop may make. */
const LOOP_MAX_DEFAULT = 100;
const LOOP_MAX_LIMIT = 10000;
const maxText = ref<string | null>(null);
const maxShown = computed(() => maxText.value ?? (typeof props.step.max === 'number' ? String(props.step.max) : ''));
const maxProblem = computed(() => {
  const t = maxShown.value.trim();
  if (!t) return null;
  const n = Number(t);
  return Number.isInteger(n) && n >= 1 && n <= LOOP_MAX_LIMIT ? null : `Max is a whole number from 1 to ${LOOP_MAX_LIMIT}.`;
});
function setMax(e: Event) {
  maxText.value = (e.target as HTMLInputElement).value;
  if (maxProblem.value) return;
  const t = maxText.value.trim();
  maxText.value = null;
  change((s) => { if (t) s.max = Number(t); else delete s.max; });
}

// ---- if (and the condition of while)
const when = computed(() => obj(props.step.when));
/**
 * A side of the comparison as its field takes it: the blank If's '' is no value yet, so 0 typed there is the number 0 (as on a
 * side that held a number), not the text "0" the checker refuses against an int ("'gt' compares int with string").
 */
const side = (v: unknown) => (v === '' ? undefined : v);
const simpleWhen = computed(() => props.step.when === undefined || props.step.when === null
  || (typeof props.step.when === 'object' && !Array.isArray(props.step.when) && !('all' in when.value) && !('any' in when.value) && !('not' in when.value)));
function setWhen(key: 'left' | 'op' | 'right', v: unknown) {
  change((s) => {
    const w = { left: '', op: 'eq', right: '', ...obj(s.when) } as Record<string, unknown>;
    w[key] = v;
    if (key === 'op' && isUnaryOp(String(v))) delete w.right;
    if (key === 'op' && !isUnaryOp(String(v)) && !('right' in w)) w.right = '';
    s.when = w;
  });
}

// ---- switch
const cases = computed(() => Object.entries(obj(props.step.cases)).map(([value, target]) => ({ value, target: str(target) })));
function setCases(list: { value: string; target: string }[]) {
  change((s) => { s.cases = Object.fromEntries(list.map((c) => [c.value, c.target === '' ? null : c.target])); });
}
function renameCase(i: number, e: Event) {
  const value = (e.target as HTMLInputElement).value;
  if (!value.trim() || cases.value.some((c, j) => j !== i && c.value === value)) return;
  setCases(cases.value.map((c, j) => (j === i ? { ...c, value } : c)));
}
function caseTarget(i: number, e: Event) {
  setCases(cases.value.map((c, j) => (j === i ? { ...c, target: (e.target as HTMLSelectElement).value } : c)));
}
function addCase() {
  let n = cases.value.length + 1;
  while (cases.value.some((c) => c.value === `value${n}`)) n++;
  setCases([...cases.value, { value: `value${n}`, target: '' }]);
}
const removeCase = (i: number) => setCases(cases.value.filter((_, j) => j !== i));

// ---- end
const outputs = computed(() => Object.entries(obj(props.step.outputs)));
const declaredOutputs = computed(() => (Array.isArray(props.ctx.doc.outputs) ? (props.ctx.doc.outputs as { name?: unknown; type?: unknown }[]) : [])
  .filter((o) => typeof o?.name === 'string').map((o) => ({ name: String(o.name), type: typeof o.type === 'string' ? o.type : '' })));
function setOutputs(list: [string, unknown][]) {
  change((s) => {
    if (list.length === 0) delete s.outputs;
    else s.outputs = Object.fromEntries(list);
  });
}
function renameOutput(i: number, e: Event) {
  const name = (e.target as HTMLInputElement).value.trim();
  if (!name || outputs.value.some(([k], j) => j !== i && k === name)) return;
  setOutputs(outputs.value.map(([k, v], j) => [j === i ? name : k, v]));
}
function addOutput() {
  const free = declaredOutputs.value.find((o) => !outputs.value.some(([k]) => k === o.name))?.name;
  let name = free ?? `output${outputs.value.length + 1}`;
  let n = outputs.value.length + 1;
  while (outputs.value.some(([k]) => k === name)) name = `output${++n}`;
  setOutputs([...outputs.value, [name, '']]);
}
</script>

<template>
  <div class="form" data-test="step-form" :data-type="step.type">
    <label class="field">Step id
      <input :value="step.id" :readonly="readonly" data-test="form-id" spellcheck="false" @change="rename" />
    </label>

    <!-- integration -->
    <template v-if="step.type === 'integration'">
      <label class="field">Integration
        <select :value="use" :disabled="readonly" data-test="form-use" @change="pickUse">
          <option value="" disabled>Choose an integration</option>
          <option v-if="use && !useKnown" :value="use">{{ use }}{{ integration ? ` (bound to ${integration.name})` : ' (not an integration)' }}</option>
          <option v-for="i in integrations" :key="i.name" :value="i.name">{{ i.name }} ({{ i.kind === 'email' ? 'Email' : 'API' }}){{ i.enabledForTeam === false ? ', not on for this team' : '' }}</option>
        </select>
      </label>
      <p v-if="override" class="override small" data-test="form-override">{{ override.name }} → {{ override.uses }}</p>
      <label class="field">Action
        <select :value="str(step.action)" :disabled="readonly || !integration" data-test="form-action" @change="pickAction">
          <option value="" disabled>Choose an action</option>
          <option v-if="str(step.action) && !action" :value="str(step.action)">{{ str(step.action) }} (unknown)</option>
          <option v-for="a in integration?.actions ?? []" :key="a.name" :value="a.name">{{ a.name }}{{ a.ask ? ' (asks first)' : '' }}</option>
        </select>
      </label>
      <p v-if="action?.description" class="muted small">{{ action.description }}</p>
      <fieldset v-if="action || extraArgs.length" class="group" data-test="form-args">
        <legend>Inputs</legend>
        <ExpressionField v-for="p in action?.params ?? []" :key="p.name" :name="`arg-${p.name}`" :label="p.name" :type="p.type" :required="p.required"
          :model-value="args[p.name]" :suggestions="suggestions" :readonly="readonly" :title="p.description"
          @update:model-value="(v) => setArg(p.name, v, p.required)" />
        <div v-for="k in extraArgs" :key="k" class="extra">
          <ExpressionField :name="`arg-${k}`" :label="k" :model-value="args[k]" :suggestions="suggestions" :readonly="readonly"
            @update:model-value="(v) => setArg(k, v, true)" />
          <span class="warn small">{{ action ? 'not an input of this action' : '' }}</span>
          <button v-if="!readonly && action" type="button" class="mini" data-test="form-arg-remove" @click="setArg(k, undefined, false)">Remove</button>
        </div>
      </fieldset>
    </template>

    <!-- tell -->
    <template v-else-if="step.type === 'tell'">
      <!-- Opened without a team (the library), the catalog lists no teams or members: both are typed. -->
      <template v-if="!tellTargets.length">
        <label class="field">Team
          <input :value="tellTeam" :readonly="readonly" placeholder="the member's own team" spellcheck="false" data-test="form-tell-team-text"
            @change="(e) => change((s) => { const v = (e.target as HTMLInputElement).value.trim(); if (v) s.team = v; else delete s.team; })" />
        </label>
        <label class="field">Member
          <input :value="tellMember" :readonly="readonly" list="step-form-tell-members" spellcheck="false" data-test="form-tell-member-text"
            @change="(e) => change((s) => { s.member = (e.target as HTMLInputElement).value.trim() || TELL_DEFAULT_MEMBER; })" />
        </label>
        <datalist id="step-form-tell-members"><option :value="TELL_DEFAULT_MEMBER" /></datalist>
      </template>
      <label v-if="tellTargets.length" class="field">Team
        <select :value="tellTeam" :disabled="readonly" data-test="form-tell-team" @change="pickTeam">
          <option value="">{{ ownTeam ? `${ownTeam.team} (own team)` : 'The member\'s own team' }}</option>
          <option v-if="tellTeam && !tellTeamKnown" :value="tellTeam">{{ tellTeam }} (not a team here)</option>
          <option v-for="t in otherTeams" :key="t.teamId" :value="t.team" :disabled="!otherTeamsAllowed">{{ t.team }}</option>
        </select>
      </label>
      <p v-if="!otherTeamsAllowed" class="muted small" data-test="form-tell-no-other-teams">Settings &gt; Scripts turns off telling other teams: a tell to another team fails.</p>
      <label v-if="tellTargets.length" class="field">Member
        <select :value="tellMember" :disabled="readonly" data-test="form-tell-member" @change="pickMember">
          <option v-if="!memberChoices.includes(tellMember)" :value="tellMember">{{ tellMember }}{{ membersListed ? ' (not an agent member of the team)' : '' }}</option>
          <option v-for="m in memberChoices" :key="m" :value="m">{{ m }}</option>
        </select>
      </label>
      <p class="muted small">Telling the Manager starts a workflow it plans; telling another agent member gives that member one card.</p>
      <ExpressionField name="tell-text" label="Text" type="string" required :model-value="step.text" :suggestions="suggestions" :readonly="readonly"
        @update:model-value="(v) => change((s) => { s.text = v; })" />
      <label class="check">
        <input type="checkbox" :checked="waits" :disabled="readonly" data-test="form-tell-wait" @change="setWait" />
        Wait for the outcome (the run waits until the workflow is done, fails or is blocked)
      </label>
      <template v-if="waits">
        <label class="field">Timeout
          <input :value="timeoutText" :readonly="readonly" list="step-form-timeouts" :placeholder="`${TELL_DEFAULT_TIMEOUT} (at most 7d)`" spellcheck="false"
            data-test="form-tell-timeout" @change="setTimeoutText" />
        </label>
        <p v-if="timeoutError" class="warn small" data-test="form-tell-timeout-problem">{{ timeoutError }}</p>
        <label class="field">Reply schema
          <select :value="replyName" :disabled="readonly" data-test="form-tell-reply" @change="pickReply">
            <option value="">(none: just the closing summary)</option>
            <option v-if="replyName && !schemaNames.includes(replyName)" :value="replyName">{{ replyName }} (not a schema)</option>
            <option v-for="n in schemaNames" :key="n" :value="n">{{ n }}</option>
          </select>
        </label>
      </template>
    </template>

    <!-- code -->
    <template v-else-if="step.type === 'code'">
      <fieldset class="group" data-test="form-code-inputs">
        <legend>Inputs</legend>
        <p v-if="!codeInputs.length" class="muted small">No inputs: the code gets an empty inputs.</p>
        <div v-for="([k, v], i) in codeInputs" :key="i" class="output" data-test="form-code-input" :data-name="k">
          <input :value="k" :readonly="readonly" spellcheck="false" aria-label="Input name" data-test="form-code-input-name" @change="renameInput(i, $event)" />
          <ExpressionField :name="`input-${k}`" label="Value" :model-value="v" :suggestions="suggestions" :readonly="readonly"
            @update:model-value="(x) => setInputs(codeInputs.map(([a, b], j) => [a, j === i ? x : b]))" />
          <button v-if="!readonly" type="button" class="mini" data-test="form-code-input-remove" @click="removeInput(i)">Remove</button>
        </div>
        <button v-if="!readonly" type="button" class="mini" data-test="form-code-input-add" @click="addInput">Add an input</button>
      </fieldset>
      <label class="field">Code (JavaScript: the body of a function; it returns the result)
        <textarea class="code" :value="str(step.code)" :readonly="readonly" spellcheck="false" rows="8" data-test="form-code"
          @keydown="codeKey" @input="change((s) => { s.code = ($event.target as HTMLTextAreaElement).value; })" />
      </label>
      <p class="muted small">The code has inputs.&lt;name&gt; and log(text), nothing else. It may run 2 seconds and use 64 MB.</p>
      <label class="field">Returns
        <input :value="str(step.returns)" :readonly="readonly" list="step-form-types" placeholder="any" spellcheck="false" data-test="form-code-returns" @change="setReturns" />
      </label>
    </template>

    <!-- if, and while's condition -->
    <template v-else-if="step.type === 'if' || step.type === 'while'">
      <template v-if="simpleWhen">
        <ExpressionField name="when-left" label="Left value" :model-value="side(when.left)" :suggestions="suggestions" :readonly="readonly" @update:model-value="(v) => setWhen('left', v)" />
        <label class="field">Operator
          <select :value="String(when.op ?? 'eq')" :disabled="readonly" data-test="form-when-op" @change="(e) => setWhen('op', (e.target as HTMLSelectElement).value)">
            <option v-for="op in CONDITION_OPS" :key="op" :value="op">{{ op }}</option>
          </select>
        </label>
        <ExpressionField v-if="!isUnaryOp(String(when.op ?? 'eq'))" name="when-right" label="Right value" :model-value="side(when.right)" :suggestions="suggestions" :readonly="readonly"
          @update:model-value="(v) => setWhen('right', v)" />
      </template>
      <p v-else class="muted small" data-test="form-when-combined">This condition combines others (all, any or not): edit it on the JSON tab.</p>
      <template v-if="step.type === 'while'">
        <p class="muted small">Checked before each pass: while it is true the body runs again; when it is false the run goes to Then.</p>
        <label class="field">Max passes
          <input type="number" min="1" :max="LOOP_MAX_LIMIT" step="1" :value="maxShown" :readonly="readonly" :placeholder="`${LOOP_MAX_DEFAULT}`" data-test="form-while-max" @change="setMax" />
        </label>
        <p v-if="maxProblem" class="warn small" data-test="form-while-max-problem">{{ maxProblem }}</p>
        <p v-else class="muted small">Empty: {{ LOOP_MAX_DEFAULT }}. At most {{ LOOP_MAX_LIMIT }}; a loop that passes it fails the run.</p>
        <label class="field">Body (each pass)
          <select :value="str(step.body)" :disabled="readonly" data-test="form-body" @change="setRef('body')($event)">
            <option value="">(none)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
          </select>
        </label>
        <label class="field">Then (when done)
          <select :value="str(step.then)" :disabled="readonly" data-test="form-then" @change="setRef('then')($event)">
            <option value="">(end)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
          </select>
        </label>
      </template>
      <template v-else>
      <label class="field">Then (yes)
        <select :value="str(step.then)" :disabled="readonly" data-test="form-then" @change="setRef('then')($event)">
          <option value="">(end)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
        </select>
      </label>
      <label class="field">Else (no)
        <select :value="str(step.else)" :disabled="readonly" data-test="form-else" @change="setRef('else')($event)">
          <option value="">(end)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
        </select>
      </label>
      </template>
    </template>

    <!-- switch -->
    <template v-else-if="step.type === 'switch'">
      <ExpressionField name="on" label="Value" :model-value="step.on" :suggestions="suggestions" :readonly="readonly" @update:model-value="(v) => change((s) => { s.on = v; })" />
      <fieldset class="group" data-test="form-cases">
        <legend>Cases</legend>
        <div v-for="(c, i) in cases" :key="i" class="row case" data-test="form-case" :data-value="c.value">
          <input :value="c.value" :readonly="readonly" spellcheck="false" aria-label="Case value" data-test="form-case-value" @change="renameCase(i, $event)" />
          <select :value="c.target" :disabled="readonly" aria-label="Goes to" data-test="form-case-target" @change="caseTarget(i, $event)">
            <option value="">(end)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
          </select>
          <button v-if="!readonly" type="button" class="mini" data-test="form-case-remove" @click="removeCase(i)">Remove</button>
        </div>
        <button v-if="!readonly" type="button" class="mini" data-test="form-case-add" @click="addCase">Add a case</button>
      </fieldset>
      <label class="field">Default
        <select :value="str(step.default)" :disabled="readonly" data-test="form-default" @change="setRef('default')($event)">
          <option value="">(end)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
        </select>
      </label>
    </template>

    <!-- forEach -->
    <template v-else-if="step.type === 'forEach'">
      <ExpressionField name="items" label="Items" :model-value="step.items" :suggestions="suggestions" :readonly="readonly" @update:model-value="(v) => change((s) => { s.items = v; })" />
      <label class="field">Item name
        <input :value="str(step.as)" :readonly="readonly" spellcheck="false" data-test="form-as" @input="change((s) => { s.as = ($event.target as HTMLInputElement).value; })" />
      </label>
      <label class="field">Body (each item)
        <select :value="str(step.body)" :disabled="readonly" data-test="form-body" @change="setRef('body')($event)">
          <option value="">(none)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
        </select>
      </label>
      <label class="field">Then (when done)
        <select :value="str(step.then)" :disabled="readonly" data-test="form-then" @change="setRef('then')($event)">
          <option value="">(end)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
        </select>
      </label>
    </template>

    <!-- set -->
    <template v-else-if="step.type === 'set'">
      <label class="field">Variable
        <select :value="str(step.var)" :disabled="readonly" data-test="form-var" @change="change((s) => { s.var = ($event.target as HTMLSelectElement).value; })">
          <option value="" disabled>Choose a variable</option>
          <option v-if="str(step.var) && !variables.some((v) => v.name === step.var)" :value="str(step.var)">{{ str(step.var) }} (not declared)</option>
          <option v-for="v in variables" :key="v.name" :value="v.name">{{ v.name }}: {{ v.type }}</option>
        </select>
      </label>
      <ExpressionField name="value" label="Value" :type="io.out?.type" :model-value="step.value" :suggestions="suggestions" :readonly="readonly"
        @update:model-value="(v) => change((s) => { s.value = v; })" />
    </template>

    <!-- file.* -->
    <template v-else-if="isFile">
      <label class="field">Operation
        <select :value="step.type" :disabled="readonly" data-test="form-file-op" @change="pickFileOp">
          <option v-for="o in FILE_OPERATIONS" :key="o.type" :value="o.type">{{ o.label }}</option>
        </select>
      </label>
      <ExpressionField name="path" label="Path" type="string" required :model-value="step.path" :suggestions="suggestions" :readonly="readonly"
        @update:model-value="(v) => change((s) => { s.path = v; })" />
      <ExpressionField v-if="fileWrites" name="text" label="Text" type="string" required :model-value="step.text" :suggestions="suggestions" :readonly="readonly"
        @update:model-value="(v) => change((s) => { s.text = v; })" />
      <label v-if="fileReads" class="field">Save result into
        <select :value="str(step.into)" :disabled="readonly" data-test="form-into" @change="change((s) => { const v = ($event.target as HTMLSelectElement).value; if (v) s.into = v; else delete s.into; })">
          <option value="">(don't save it)</option>
          <option v-for="v in variables" :key="v.name" :value="v.name">{{ v.name }}: {{ v.type }}</option>
        </select>
      </label>
    </template>

    <!-- log -->
    <template v-else-if="step.type === 'log'">
      <ExpressionField name="text" label="Text" type="string" :model-value="step.text" :suggestions="suggestions" :readonly="readonly"
        @update:model-value="(v) => change((s) => { s.text = v; })" />
    </template>

    <!-- end -->
    <template v-else-if="step.type === 'end'">
      <fieldset class="group" data-test="form-outputs">
        <legend>Outputs</legend>
        <p v-if="!outputs.length" class="muted small">No outputs: the run just ends.</p>
        <div v-for="([k, v], i) in outputs" :key="i" class="output" data-test="form-output" :data-name="k">
          <input :value="k" :readonly="readonly" list="step-form-outputs" spellcheck="false" aria-label="Output name" data-test="form-output-name" @change="renameOutput(i, $event)" />
          <ExpressionField :name="`output-${k}`" label="Value" :type="declaredOutputs.find((o) => o.name === k)?.type" :model-value="v" :suggestions="suggestions" :readonly="readonly"
            @update:model-value="(x) => setOutputs(outputs.map(([a, b], j) => [a, j === i ? x : b]))" />
          <button v-if="!readonly" type="button" class="mini" data-test="form-output-remove" @click="setOutputs(outputs.filter((_, j) => j !== i))">Remove</button>
        </div>
        <button v-if="!readonly" type="button" class="mini" data-test="form-output-add" @click="addOutput">Add an output</button>
      </fieldset>
    </template>

    <p v-else class="muted small" data-test="form-other">Edit this step on the JSON tab.</p>

    <template v-if="hasInto">
      <label class="field">Save result into
        <select :value="intoValue" :disabled="readonly" data-test="form-into" @change="pickInto">
          <option value="">(don't save it)</option>
          <option v-for="v in variables" :key="v.name" :value="v.name">{{ v.name }}: {{ v.type }}{{ io.out && io.out.type === v.type ? ' ✓' : '' }}</option>
          <option v-if="!readonly" :value="NEW">New variable…</option>
        </select>
      </label>
      <div v-if="intoChoice === NEW" class="new row" data-test="form-into-new">
        <input v-model="newName" placeholder="name" spellcheck="false" data-test="form-into-new-name" />
        <input v-model="newType" list="step-form-types" placeholder="type" spellcheck="false" data-test="form-into-new-type" />
        <button type="button" class="mini" :disabled="!!newProblem" :title="newProblem ?? ''" data-test="form-into-new-add" @click="addInto">Add</button>
        <button type="button" class="mini" @click="intoChoice = null">Cancel</button>
      </div>
    </template>

    <label v-if="hasNext" class="field">Next
      <select :value="str(step.next)" :disabled="readonly" data-test="form-next" @change="setRef('next')($event)">
        <option value="">(end)</option><option v-for="t in targets" :key="t" :value="t">{{ t }}</option>
      </select>
    </label>

    <div v-if="io.in.length || io.out" class="io" data-test="form-io">
      <div v-if="io.in.length"><span class="muted">in</span> <code data-test="form-io-in">{{ ioInText(io) }}</code></div>
      <div v-if="io.out"><span class="muted">out</span> <code data-test="form-io-out">{{ ioOutText(io) }}</code></div>
    </div>

    <datalist id="step-form-types"><option v-for="t in types" :key="t" :value="t" /></datalist>
    <datalist id="step-form-timeouts"><option v-for="t in TELL_TIMEOUTS" :key="t" :value="t" /></datalist>
    <datalist id="step-form-outputs"><option v-for="o in declaredOutputs" :key="o.name" :value="o.name" /></datalist>
  </div>
</template>

<style scoped>
.form { display: flex; flex-direction: column; gap: 8px; font-size: 12.5px; }
.field { display: flex; flex-direction: column; gap: 2px; font-size: 12px; color: var(--ink-soft); }
.field input, .field select, .case input, .output > input, .new input { font-size: 12px; padding: 3px 6px; border: 1px solid var(--line); border-radius: 4px; background: var(--surface); color: var(--ink); }
.check { display: flex; gap: 6px; align-items: flex-start; font-size: 12px; color: var(--ink-soft); }
.group { border: 1px solid var(--line-soft); border-radius: 5px; padding: 6px 8px; display: flex; flex-direction: column; gap: 6px; margin: 0; }
.group legend { font-size: 11.5px; color: var(--muted); padding: 0 4px; }
.row { display: flex; gap: 6px; align-items: center; }
.case input { width: 90px; }
.case select { flex: 1 1 auto; min-width: 0; }
.output { display: flex; flex-direction: column; gap: 3px; padding-bottom: 4px; border-bottom: 1px dashed var(--line-soft); }
.extra { display: flex; flex-direction: column; gap: 2px; }
.new input { width: 90px; flex: 1 1 auto; }
.override { color: var(--accent); margin: 0; }
.io { display: flex; flex-direction: column; gap: 2px; padding: 6px 8px; border-radius: 5px; background: var(--surface-2); font-size: 11.5px; }
.io code { word-break: break-word; }
.field textarea.code { font-family: ui-monospace, monospace; font-size: 12px; padding: 4px 6px; border: 1px solid var(--line); border-radius: 4px; background: var(--surface); color: var(--ink); resize: vertical; tab-size: 2; white-space: pre; }
.mini { font-size: 11.5px; padding: 1px 8px; align-self: flex-start; }
.small { font-size: 12px; margin: 0; }
.warn { color: var(--warn-ink); }
</style>
