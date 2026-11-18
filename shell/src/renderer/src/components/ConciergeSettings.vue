<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { ConciergeDefaults, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { useAgentOptions } from '../agentOptions';
import { savedConciergeAgent } from '../conciergeDefault';
import { readOnly } from '../remoteAccess';
import LoadingNote from './LoadingNote.vue';

// Settings > Concierge: the agent new Concierge conversations start on. Saved as soon as it is picked; existing
// conversations keep their own agent.
const props = defineProps<{ api?: YawbleApi }>();
const api = resolveApi(props.api);
const agentOptions = useAgentOptions(api);

const loaded = ref(false);
const value = ref<string | null>(null);
const error = ref<string | null>(null);
const saving = ref(false);
const saved = ref(false);

onMounted(async () => {
  try {
    const d = await api.call<ConciergeDefaults>('concierge.defaults');
    value.value = d.defaultAgent ?? null;
    savedConciergeAgent.value = value.value;
    loaded.value = true;
  } catch (e) { error.value = errorText(e); }
});

async function pick(next: string | null) {
  const before = value.value;
  value.value = next;
  error.value = null;
  saved.value = false;
  saving.value = true;
  try {
    await api.call('concierge.setDefaults', { defaultAgent: next });
    savedConciergeAgent.value = next; // the window's next new chat starts on it (App.vue defaultCli)
    saved.value = true;
  } catch (e) {
    value.value = before;
    error.value = errorText(e);
  } finally {
    saving.value = false;
  }
}

function onChange(e: Event) {
  const v = (e.target as HTMLSelectElement).value;
  void pick(v === '' ? null : v);
}
</script>

<template>
  <section class="concierge-settings" data-test="settings-concierge">
    <h3>New conversations</h3>
    <p v-if="error" class="error" data-test="concierge-defaults-error">{{ error }}</p>
    <LoadingNote v-if="!loaded && !error" text="Loading settings…" />
    <label v-if="loaded" class="field">
      <span><b>Default agent</b> <span class="muted">New Concierge conversations start on this agent; existing ones keep theirs.</span></span>
      <select :value="value ?? ''" data-test="concierge-default-agent" :disabled="readOnly || saving" @change="onChange">
        <option value="">First available on this computer</option>
        <option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option>
        <option v-if="value && !agentOptions.some((o) => o.id === value)" :value="value">{{ value }}</option>
      </select>
      <span v-if="saved" class="muted small" data-test="concierge-defaults-saved">Saved</span>
    </label>
  </section>
</template>

<style scoped>
h3 { margin: 0 0 12px; font-size: 15px; }
.field { display: flex; flex-direction: column; gap: 6px; font-size: 13.5px; max-width: 520px; }
.field select { max-width: 100%; }
.muted { color: var(--muted); font-weight: 400; }
.small { font-size: 12px; }
.error { color: var(--bad); }
</style>
