<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { pretty } from '../../workflows/api';
import type { DesignContext, MemberIntegration } from '../../workflows/catalog';
import type { FlowNode } from '../../workflows/flowchart';
import type { WorkflowStep, WorkflowVariable } from '../../workflows/types';
import StepForm from './StepForm.vue';

// The flowchart's side panel for the selected node: the check's problems for it, then the step on two tabs, Design (a
// form for its type, the default) and JSON (the step's JSON, editable unless read-only). Both write the one document:
// a valid JSON edit (an object with a string id and type) goes straight in; one that is not is refused and the form
// keeps the step as it was.
const props = defineProps<{
  node: FlowNode; step: WorkflowStep | null; readonly: boolean;
  ctx: DesignContext; integrations?: MemberIntegration[] | null;
}>();
const emit = defineEmits<{ change: [step: WorkflowStep, variable?: WorkflowVariable]; remove: []; close: [] }>();

const tab = ref<'design' | 'json'>('design');
const local = ref(props.step ? pretty(props.step) : '');
const problem = ref<string | null>(null);

const same = (a: string, b: string) => {
  try { return JSON.stringify(JSON.parse(a)) === JSON.stringify(JSON.parse(b)); } catch { return false; }
};
// The document changed from elsewhere (the form, undo, a connection): show it, unless it is what is typed.
watch(() => (props.step ? pretty(props.step) : ''), (incoming) => {
  if (!same(local.value, incoming)) { local.value = incoming; problem.value = null; }
});

function edit(e: Event) {
  local.value = (e.target as HTMLTextAreaElement).value;
  if (props.readonly) return;
  let v: unknown;
  try { v = JSON.parse(local.value); } catch (err) { problem.value = `Not valid JSON, so it is not used: ${(err as Error).message}`; return; }
  if (!v || typeof v !== 'object' || Array.isArray(v)) { problem.value = 'A step is a JSON object.'; return; }
  const s = v as WorkflowStep;
  if (typeof s.id !== 'string' || !s.id.trim()) { problem.value = 'A step needs an "id".'; return; }
  if (typeof s.type !== 'string' || !s.type.trim()) { problem.value = 'A step needs a "type".'; return; }
  problem.value = null;
  emit('change', s);
}

const heading = computed(() => (props.node.kind === 'start' ? 'Start' : props.node.kind === 'end' ? 'End' : props.node.id));
</script>

<template>
  <aside class="panel" data-test="flow-panel" :data-node="node.id">
    <div class="head">
      <strong><code>{{ heading }}</code></strong>
      <span v-if="node.stepType" class="muted">{{ node.stepType }}</span>
      <span class="grow" />
      <button type="button" class="mini" data-test="flow-panel-close" @click="emit('close')">Close</button>
    </div>
    <ul v-if="node.problems.length" class="problems" data-test="flow-panel-problems">
      <li v-for="(p, i) in node.problems" :key="i" data-test="flow-panel-problem">{{ p }}</li>
    </ul>
    <p v-else class="muted small">No problems from the last check.</p>
    <template v-if="step">
      <div class="tabs" role="tablist">
        <button type="button" role="tab" :aria-selected="tab === 'design'" :class="{ on: tab === 'design' }" data-test="flow-panel-tab-design" @click="tab = 'design'">Design</button>
        <button type="button" role="tab" :aria-selected="tab === 'json'" :class="{ on: tab === 'json' }" data-test="flow-panel-tab-json" @click="tab = 'json'">JSON</button>
      </div>
      <StepForm v-if="tab === 'design'" :step="step" :ctx="ctx" :readonly="readonly" :integrations="integrations" @change="(s, v) => emit('change', s, v)" />
      <template v-else>
        <textarea class="json" :value="local" :readonly="readonly" spellcheck="false" aria-label="Step JSON" data-test="flow-panel-json" @input="edit" />
        <p v-if="problem" class="error small" data-test="flow-panel-error">{{ problem }}</p>
      </template>
      <button v-if="!readonly" type="button" class="danger mini" data-test="flow-panel-delete" @click="emit('remove')">Delete step</button>
    </template>
    <p v-else class="muted small">A step whose next is empty ends the run here.</p>
  </aside>
</template>

<style scoped>
.panel { display: flex; flex-direction: column; gap: 6px; width: 320px; flex: 0 0 auto; border-left: 1px solid var(--line-soft); padding-left: 10px; min-height: 0; overflow: auto; font-size: 12.5px; }
.head { display: flex; align-items: center; gap: 8px; }
.grow { flex: 1 1 auto; }
.problems { margin: 0; padding: 6px 8px 6px 22px; background: var(--warn-bg); border: 1px solid var(--warn-line); border-radius: 5px; color: var(--warn-ink); }
.tabs { display: inline-flex; align-self: flex-start; border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }
.tabs button { border: 0; border-radius: 0; padding: 3px 10px; font-size: 12px; background: var(--surface); color: var(--ink); }
.tabs button.on { background: var(--selected); }
.json { min-height: 220px; flex: 1 1 auto; font-family: ui-monospace, monospace; font-size: 12px; padding: 6px 8px; border: 1px solid var(--line); border-radius: 5px; background: var(--surface); color: var(--ink); resize: vertical; }
.json[readonly] { background: var(--surface-sunken); }
.mini { font-size: 11.5px; padding: 1px 8px; }
.danger { align-self: flex-start; color: var(--bad); }
.small { font-size: 12px; margin: 0; }
</style>
