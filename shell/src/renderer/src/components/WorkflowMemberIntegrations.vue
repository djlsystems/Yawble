<script setup lang="ts">
import { computed, ref } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { readOnly, vChange } from '../remoteAccess';
import { teamBindingChoices, type BindingChoice } from '../workflows/api';
import { bindingsFrom, integrationText, overrideChoices, workflowsApi, type WorkflowMember, type WorkflowMemberIntegration } from '../workflowMembers';
import BusyLabel from './BusyLabel.vue';

// A workflow member's integrations: each one its workflow names, and what the member uses for it ("Yawble DistinctID Support →
// Other inbox" when the member uses another integration of the same kind). Change sets or clears the override per
// integration (an alias picks its integration) over the team's integrations, through workflows.memberUpdate; a refusal shows
// its reason and keeps the choice. A Visitor only reads.
const props = defineProps<{ api: YawbleApi; member: WorkflowMember }>();
const emit = defineEmits<{ updated: [member: WorkflowMember] }>();
const rpc = workflowsApi(props.api);

const list = computed<WorkflowMemberIntegration[]>(() => props.member.integrations ?? []);
/** A real name: the member uses it, or overrides it; an alias has a binding and is never "overridden". */
const real = (i: WorkflowMemberIntegration) => i.overridden || i.uses.toLowerCase() === i.name.toLowerCase();

const editing = ref(false);
const choices = ref<BindingChoice[] | null>(null);
const picked = ref<Record<string, string>>({});
const error = ref<string | null>(null);
const saving = ref(false);

async function edit() {
  error.value = null;
  picked.value = Object.fromEntries(list.value.map((i) => [i.name, real(i) ? (i.overridden ? i.uses : '') : i.uses]));
  editing.value = true;
  try { choices.value = await teamBindingChoices(props.api, props.member.teamId); } catch (e) { choices.value = []; error.value = errorText(e); }
}

const options = (i: WorkflowMemberIntegration) => {
  const of = overrideChoices(choices.value ?? [], i);
  const current = picked.value[i.name];
  return current && !of.includes(current) ? [current, ...of] : of;
};

async function save() {
  if (saving.value) return;
  error.value = null;
  saving.value = true;
  try {
    const realNames = new Set(list.value.filter(real).map((i) => i.name));
    const m = await rpc.memberUpdate({ memberId: props.member.memberId, bindings: bindingsFrom(picked.value, (n) => realNames.has(n)) });
    editing.value = false;
    emit('updated', m);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <div v-if="list.length" class="integrations" data-test="workflow-member-integrations">
    <h4>Integrations
      <button v-if="!readOnly && !editing" v-change type="button" class="small-btn" data-test="workflow-member-integrations-change" @click="edit">Change</button>
    </h4>
    <ul v-if="!editing">
      <li v-for="i in list" :key="i.name" :class="{ over: i.overridden }" :data-test="`workflow-member-integration-${i.name}`"
        :title="i.overridden ? `This member uses ${i.uses} in place of ${i.name}` : ''">{{ integrationText(i) }}
        <span class="muted">({{ i.kind === 'email' ? 'Email' : 'API' }})</span></li>
    </ul>
    <form v-else class="edit" data-test="workflow-member-integrations-form" @submit.prevent="save">
      <label v-for="i in list" :key="i.name" :data-test="`workflow-member-override-${i.name}`">{{ i.name }} <span class="muted">({{ i.kind === 'email' ? 'Email' : 'API' }})</span>
        <select v-model="picked[i.name]" :disabled="choices === null">
          <option v-if="real(i)" value="">Use {{ i.name }}</option>
          <option v-else value="" disabled>Choose one of the team's integrations</option>
          <option v-for="n in options(i)" :key="n" :value="n">{{ real(i) ? `Use ${n} instead` : n }}</option>
        </select>
      </label>
      <p v-if="error" class="error small" data-test="workflow-member-integrations-error">{{ error }}</p>
      <div class="buttons">
        <button type="submit" class="primary small-btn" data-test="workflow-member-integrations-save" :disabled="saving" :aria-busy="saving">
          <BusyLabel :busy="saving" text="Saving…">Save</BusyLabel></button>
        <button type="button" class="small-btn" data-test="workflow-member-integrations-cancel" :disabled="saving" @click="editing = false">Cancel</button>
      </div>
    </form>
  </div>
</template>

<style scoped>
h4 { font-size: 12px; margin: 6px 0 2px; color: var(--ink-soft); font-weight: 600; display: flex; align-items: center; gap: 6px; }
ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; font-size: 12.5px; }
li.over { color: var(--accent); }
.edit { display: flex; flex-direction: column; gap: 6px; }
.edit label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--ink-soft); }
.edit select { font: inherit; font-size: 13px; }
.buttons { display: flex; gap: 6px; }
.small-btn { font-size: 12px; padding: 1px 8px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); margin: 0; }
</style>
