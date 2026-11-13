<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { CommandLines, DetectedCli, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';

// The exact command line an agent runs (agents.commandLine: the core's own builder, per-run values as placeholders,
// environment variable NAMES only), and what the sandbox gets for the CLI it runs.
const props = defineProps<{ api?: YawbleApi; cli: string; title: string; detected: DetectedCli | null }>();
const emit = defineEmits<{ close: [] }>();
const api = resolveApi(props.api);

const lines = ref<CommandLines | null>(null);
const error = ref<string | null>(null);

onMounted(async () => {
  try { lines.value = await api.call<CommandLines>('agents.commandLine', { cli: props.cli }); }
  catch (e) { error.value = errorText(e); }
});
</script>

<template>
  <Modal :title="`Command line · ${title}`" wide @close="emit('close')">
    <div data-test="cmd-dialog">
      <p v-if="error" class="error" data-test="cmd-error">{{ error }}</p>
      <LoadingNote v-else-if="!lines" text="Loading the command line…" />
      <template v-else>
        <p class="muted small">Model: {{ lines.model.label }}</p>
        <div class="kind" data-test="cmd-headless"><b>Headless run</b>
          <code class="mono">{{ lines.headless.argv.join(' ') }}</code>
          <span class="muted small">environment variables set (names only): {{ lines.headless.env.join(', ') || 'none' }}</span></div>
        <div class="kind" data-test="cmd-interactive"><b>Interactive run</b>
          <code class="mono">{{ lines.interactive.argv.join(' ') }}</code>
          <span class="muted small">environment variables set (names only): {{ lines.interactive.env.join(', ') || 'none' }}</span></div>
      </template>
      <section class="sandbox" data-test="cmd-sandbox">
        <b>What the sandbox gets</b>
        <template v-if="detected?.found">
          <div class="small">Read-only: <span class="mono" data-test="cli-readonly">{{ detected.readOnlyFolders.join(' · ') || 'nothing' }}</span></div>
          <div class="small">Kept blocked (its config and sign-in): <span class="mono" data-test="cli-config">{{ detected.configFolders.join(' · ') }}</span></div>
        </template>
        <div v-else class="small muted">The CLI is not installed on this computer, so nothing of it is granted.</div>
      </section>
    </div>
  </Modal>
</template>

<style scoped>
.kind { margin: 8px 0; display: flex; flex-direction: column; gap: 2px; }
.sandbox { margin-top: 12px; display: flex; flex-direction: column; gap: 2px; }
.small { font-size: 12px; }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; word-break: break-all; font-size: 12px; }
</style>
