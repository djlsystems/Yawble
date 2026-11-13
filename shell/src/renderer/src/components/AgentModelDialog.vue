<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { AgentModelList, AgentProfileRow, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import AgentModelPicker from './AgentModelPicker.vue';

// An agent's default model. A built-in's is saved with agentProfiles.setDefaultModel; a custom agent's with
// agentProfiles.update (its arguments unchanged). The list is the app's own (agents.models runs no CLI).
const props = defineProps<{ api?: YawbleApi; agent: AgentProfileRow; title: string }>();
const emit = defineEmits<{ close: []; saved: [string] }>();
const api = resolveApi(props.api);

const models = ref<string[]>([]);
const modelsReady = ref(false); // the picker waits for the list, so a listed model is not first shown as Custom…
const model = ref(props.agent.defaultModel ?? '');
const error = ref<string | null>(null);
const saving = ref(false);
const incomplete = ref(false); // Custom… with no name

onMounted(async () => {
  try { models.value = (await api.call<AgentModelList>('agents.models', { cli: props.agent.baseCli })).models; }
  catch (e) { error.value = errorText(e); }
  modelsReady.value = true;
});

async function save() {
  if (saving.value) return;
  saving.value = true;
  error.value = null;
  const a = props.agent;
  try {
    if (a.custom) {
      await api.call('agentProfiles.update', {
        id: a.id, name: a.name, defaultModel: model.value, argsBefore: a.argsBefore, argsAfter: a.argsAfter, removedArgs: a.removedArgs,
      });
    } else {
      await api.call('agentProfiles.setDefaultModel', { baseCli: a.baseCli, model: model.value });
    }
    emit('saved', `Default model of ${props.title} saved.`);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <Modal :title="`Default model · ${title}`" @close="emit('close')">
    <div data-test="model-dialog">
      <p class="muted small">Members on this agent use it unless they pick their own model.</p>
      <LoadingNote v-if="!modelsReady" text="Loading the models…" />
      <AgentModelPicker v-else v-model="model" :models="models" test-id="model-dialog-pick" @incomplete="incomplete = $event" />
      <p v-if="error" class="error" data-test="model-dialog-error">{{ error }}</p>
      <div class="row actions"><span class="grow"></span>
        <button data-test="model-dialog-cancel" :disabled="saving" @click="emit('close')">Cancel</button>
        <button class="primary" data-test="model-dialog-save" :disabled="saving || !modelsReady || incomplete" :aria-busy="saving" @click="save"><BusyLabel :busy="saving" text="Saving…">Save</BusyLabel></button>
      </div>
    </div>
  </Modal>
</template>

<style scoped>
.small { font-size: 12px; }
.actions { margin-top: 12px; }
</style>
