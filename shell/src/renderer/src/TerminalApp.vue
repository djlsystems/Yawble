<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import LoadingNote from './components/LoadingNote.vue';
import BusyLabel from './components/BusyLabel.vue';
import type { YawbleApi, TerminalBanner, TerminalSessionInfo } from '../../shared/api';
import { errorText, resolveApi } from './api';
import TerminalScreen from './components/TerminalScreen.vue';

// An agent's terminal window: titled with the effective-policy banner; attaches to a running
// batch (input off) or shows the last transcript with "Open interactive CLI"; Stop. Closing only detaches.
// Embedded: the same, in the main window's terminal pane (the window's title is left alone).
const props = defineProps<{ memberId: number; api?: YawbleApi; pollMs?: number; embedded?: boolean }>();
const emit = defineEmits<{ banner: [TerminalBanner] }>();
const api = resolveApi(props.api);

const banner = ref<TerminalBanner | null>(null);
const session = ref<TerminalSessionInfo | null>(null);
const error = ref<string | null>(null);
const view = ref<InstanceType<typeof TerminalScreen> | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;

const live = computed(() => !!session.value && !session.value.ended);
const stateLine = computed(() => {
  const b = banner.value;
  const s = session.value;
  if (!b) return '';
  if (s && !s.ended && s.kind === 'interactive') return `Interactive ${s.command ?? ''} session · input on`;
  if (b.state === 'running') return `Run ${b.runId} · ${s?.command ?? ''} · input off during headless run`;
  if (b.state === 'queued') return `Queued · position ${b.queuePosition} of ${b.queueLength} · waiting for a global run slot`;
  const last = b.lastRunState ? ` · last run ${b.lastRunState}${b.lastRunReason ? `: ${b.lastRunReason}` : ''}` : '';
  return `Idle${last} · showing the last transcript`;
});

async function refresh() {
  try {
    banner.value = await api.call<TerminalBanner>('terminal.banner', { memberId: props.memberId });
    emit('banner', banner.value);
    if (!props.embedded) document.title = banner.value.title;
    error.value = null;
  } catch (e) {
    error.value = errorText(e);
  }
}

/** Open interactive CLI / Stop clicked and the core has not answered yet: that button is held with a spinner. */
const doing = ref<'open' | 'stop' | null>(null);
async function openInteractive() {
  if (doing.value) return;
  error.value = null;
  doing.value = 'open';
  try {
    // The PTY starts at the window's fitted size, so the CLI's TUI draws for the terminal it is shown in.
    await api.call('terminal.openInteractive', { memberId: props.memberId, ...(view.value?.size() ?? {}) });
  } catch (e) {
    error.value = errorText(e);
  } finally {
    doing.value = null;
  }
}

async function stop() {
  if (doing.value) return;
  error.value = null;
  doing.value = 'stop';
  try {
    await api.call('terminal.stop', { memberId: props.memberId });
    await refresh();
  } catch (e) {
    error.value = errorText(e);
  } finally {
    doing.value = null;
  }
}

onMounted(() => {
  void refresh();
  timer = setInterval(refresh, props.pollMs ?? 1000);
});
onBeforeUnmount(() => { if (timer) clearInterval(timer); });
</script>

<template>
  <div class="term-window" :class="{ embedded }">
    <header class="banner" data-test="banner">
      <div class="title mono" data-test="banner-title">{{ banner?.title ?? 'Sandbox terminal' }}</div>
      <div class="details mono" data-test="banner-details">{{ banner?.details.join(' · ') }}</div>
      <div v-if="banner?.loosened.length" class="loosened mono" data-test="banner-loosened">loosened: {{ banner.loosened.join('; ') }}</div>
    </header>
    <div class="bar row">
      <LoadingNote v-if="!banner && !error" class="grow" text="Loading the terminal…" />
      <span v-else class="grow state" data-test="terminal-state">{{ stateLine }}</span>
      <span v-if="error" class="error" data-test="terminal-error">{{ error }}</span>
      <button v-if="banner && banner.state === 'idle' && !live" data-test="open-interactive" :disabled="!!doing" :aria-busy="doing === 'open'" @click="openInteractive"><BusyLabel :busy="doing === 'open'" text="Opening…">Open interactive CLI</BusyLabel></button>
      <button data-test="terminal-stop" :disabled="!(banner?.state === 'running' || live) || !!doing" :aria-busy="doing === 'stop'" @click="stop"><BusyLabel :busy="doing === 'stop'" text="Stopping…">Stop ■</BusyLabel></button>
    </div>
    <div class="screen">
      <TerminalScreen ref="view" :key="memberId" :channel="`member:${memberId}`" :api="api" @session="session = $event" />
    </div>
  </div>
</template>

<style scoped>
.term-window { display: flex; flex-direction: column; height: 100vh; background: #101418; color: #d8dee9; }
.term-window.embedded { height: 100%; }
.banner { padding: 8px 12px; background: #1b222c; border-bottom: 1px solid #2c3542; }
.title { font-size: 13px; color: #fff; }
.details { font-size: 12px; color: #9aa6b8; margin-top: 2px; }
.loosened { font-size: 12px; color: #f2b84b; margin-top: 2px; }
.bar { padding: 4px 12px; font-size: 12px; background: #151b23; color: #9aa6b8; }
.bar button { background: #222b36; color: #d8dee9; border-color: #36404e; }
.screen { flex: 1; min-height: 0; }
.error { color: #ff8a8a; }
</style>
