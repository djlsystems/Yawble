<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { YawbleApi, TerminalBanner, TerminalSessionInfo } from '../../../shared/api';
import TerminalApp from '../TerminalApp.vue';
import BusyLabel from './BusyLabel.vue';
import { errorText, resolveApi } from '../api';
import { closeTerminalPane, isAgentSession, setTerminalPaneWidth, terminalPane } from '../terminalPane';
import TerminalScreen from './TerminalScreen.vue';

// The terminal pane on the right of the main window: the one terminal shown (an agent's, or an Agents test session),
// a drag handle on its left edge to resize it, and a close button. Empty until a terminal button picks one.
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
// The member terminal's last banner (TerminalApp polls it): says whether the member holds an INTERACTIVE session.
const banner = ref<TerminalBanner | null>(null);
const endsSession = computed(() => terminalPane.target?.kind === 'member' && !!banner.value?.interactive);
const closeTitle = computed(() => {
  const t = terminalPane.target;
  if (t?.kind === 'channel' && isAgentSession(t.channel)) return 'Stop this session and close the terminal';
  if (t?.kind === 'member' && endsSession.value) return `End the session and close — queued work for ${t.title.split(' · ')[0]} resumes`;
  return 'Close';
});
watch(() => terminalPane.target, () => { banner.value = null; session.value = null; sawLive.value = false; finished.value = false; });
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
  if (t?.kind !== 'channel' || stopping.value) return;
  stopping.value = 'stop';
  try { await t.stop?.(); } finally { stopping.value = null; }
}
/** ✕: an agent's Terminal / Test session (agent-test:<cli>) is stopped, then the pane closes; a member's terminal is
 *  only hidden. (Ctrl+` / View → Terminal only hides the pane, so a session can be hidden on purpose and shown again.) */
async function close() {
  const t = terminalPane.target;
  if (stopping.value) return;
  terminalPane.stopError = null;
  try {
    if (t?.kind === 'channel' && isAgentSession(t.channel) && !finished.value) { stopping.value = 'close'; await t.stop?.(); }
    else if (t?.kind === 'member' && endsSession.value) {
      // Ask the core again just before: a stale banner must never end a headless run that began since.
      stopping.value = 'close';
      const fresh = await resolveApi(props.api).call<TerminalBanner>('terminal.banner', { memberId: t.memberId });
      if (fresh.interactive) await resolveApi(props.api).call('terminal.stop', { memberId: t.memberId });
    }
  } catch (e) {
    // An agent session reports its own failure (Settings > Agents); a member's is shown here. The pane closes either way.
    if (t?.kind === 'member') terminalPane.stopError = `Could not end the session: ${errorText(e)}`;
  } finally { stopping.value = null; }
  closeTerminalPane();
}
</script>

<template>
  <aside class="pane" :style="{ width: terminalPane.width + 'px' }" data-test="terminal-pane">
    <div class="handle" title="Drag to resize" @pointerdown="startDrag" @pointermove="drag" @pointerup="endDrag" @pointercancel="endDrag"></div>
    <header class="head">
      <span class="title" data-test="terminal-pane-title">{{ terminalPane.target?.title ?? 'Terminal' }}</span>
      <button v-if="terminalPane.target?.kind === 'channel' && terminalPane.target.stop" class="mini" data-test="terminal-pane-stop"
        :disabled="finished || !!stopping" :aria-busy="stopping === 'stop'" @click="stop"><BusyLabel :busy="stopping === 'stop'" text="Stopping…">Stop ■</BusyLabel></button>
      <button class="mini" :title="closeTitle" data-test="terminal-pane-close" :disabled="!!stopping" :aria-busy="stopping === 'close'" @click="close"><BusyLabel :busy="stopping === 'close'">✕</BusyLabel></button>
    </header>
    <div class="body">
      <TerminalApp v-if="terminalPane.target?.kind === 'member'" :key="`m${terminalPane.target.memberId}`" embedded
        :member-id="terminalPane.target.memberId" :api="api" :poll-ms="pollMs" @banner="banner = $event" />
      <TerminalScreen v-else-if="terminalPane.target?.kind === 'channel'" :key="terminalPane.target.channel"
        :channel="terminalPane.target.channel" :api="api" @session="onSession" />
      <p v-else class="empty">No terminal open. Click a terminal button (an agent's <code>&gt;_</code>, or Settings → Agents → Test) to show one here.</p>
    </div>
  </aside>
</template>

<style scoped>
.pane { position: relative; display: flex; flex-direction: column; min-width: 320px; max-width: 75vw; height: 100%; background: #101418; border-left: 1px solid var(--line); }
.handle { position: absolute; left: -3px; top: 0; bottom: 0; width: 6px; cursor: col-resize; z-index: 2; }
.handle:hover { background: rgba(80, 120, 200, .25); }
.head { display: flex; align-items: center; gap: 6px; padding: 6px 10px; background: #1b222c; color: #d8dee9; border-bottom: 1px solid #2c3542; }
.title { flex: 1; font-size: 12.5px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.mini { font-size: 12px; padding: 2px 8px; background: #222b36; color: #d8dee9; border: 1px solid #36404e; border-radius: 6px; }
.mini:disabled { opacity: .5; }
.body { flex: 1; min-height: 0; }
.empty { color: #8a95a7; padding: 16px; font-size: 13px; }
.empty code { background: #222b36; padding: 0 4px; border-radius: 3px; }
</style>
