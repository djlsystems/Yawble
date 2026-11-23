<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, provide, reactive, ref, useId, watch } from 'vue';
import type { DetectedCli, YawbleApi, TerminalSessionInfo } from '../../../shared/api';
import BusyLabel from './BusyLabel.vue';
import { resolveApi } from '../api';
import { usePhone } from '../phone';
import { AGENT_SESSION_PREFIX, closeTerminalPane, isAgentSession, PaneHeaderKey, setTerminalPaneWidth, terminalPane } from '../terminalPane';
import TerminalScreen from './TerminalScreen.vue';

// The terminal pane on the right of the main window: the one channel terminal shown (an Agents Terminal / Test
// session), a drag handle on its left edge to resize it, and a close button. A member's terminal is never shown here:
// it is in that member's pane on the team page (memberTerminal.ts). pollMs is accepted for App's binding.
const props = defineProps<{ api?: YawbleApi; pollMs?: number }>();

const session = ref<TerminalSessionInfo | null>(null);
// The shown session ended after this pane saw it live: there is nothing left to stop. (A channel's previous run stays
// on it, ended, until the next one begins, so an ended session seen first may be a newer run still starting: its stop,
// which names that run, is still sent and the core keeps it until the sandbox begins.)
const sawLive = ref(false);
const finished = ref(false);
function onSession(s: TerminalSessionInfo | null) {
  session.value = s;
  if (s && !s.ended) sawLive.value = true;
  finished.value = sawLive.value && !!s?.ended;
}
const closeTitle = computed(() => {
  const t = terminalPane.target;
  return t && isAgentSession(t.channel) ? 'Stop this session and close the terminal' : 'Close';
});
watch(() => terminalPane.target, () => { session.value = null; sawLive.value = false; finished.value = false; });
// A phone (B057): the pane is the whole screen with one header row - the title, the shown terminal's Formatted / Raw
// tabs, a ⋯ for the rest (the session's Stop) and ✕ - and the terminal fills the rest.
const phone = usePhone();
const uid = useId();
const slots = reactive({ get on() { return phone.value; }, tabs: `terminal-pane-tabs-${uid}`, more: `terminal-pane-more-${uid}` });
provide(PaneHeaderKey, slots);
const moreOpen = ref(false);
const hasMore = computed(() => !!terminalPane.target?.stop);
watch(() => terminalPane.target, () => { moreOpen.value = false; });
// B128: Grok's Terminal / Test session (agent-test:grok) while Grok is not signed in shows why above the screen, so
// grok's own "No models are available" is not the only clue. The status is the Settings > Agents tile's (agents.detect,
// DetectedCli.signIn); it is asked again when the target changes, when the window gets focus and every few seconds
// while it reads not signed in, and the line goes away as soon as it reads anything else. A failed ask shows nothing.
const SIGN_IN_CLIS = new Set(['grok']);
const SIGN_IN_POLL_MS = 5000;
const signInCli = computed(() => {
  const t = terminalPane.target;
  if (!t || !isAgentSession(t.channel)) return null;
  const cli = t.channel.slice(AGENT_SESSION_PREFIX.length);
  return SIGN_IN_CLIS.has(cli) ? cli : null;
});
// The core's detail (SignInCatalog) already begins with the heading the banner shows and may end "; then reopen this
// dialog" (for the Settings > Agents tile). The pane re-checks by itself, so both are dropped here; the tile keeps them.
const SIGN_IN_HEADING = /^\s*Not signed in: run `?grok`? in your own terminal and sign in\s*(?:\.\s*|$)/i;
function paneDetail(detail: string | null | undefined): string | null {
  if (!detail) return null;
  let d = detail.replace(SIGN_IN_HEADING, '').trim();
  d = d.replace(/[;,]?\s*then reopen this dialog\s*\.?\s*$/i, '').trim();
  if (!d) return null;
  return /[.!?]$/.test(d) ? d : `${d}.`;
}
const notSignedIn = ref<{ detail: string | null } | null>(null);
let signInAsked = 0;
let signInTimer: ReturnType<typeof setTimeout> | undefined;
async function checkSignIn() {
  clearTimeout(signInTimer);
  const cli = signInCli.value;
  const api = resolveApi(props.api);
  if (!cli || !api) { notSignedIn.value = null; return; }
  const asked = ++signInAsked; // a reply to an older ask (another target) is dropped
  try {
    const found = (await api.call<DetectedCli[]>('agents.detect')).find((c) => c.cliId === cli);
    if (asked !== signInAsked) return;
    notSignedIn.value = found?.found && found.signIn?.status === 'not-signed-in' ? { detail: paneDetail(found.signIn.detail) } : null;
  } catch {
    if (asked !== signInAsked) return;
    notSignedIn.value = null;
  }
  if (notSignedIn.value) signInTimer = setTimeout(() => { void checkSignIn(); }, SIGN_IN_POLL_MS);
}
watch(signInCli, () => { notSignedIn.value = null; void checkSignIn(); }, { immediate: true });
function onFocus() { if (signInCli.value) void checkSignIn(); }
onMounted(() => window.addEventListener('focus', onFocus));
onBeforeUnmount(() => { signInAsked++; clearTimeout(signInTimer); window.removeEventListener('focus', onFocus); });
let dragging = false;
function startDrag(e: PointerEvent) {
  dragging = true;
  (e.target as HTMLElement).setPointerCapture(e.pointerId);
}
function drag(e: PointerEvent) {
  if (dragging) setTerminalPaneWidth(window.innerWidth - e.clientX);
}
function endDrag() { dragging = false; }
/** Stop / ✕ clicked and the session's stop has not answered yet: held, with a spinner. */
const stopping = ref<'stop' | 'close' | null>(null);
async function stop() {
  const t = terminalPane.target;
  if (!t || stopping.value) return;
  stopping.value = 'stop';
  try { await t.stop?.(); } finally { stopping.value = null; }
}
/** ✕: an agent's Terminal / Test session (agent-test:<cli>) is stopped, then the pane closes; another channel is only
 *  hidden. */
async function close() {
  const t = terminalPane.target;
  if (stopping.value) return;
  try {
    if (t && isAgentSession(t.channel) && !finished.value) { stopping.value = 'close'; await t.stop?.(); }
  } catch { /* an agent session reports its own failure (Settings > Agents); the pane closes either way */ } finally { stopping.value = null; }
  closeTerminalPane();
}
</script>

<template>
  <aside class="pane" :class="{ phone }" :style="phone ? undefined : { width: terminalPane.width + 'px' }" data-test="terminal-pane">
    <div v-if="!phone" class="handle" title="Drag to resize" @pointerdown="startDrag" @pointermove="drag" @pointerup="endDrag" @pointercancel="endDrag"></div>
    <header class="head" data-test="terminal-pane-head">
      <span class="title" data-test="terminal-pane-title">{{ terminalPane.target?.title ?? 'Terminal' }}</span>
      <template v-if="phone">
        <!-- The shown terminal's Formatted / Raw tabs come here (TerminalScreen: PaneHeaderKey), its Stop into the ⋯ menu. -->
        <span :id="slots.tabs" class="tabs-slot" data-test="terminal-pane-tabs"></span>
        <button v-if="hasMore" class="mini more-toggle" :class="{ on: moreOpen }" aria-label="More" :aria-expanded="moreOpen" data-test="terminal-pane-more"
          @click="moreOpen = !moreOpen">⋯</button>
        <div v-show="moreOpen" :id="slots.more" class="more-menu" data-test="terminal-pane-menu" @click="moreOpen = false">
          <button v-if="terminalPane.target?.stop" class="mini" data-test="terminal-pane-stop"
            :disabled="finished || !!stopping" :aria-busy="stopping === 'stop'" @click="stop"><BusyLabel :busy="stopping === 'stop'" text="Stopping…">Stop ■</BusyLabel></button>
        </div>
      </template>
      <button v-else-if="terminalPane.target?.stop" class="mini" data-test="terminal-pane-stop"
        :disabled="finished || !!stopping" :aria-busy="stopping === 'stop'" @click="stop"><BusyLabel :busy="stopping === 'stop'" text="Stopping…">Stop ■</BusyLabel></button>
      <button class="mini" :title="closeTitle" data-test="terminal-pane-close" :disabled="!!stopping" :aria-busy="stopping === 'close'" @click="close"><BusyLabel :busy="stopping === 'close'">✕</BusyLabel></button>
    </header>
    <div v-if="notSignedIn" class="signin" role="status" data-test="terminal-pane-signin">
      <b>Not signed in: run <code>grok</code> in your own terminal and sign in</b>
      <span v-if="notSignedIn.detail" class="detail" data-test="terminal-pane-signin-detail">{{ notSignedIn.detail }}</span>
    </div>
    <div class="body">
      <TerminalScreen v-if="terminalPane.target" :key="terminalPane.target.channel"
        :channel="terminalPane.target.channel" :api="api" @session="onSession" />
      <p v-else class="empty" data-test="terminal-pane-empty">No terminal open. Settings → Agents → Terminal or Test shows one here; an agent's
        terminal (<code>&gt;_</code>) opens in its pane on the team page.</p>
    </div>
  </aside>
</template>

<style scoped>
.pane { position: relative; display: flex; flex-direction: column; min-width: 320px; max-width: 75vw; height: 100%; background: var(--term-bg); border-left: 1px solid var(--line); }
.handle { position: absolute; left: -3px; top: 0; bottom: 0; width: 6px; cursor: col-resize; z-index: 2; }
.handle:hover { background: var(--term-grip); }
.head { display: flex; align-items: center; gap: 6px; padding: 6px 10px; background: var(--term-head); color: var(--term-fg); border-bottom: 1px solid var(--term-line); }
.title { flex: 1; font-size: 12.5px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.mini { font-size: 12px; padding: 2px 8px; background: var(--term-btn); color: var(--term-btn-ink); border: 1px solid var(--term-btn-line); border-radius: 6px; }
.mini:disabled { opacity: .5; }
.body { flex: 1; min-height: 0; }
.signin { flex: none; display: flex; flex-direction: column; gap: 2px; padding: 6px 10px; font-size: 12.5px; background: var(--warn-soft); color: var(--warn-ink); border-bottom: 1px solid var(--warn-line); }
.signin code { background: var(--warn-chip); padding: 0 4px; border-radius: 3px; }
.signin .detail { font-size: 12px; opacity: .85; }
.empty { color: var(--term-muted); padding: 16px; font-size: 13px; }
.empty code { background: var(--term-btn-on); padding: 0 4px; border-radius: 3px; }
/* A phone (B057): the whole screen over the page (no side pane crushing the team column), one 44 px header row, the
   terminal filling the rest and never past the bottom (dvh: what the browser really shows). */
@media (max-width: 759.98px) {
  .pane.phone { position: fixed; inset: 0; z-index: 9; width: auto; min-width: 0; max-width: none; height: 100dvh; border-left: 0; overflow: hidden; }
  .pane.phone .head { position: relative; flex: none; height: 44px; box-sizing: content-box; gap: 2px;
    padding: env(safe-area-inset-top, 0px) calc(2px + env(safe-area-inset-right, 0px)) 0 calc(10px + env(safe-area-inset-left, 0px)); }
  .pane.phone .title { min-width: 0; font-size: 14px; }
  .pane.phone .head > .mini { min-width: 44px; height: 44px; flex: none; padding: 0 8px; border-color: transparent; background: transparent; font-size: 16px; }
  .pane.phone .head > .mini.on { background: var(--term-btn-on); }
  .tabs-slot { display: flex; flex: none; }
  .more-menu { position: absolute; right: 4px; top: calc(100% + 2px); z-index: 3; display: flex; flex-direction: column; align-items: stretch; gap: 4px;
    max-width: min(320px, calc(100vw - 8px)); padding: 6px; background: var(--term-head); border: 1px solid var(--term-line); border-radius: 8px; box-shadow: 0 6px 20px var(--shadow); }
  .more-menu .mini { min-height: 44px; font-size: 14px; }
  .pane.phone .body { flex: 1 1 0; min-height: 0; overflow: hidden; }
}
</style>
