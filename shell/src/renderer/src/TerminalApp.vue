<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, ref } from 'vue';
import { PaneHeaderKey } from './terminalPane';
import LoadingNote from './components/LoadingNote.vue';
import BusyLabel from './components/BusyLabel.vue';
import type { YawbleApi, TerminalBanner, TerminalSessionInfo } from '../../shared/api';
import { errorText, resolveApi } from './api';
import TerminalScreen from './components/TerminalScreen.vue';

// An agent's terminal window: one slim bar (what the terminal shows, Open interactive CLI, Stop) over the screen; attaches
// to a running batch (input off) or shows the last transcript. The effective-policy banner (sandbox, CLI, network,
// timeouts, anything loosened) is folded behind the bar's "Policy" toggle, closed by default and remembered. Closing
// only detaches.
// Embedded: the same, in the main window's terminal pane (the window's title is left alone).
const props = defineProps<{ memberId: number; api?: YawbleApi; pollMs?: number; embedded?: boolean }>();
const emit = defineEmits<{ banner: [TerminalBanner] }>();
const api = resolveApi(props.api);
/** The pane's header on a phone (TerminalPane): the bar goes into its ⋯ menu. */
const pane = inject(PaneHeaderKey, null);
const inMenu = computed(() => !!props.embedded && !!pane?.on);

const banner = ref<TerminalBanner | null>(null);
const session = ref<TerminalSessionInfo | null>(null);
const error = ref<string | null>(null);
const view = ref<InstanceType<typeof TerminalScreen> | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;

const DetailsKey = 'yawble.terminal.details';
/** The policy banner is shown: off unless the person opened it (remembered in this app's storage, a convenience only). */
const showDetails = ref(readDetails());
function readDetails(): boolean { try { return localStorage.getItem(DetailsKey) === '1'; } catch { return false; } }
function toggleDetails() {
  showDetails.value = !showDetails.value;
  try { if (showDetails.value) localStorage.setItem(DetailsKey, '1'); else localStorage.removeItem(DetailsKey); } catch { /* storage refused */ }
}

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
    <!-- In the main window's pane on a phone (B057) the bar is the pane header's ⋯ menu; anywhere else it stays here. -->
    <Teleport defer :key="inMenu ? 'menu' : 'here'" :to="inMenu ? `#${pane!.more}` : 'body'" :disabled="!inMenu">
    <div class="bar row" :class="{ 'in-menu': inMenu }">
      <LoadingNote v-if="!banner && !error" class="grow" text="Loading the terminal…" />
      <span v-else class="grow state" data-test="terminal-state">{{ stateLine }}</span>
      <span v-if="error && !inMenu" class="error" data-test="terminal-error">{{ error }}</span>
      <button v-if="banner && banner.state === 'idle' && !live" data-test="open-interactive" :disabled="!!doing" :aria-busy="doing === 'open'" @click="openInteractive"><BusyLabel :busy="doing === 'open'" text="Opening…">Open interactive CLI</BusyLabel></button>
      <button v-if="banner" class="toggle" :class="{ on: showDetails, warn: banner.loosened.length }" data-test="terminal-details-toggle"
        :aria-expanded="showDetails" :title="showDetails ? 'Hide this agent\'s sandbox policy' : 'Show this agent\'s sandbox policy'"
        @click="toggleDetails">{{ banner.loosened.length ? '⚠ ' : '' }}Policy {{ showDetails ? '▴' : '▾' }}</button>
      <button data-test="terminal-stop" :disabled="!(banner?.state === 'running' || live) || !!doing" :aria-busy="doing === 'stop'" @click="stop"><BusyLabel :busy="doing === 'stop'" text="Stopping…">Stop ■</BusyLabel></button>
    </div>
    </Teleport>
    <p v-if="error && inMenu" class="error phone-error" data-test="terminal-error">{{ error }}</p>
    <header v-show="showDetails" class="banner" data-test="banner">
      <div class="title mono" data-test="banner-title">{{ banner?.title ?? 'Sandbox terminal' }}</div>
      <div class="details mono" data-test="banner-details">{{ banner?.details.join(' · ') }}</div>
      <div v-if="banner?.loosened.length" class="loosened mono" data-test="banner-loosened">loosened: {{ banner.loosened.join('; ') }}</div>
    </header>
    <div class="screen">
      <TerminalScreen ref="view" :key="memberId" :channel="`member:${memberId}`" :api="api" @session="session = $event" />
    </div>
  </div>
</template>

<style scoped>
.term-window { display: flex; flex-direction: column; height: 100vh; background: var(--term-bg); color: var(--term-fg); }
.term-window.embedded { height: 100%; }
.banner { padding: 6px 12px; background: var(--term-head); border-bottom: 1px solid var(--term-line); }
.title { font-size: 13px; color: var(--term-title); }
.details { font-size: 12px; color: var(--term-muted); margin-top: 2px; }
.loosened { font-size: 12px; color: var(--term-warn); margin-top: 2px; }
.bar { padding: 4px 12px; font-size: 12px; background: var(--term-bar); color: var(--term-muted); flex-wrap: nowrap; }
.state { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bar button { white-space: nowrap; flex: none; }
.toggle.on { background: var(--term-btn-on); }
.toggle.warn { color: var(--term-warn); }
.bar button { background: var(--term-btn); color: var(--term-btn-ink); border-color: var(--term-btn-line); }
.screen { flex: 1; min-height: 0; }
.error { color: var(--term-bad); }
/* A phone, in the pane's ⋯ menu (B057): the state line over the actions, each a 44 px row. */
@media (max-width: 759.98px) {
  .bar.in-menu { flex-direction: column; align-items: stretch; gap: 4px; padding: 0; background: transparent; font-size: 13px; }
  .bar.in-menu .state { white-space: normal; padding: 4px 6px; }
  .bar.in-menu button { min-height: 44px; }
  .phone-error { margin: 0; padding: 4px 12px; font-size: 12px; background: var(--term-bar); }
}
</style>
