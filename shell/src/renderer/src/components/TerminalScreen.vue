<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue';
import type { YawbleApi, TerminalSessionInfo } from '../../../shared/api';
import { PaneHeaderKey } from '../terminalPane';
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
/** The pane's header on a phone (TerminalPane): the tabs go into it. */
const pane = inject(PaneHeaderKey, null);
const inHeader = computed(() => !!pane?.on);

function onSession(s: TerminalSessionInfo | null) {
  session.value = s;
  emit('session', s);
}

defineExpose({ size: () => view.value?.size() ?? null, focus: () => view.value?.focus() });
</script>

<template>
  <div class="screen-wrap" :class="{ 'in-pane': inHeader }">
    <!-- In the main window's pane on a phone (B057) the tabs sit in the pane's one header row; anywhere else, here. -->
    <Teleport defer :key="inHeader ? 'header' : 'here'" :to="inHeader ? `#${pane!.tabs}` : 'body'" :disabled="!inHeader">
    <div class="modes" :class="{ 'in-header': inHeader }" role="tablist" data-test="terminal-modes">
      <button role="tab" :class="{ on: shown === 'formatted' }" :disabled="interactive" data-test="terminal-mode-formatted"
        :title="interactive ? 'An interactive session is shown as the terminal' : 'The run as a person reads it'" @click="mode = 'formatted'">Formatted</button>
      <button role="tab" :class="{ on: shown === 'raw' }" data-test="terminal-mode-raw" title="The terminal, exactly as the CLI printed it (JSON for headless runs)" @click="mode = 'raw'">Raw</button>
    </div>
    </Teleport>
    <div class="layer" :class="{ hidden: shown !== 'raw' }">
      <TerminalView ref="view" :channel="channel" :api="api" :rows="rows" @session="onSession" @text="text = $event" />
    </div>
    <div v-if="shown === 'formatted'" class="layer"><TranscriptView :text="text" /></div>
  </div>
</template>

<style scoped>
.screen-wrap { position: relative; height: 100%; min-height: 160px; display: flex; flex-direction: column; background: var(--term-bg); }
.modes { display: flex; gap: 2px; padding: 4px 6px; background: var(--term-bar); border-bottom: 1px solid var(--term-line); }
.modes button { font-size: 11.5px; padding: 2px 10px; border-radius: 10px; border: 1px solid transparent; background: transparent; color: var(--term-muted); }
.modes button.on { background: var(--term-btn-on); border-color: var(--term-btn-line); color: var(--term-on-ink); }
.modes button:disabled { opacity: .5; }
.layer { flex: 1; min-height: 0; position: relative; }
/* Hidden but laid out, so xterm keeps its size and keeps receiving output. */
.layer.hidden { position: absolute; inset: 0; visibility: hidden; pointer-events: none; }
/* A phone (B057): the screen takes what the pane leaves it (no 160 px floor pushing xterm past the bottom); in the
   pane's header the tabs are part of its one row. */
@media (max-width: 759.98px) {
  .screen-wrap.in-pane { min-height: 0; }
  .modes.in-header { padding: 0; gap: 0; background: transparent; border-bottom: 0; }
  .modes.in-header button { min-height: 44px; padding: 0 8px; font-size: 13px; border-radius: 8px; }
}
</style>
