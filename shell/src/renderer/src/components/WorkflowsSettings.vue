<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { readOnly } from '../remoteAccess';
import { workflowsSettingsApi, type WorkflowsAppSettings } from '../workflows/api';
import LoadingNote from './LoadingNote.vue';

// Settings > Workflows: what applies to every workflow member. "Workflows may tell other teams" (on by default): off, a tell
// step that names another team fails with that reason. A Visitor sees it but cannot change it.
const props = defineProps<{ api?: YawbleApi }>();
const rpc = workflowsSettingsApi(resolveApi(props.api));

const settings = ref<WorkflowsAppSettings | null>(null);
const error = ref<string | null>(null);
const saving = ref(false);

onMounted(async () => {
  try { settings.value = await rpc.get(); } catch (e) { error.value = errorText(e); }
});

async function setTellOtherTeams(e: Event) {
  const on = (e.target as HTMLInputElement).checked;
  error.value = null;
  saving.value = true;
  try {
    settings.value = await rpc.set({ tellOtherTeams: on });
  } catch (err) {
    error.value = errorText(err);
    (e.target as HTMLInputElement).checked = settings.value?.tellOtherTeams ?? true;
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <section class="workflows-settings" data-test="settings-workflows">
    <h3>Workflows</h3>
    <p v-if="error" class="error" data-test="settings-workflows-error">{{ error }}</p>
    <LoadingNote v-if="!settings && !error" text="Loading settings…" />
    <label v-if="settings" class="check">
      <input type="checkbox" :checked="settings.tellOtherTeams" :disabled="readOnly || saving" data-test="workflows-tell-other-teams" @change="setTellOtherTeams" />
      <span>
        <b>Workflows may tell other teams</b>
        <span class="muted">A workflow's Tell step gives work to an agent member of its own team, or of another team when the step names one.
          Off: a Tell to another team fails the run, with that reason.</span>
      </span>
    </label>
    <p class="muted small">How many tells one run may make is set on each workflow member's card on its team page.</p>
  </section>
</template>

<style scoped>
.workflows-settings { display: flex; flex-direction: column; gap: 10px; max-width: 760px; }
h3 { margin: 0; }
.check { display: flex; gap: 10px; align-items: flex-start; }
.check > span { display: flex; flex-direction: column; gap: 2px; }
.small { font-size: 12.5px; margin: 0; }
.error { color: var(--bad); margin: 0; }
</style>
