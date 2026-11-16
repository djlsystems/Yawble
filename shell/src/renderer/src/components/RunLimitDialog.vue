<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { GlobalPolicyDoc, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import Modal from './Modal.vue';
import BusyLabel from './BusyLabel.vue';
import LoadingNote from './LoadingNote.vue';

// The global run limit on its own, opened from the Kanban board's In Progress count: the same setting and words as
// Settings > Global policy > Limits, saved the same way (policy.saveGlobal with the rest of the policy unchanged).
const props = defineProps<{ api: YawbleApi }>();
const emit = defineEmits<{ close: []; changed: [limit: number] }>();

const Ceiling = 64;
const doc = ref<GlobalPolicyDoc | null>(null);
const value = ref('');
const error = ref<string | null>(null);
const saving = ref(false);

onMounted(async () => {
  try {
    doc.value = await props.api.call<GlobalPolicyDoc>('policy.getGlobal');
    value.value = String(doc.value.app.maxConcurrentAgents);
  } catch (e) { error.value = errorText(e); }
});

async function save() {
  if (!doc.value) return;
  const n = Number(value.value.trim());
  if (!Number.isInteger(n) || n < 1 || n > Ceiling) { error.value = `The global run limit is a whole number from 1 to ${Ceiling}.`; return; }
  error.value = null;
  saving.value = true;
  try {
    const policy = { ...doc.value, app: { ...doc.value.app, maxConcurrentAgents: n } };
    const back = await props.api.call<GlobalPolicyDoc>('policy.saveGlobal', { policy });
    emit('changed', back?.app?.maxConcurrentAgents ?? n);
    emit('close');
  } catch (e) { error.value = errorText(e); } finally { saving.value = false; }
}
</script>

<template>
  <Modal title="Global run limit" @close="emit('close')">
    <form class="rl" data-test="run-limit-dialog" @submit.prevent="save">
      <LoadingNote v-if="!doc && !error" text="Loading…" />
      <template v-if="doc">
        <label for="rl-value">Global run limit</label>
        <p class="muted small" data-test="run-limit-hint">Most sandboxes at once across all teams: member runs, Manager runs and interactive sessions. Runs beyond it wait in the queue.</p>
        <input id="rl-value" v-model="value" class="num" inputmode="numeric" data-test="run-limit-value" />
      </template>
      <p v-if="error" class="error small" data-test="run-limit-error">{{ error }}</p>
      <div class="actions">
        <button type="button" data-test="run-limit-cancel" @click="emit('close')">Cancel</button>
        <button type="submit" class="primary" data-test="run-limit-save" :disabled="!doc || saving" :aria-busy="saving"><BusyLabel :busy="saving" text="Saving…">Save</BusyLabel></button>
      </div>
    </form>
  </Modal>
</template>

<style scoped>
.rl { display: flex; flex-direction: column; gap: 8px; min-width: min(360px, 80vw); }
.rl label { font-weight: 600; }
.rl .num { width: 6em; }
.actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 4px; }
</style>
