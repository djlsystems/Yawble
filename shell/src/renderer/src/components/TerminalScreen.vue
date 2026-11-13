<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { YawbleApi, TerminalSessionInfo } from '../../../shared/api';
import TerminalView from './TerminalView.vue';
import TranscriptView from './TranscriptView.vue';

// One terminal channel, shown Formatted (a headless run's JSON events as a person reads them) or Raw (the terminal
// itself, as the CLI printed it). The terminal stays attached either way. An interactive session is a TUI: Raw.
const props = defineProps<{ channel: string; api?: YawbleApi; rows?: number }>();
const emit = defineEmits<{ session: [TerminalSessionInfo | null] }>();

type Mode = 'formatted' | 'raw';
const stored = (() => { try { return localStorage.getItem('yawble.terminalMode') as Mode | null; } catch { return null; } })();
const mode = ref<Mode>(stored === 'raw' ? 'raw' : 'formatted');
watch(mode, (m) => { try { localStorage.setItem('yawble.terminalMode', m); } catch { /* ignore */ } });

const text = ref('');
const session = ref<TerminalSessionInfo | null>(null);
const interactive = computed(() => !!session.value && !session.value.ended && session.value.kind !== 'headless');
const shown = computed<Mode>(() => (interactive.value ? 'raw' : mode.value));
const view = ref<InstanceType<typeof TerminalView> | null>(null);

function onSession(s: TerminalSessionInfo | null) {
  session.value = s;
  emit('session', s);
}

defineExpose({ size: () => view.value?.size() ?? null, focus: () => view.value?.focus() });
</script>

<template>
  <div class="screen-wrap">
    <div class="modes" role="tablist" data-test="terminal-modes">
      <button role="tab" :class="{ on: shown === 'formatted' }" :disabled="interactive" data-test="terminal-mode-formatted"
        :title="interactive ? 'An interactive session is shown as the terminal' : 'The run as a person reads it'" @click="mode = 'formatted'">Formatted</button>
      <button role="tab" :class="{ on: shown === 'raw' }" data-test="terminal-mode-raw" title="The terminal, exactly as the CLI printed it (JSON for headless runs)" @click="mode = 'raw'">Raw</button>
    </div>
    <div class="layer" :class="{ hidden: shown !== 'raw' }">
      <TerminalView ref="view" :channel="channel" :api="api" :rows="rows" @session="onSession" @text="text = $event" />
    </div>
    <div v-if="shown === 'formatted'" class="layer"><TranscriptView :text="text" /></div>
  </div>
</template>

<style scoped>
.screen-wrap { position: relative; height: 100%; min-height: 160px; display: flex; flex-direction: column; background: #101418; }
.modes { display: flex; gap: 2px; padding: 4px 6px; background: #151b23; border-bottom: 1px solid #2c3542; }
.modes button { font-size: 11.5px; padding: 2px 10px; border-radius: 10px; border: 1px solid transparent; background: transparent; color: #9aa6b8; }
.modes button.on { background: #222b36; border-color: #36404e; color: #e6ebf2; }
.modes button:disabled { opacity: .5; }
.layer { flex: 1; min-height: 0; position: relative; }
/* Hidden but laid out, so xterm keeps its size and keeps receiving output. */
.layer.hidden { position: absolute; inset: 0; visibility: hidden; pointer-events: none; }
</style>
