<script setup lang="ts">
import { computed, ref } from 'vue';
import {
  actionOf, CONDITION_OPS, expressionSuggestions, integrationOf, ioInText, ioOutText, isUnaryOp, NATIVE_TYPES, stepIO,
  type DesignContext, type MemberIntegration,
} from '../../scripts/catalog';
import type { ScriptStep, ScriptVariable } from '../../scripts/types';
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
const hasNext = computed(() => !['if', 'switch', 'forEach', 'end'].includes(props.step.type));

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

// ---- if
const when = computed(() => obj(props.step.when));
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

    <!-- if -->
    <template v-else-if="step.type === 'if'">
      <template v-if="simpleWhen">
        <ExpressionField name="when-left" label="Left value" :model-value="when.left" :suggestions="suggestions" :readonly="readonly" @update:model-value="(v) => setWhen('left', v)" />
        <label class="field">Operator
          <select :value="String(when.op ?? 'eq')" :disabled="readonly" data-test="form-when-op" @change="(e) => setWhen('op', (e.target as HTMLSelectElement).value)">
            <option v-for="op in CONDITION_OPS" :key="op" :value="op">{{ op }}</option>
          </select>
        </label>
        <ExpressionField v-if="!isUnaryOp(String(when.op ?? 'eq'))" name="when-right" label="Right value" :model-value="when.right" :suggestions="suggestions" :readonly="readonly"
          @update:model-value="(v) => setWhen('right', v)" />
      </template>
      <p v-else class="muted small" data-test="form-when-combined">This condition combines others (all, any or not): edit it on the JSON tab.</p>
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
    <datalist id="step-form-outputs"><option v-for="o in declaredOutputs" :key="o.name" :value="o.name" /></datalist>
  </div>
</template>

<style scoped>
.form { display: flex; flex-direction: column; gap: 8px; font-size: 12.5px; }
.field { display: flex; flex-direction: column; gap: 2px; font-size: 12px; color: var(--ink-soft); }
.field input, .field select, .case input, .output > input, .new input { font-size: 12px; padding: 3px 6px; border: 1px solid var(--line); border-radius: 4px; background: var(--surface); color: var(--ink); }
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
.mini { font-size: 11.5px; padding: 1px 8px; align-self: flex-start; }
.small { font-size: 12px; margin: 0; }
.warn { color: var(--warn-ink); }
</style>
