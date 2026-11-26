<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue';
import { usePoll } from './usePoll';
import { PaneHeaderKey } from './terminalPane';
import LoadingNote from './components/LoadingNote.vue';
import BusyLabel from './components/BusyLabel.vue';
import { isStoppedRun, type YawbleApi, type TerminalBanner, type TerminalSessionInfo } from '../../shared/api';
import { errorText, resolveApi } from './api';
import TerminalScreen from './components/TerminalScreen.vue';

// An agent's terminal window: one slim bar (what the terminal shows, Open interactive CLI, Stop) over the screen. It
// attaches to a running batch (input off) or shows the last transcript; the effective policy is behind the bar's
// "Policy" toggle. Closing only detaches.
// embedded: the same, in the main window's terminal pane. inPane: inside the member's pane on the team page: only the
// screen, never interactive (Open interactive CLI opens this window; Stop is in the pane's menu).
const props = defineProps<{ memberId: number; api?: YawbleApi; pollMs?: number; embedded?: boolean; inPane?: boolean }>();
const emit = defineEmits<{ banner: [TerminalBanner]; live: [boolean] }>();
const api = resolveApi(props.api);
/** The pane's header on a phone (the member's full-screen terminal in MemberPane): the bar goes into its ⋯ menu. */
const pane = inject(PaneHeaderKey, null);
const inMenu = computed(() => !!props.embedded && !!pane?.on);
/** Stop is the bar's own, except in a member's pane at desktop width (its menu has it); on a phone that menu is under
 *  the full-screen terminal, so Stop is in the ⋯ menu. Open interactive CLI is never in a member's pane. */
const ownActions = computed(() => !props.inPane || inMenu.value);
/** The bar: anywhere but a member's pane, where only the phone's ⋯ menu has it. */
const hasBar = computed(() => !props.inPane || inMenu.value);

const banner = ref<TerminalBanner | null>(null);
const session = ref<TerminalSessionInfo | null>(null);
const error = ref<string | null>(null);
const view = ref<InstanceType<typeof TerminalScreen> | null>(null);

const DetailsKey = 'yawble.terminal.details';
/** The policy banner is shown: off unless the person opened it (remembered in this app's storage, a convenience only). */
const showDetails = ref(readDetails());
function readDetails(): boolean { try { return localStorage.getItem(DetailsKey) === '1'; } catch { return false; } }
function toggleDetails() {
  showDetails.value = !showDetails.value;
  try { if (showDetails.value) localStorage.setItem(DetailsKey, '1'); else localStorage.removeItem(DetailsKey); } catch { /* storage refused */ }
}

const live = computed(() => !!session.value && !session.value.ended);
watch(live, (v) => emit('live', v));
/** The state line's fields, ' · ' between them; an empty field (no command known yet) gets no separator of its own. */
const joinFields = (...parts: (string | null | undefined)[]) => parts.map((p) => (p ?? '').trim()).filter(Boolean).join(' · ');
const stateLine = computed(() => {
  const b = banner.value;
  const s = session.value;
  if (!b) return '';
  if (s && !s.ended && s.kind === 'interactive') return joinFields(['Interactive', s.command, 'session'].filter(Boolean).join(' '), 'input on');
  if (b.state === 'running') return joinFields(`Run ${b.runId ?? ''}`, s?.command, 'input off during headless run');
  if (b.state === 'queued') return `Queued · position ${b.queuePosition} of ${b.queueLength} · waiting for a global run slot`;
  // A run the person stopped (recorded failed, class "stopped") reads as stopped, not failed.
  const stopped = !!b.lastRunState && b.lastRunState !== 'completed' && b.lastRunState !== 'interrupted' && isStoppedRun({ state: b.lastRunState, failureClass: b.lastRunFailureClass });
  const last = stopped ? ' · last run stopped by you' : b.lastRunState ? ` · last run ${b.lastRunState}${b.lastRunReason ? `: ${b.lastRunReason}` : ''}` : '';
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
    return false;
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

usePoll(refresh, props.pollMs ?? 1000);
/** The terminal's fitted size (cols, rows), for a PTY started from outside (the member pane's Open interactive CLI). */
defineExpose({ size: () => view.value?.size() ?? null });
</script>

<template>
  <div class="term-window" :class="{ embedded, 'in-pane': inPane }">
    <!-- In the main window's pane on a phone the bar is the pane header's ⋯ menu; anywhere else it stays here. -->
    <Teleport v-if="hasBar" defer :key="inMenu ? 'menu' : 'here'" :to="inMenu ? `#${pane!.more}` : 'body'" :disabled="!inMenu">
    <div class="bar row" :class="{ 'in-menu': inMenu }">
      <LoadingNote v-if="!banner && !error" class="grow" text="Loading the terminal…" />
      <span v-else class="grow state" data-test="terminal-state">{{ stateLine }}</span>
      <span v-if="error && !inMenu" class="error" data-test="terminal-error">{{ error }}</span>
      <button v-if="!inPane && banner && banner.state === 'idle' && !live" data-test="open-interactive" :disabled="!!doing" :aria-busy="doing === 'open'" @click="openInteractive"><BusyLabel :busy="doing === 'open'" text="Opening…">Open interactive CLI</BusyLabel></button>
      <button v-if="banner && !inPane" class="toggle" :class="{ on: showDetails, warn: banner.loosened.length }" data-test="terminal-details-toggle"
        :aria-expanded="showDetails" :title="showDetails ? 'Hide this agent\'s sandbox policy' : 'Show this agent\'s sandbox policy'"
        @click="toggleDetails">{{ banner.loosened.length ? '⚠ ' : '' }}Policy {{ showDetails ? '▴' : '▾' }}</button>
      <button v-if="ownActions" data-test="terminal-stop" :disabled="!(banner?.state === 'running' || live) || !!doing" :aria-busy="doing === 'stop'" @click="stop"><BusyLabel :busy="doing === 'stop'" text="Stopping…">Stop ■</BusyLabel></button>
    </div>
    </Teleport>
    <p v-if="error && (inMenu || !hasBar)" class="error phone-error" data-test="terminal-error">{{ error }}</p>
    <!-- On a phone (opened from the pane's ⋯) the strip is capped and scrolls on its own, with a ✕ to hide it (O2). -->
    <header v-if="!inPane" v-show="showDetails" class="banner" :class="{ 'in-menu': inMenu }" data-test="banner">
      <div class="banner-text" data-test="banner-text">
        <div class="title mono" data-test="banner-title">{{ banner?.title ?? 'Sandbox terminal' }}</div>
        <div class="details mono" data-test="banner-details">{{ banner?.details.join(' · ') }}</div>
        <div v-if="banner?.loosened.length" class="loosened mono" data-test="banner-loosened">loosened: {{ banner.loosened.join('; ') }}</div>
      </div>
      <button v-if="inMenu" class="banner-close" data-test="banner-close" title="Hide this agent's sandbox policy" aria-label="Hide the policy" @click="toggleDetails">✕</button>
    </header>
    <div class="screen">
      <TerminalScreen ref="view" :key="memberId" :channel="`member:${memberId}`" :api="api" :formatted-only="inPane" @session="session = $event" />
    </div>
  </div>
</template>

<style scoped>
.term-window { display: flex; flex-direction: column; height: 100vh; height: 100dvh; background: var(--term-bg); color: var(--term-fg); }
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
.phone-error { margin: 0; padding: 4px 12px; font-size: 12px; background: var(--term-bar); }
/* A phone, in the pane's ⋯ menu: the state line over the actions, each a 44 px row. */
@media (max-width: 759.98px) {
  .bar.in-menu { flex-direction: column; align-items: stretch; gap: 4px; padding: 0; background: transparent; font-size: 13px; }
  .bar.in-menu .state { white-space: normal; padding: 4px 6px; }
  .bar.in-menu button { min-height: 44px; }
  /* The Policy strip opened from ⋯ (O2): at most 20% of the screen, so with the 44 px header the transcript keeps at
     least 60% (375x521: 104 px; 667x263: 53 px); its text scrolls inside it and the 44 px ✕ stays put. */
  .banner.in-menu { display: flex; align-items: flex-start; gap: 4px; max-height: 20vh; padding: 0 0 0 12px; box-sizing: border-box; flex: none; overflow: hidden; }
  .banner.in-menu .banner-text { flex: 1; min-width: 0; max-height: 20vh; overflow-y: auto; padding: 6px 0; box-sizing: border-box; overscroll-behavior: contain; }
  .banner.in-menu .banner-close { flex: none; min-width: 44px; min-height: 44px; padding: 0; font-size: 16px; background: transparent; color: var(--term-fg); border: 0; }
  /* In a member's pane: the bar and the Formatted / Raw tabs are one 44 px row each; the screen takes the rest. */
  .in-pane .bar { height: 44px; box-sizing: border-box; padding: 0 4px 0 10px; }
  .in-pane .bar button { min-height: 44px; font-size: 14px; }
  .in-pane :deep(.modes) { height: 44px; box-sizing: border-box; padding: 0 4px; align-items: center; }
  .in-pane :deep(.modes button) { min-height: 40px; padding: 0 12px; font-size: 14px; }
  .in-pane :deep(.screen-wrap) { min-height: 0; }
}
</style>
