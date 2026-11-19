<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import type { YawbleApi, TeamSummary, WorkflowSummary } from '../../../shared/api';
import { WORKFLOW_EXTEND } from '../../../shared/api';
import { expiresSoon, extendErrorText, until } from '../workflowExpiry';
import { errorText, resolveApi } from '../api';
import { ago } from '../board/cards';
import { displayName } from '../faces/faces';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import CommitText from './CommitText.vue';
import { vChange } from '../remoteAccess';
import OverallTime from '../timeline/OverallTime.vue';
import { hasTime } from '../timeline/time';

/**
 * A team's workflows, newest activity first: open ones (and who is working them, or that nobody is), completed,
 * closed and expired ones (B085: closed by the app after the team's idle days; their time is their last activity, endedAt). On an open one the person can nudge it (its Manager is told to pick it up), stop what runs in it, or
 * close it (v-change: disabled in a read-only Visitor view, card 305; the core refuses them anyway). Everything is also on the bus, so the Board and the Monitor follow.
 */
// focus: a workflow to show selected when the list opens (a workflow clicked on the Monitor).
const props = defineProps<{ team: TeamSummary; api?: YawbleApi; pollMs?: number; focus?: number | null }>();
const emit = defineEmits<{ close: [] }>();
const api = resolveApi(props.api);

const list = ref<WorkflowSummary[] | null>(null);
const selected = ref<number | null>(props.focus ?? null);
const flash = ref<number | null>(null);
const error = ref<string | null>(null);
const busy = ref<number | null>(null);
const busyMethod = ref<string | null>(null);
const doing = (id: number, method: string) => busy.value === id && busyMethod.value === method;
const acting = ref<{ id: number; kind: 'nudge' | 'close'; text: string } | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;

// The newest 50 are listed; opened at a workflow that is older than those, the newest 200 (core's most) instead, so
// it can be shown. Past that the focus workflow is named as not found, not silently left out.
const ListLimit = 50;
const FocusLimit = 200;
const limit = ref(ListLimit);
const missing = ref<number | null>(null);

async function load() {
  try {
    list.value = await api.call<WorkflowSummary[]>('bus.workflows', { teamId: props.team.id, limit: limit.value });
    error.value = null;
  } catch (e) { error.value = errorText(e); }
}

async function run(id: number, method: string, params: Record<string, unknown>) {
  if (busy.value !== null) return;
  busy.value = id;
  busyMethod.value = method;
  error.value = null;
  try {
    await api.call(method, { teamId: props.team.id, workflow: id, ...params });
    acting.value = null;
    await load();
  } catch (e) {
    // already_ended: it ended meanwhile; said as such (the next poll shows how it ended).
    error.value = method === WORKFLOW_EXTEND ? extendErrorText(e, id) : errorText(e);
  } finally { busy.value = null; busyMethod.value = null; }
}

const confirmAct = () => {
  const a = acting.value!;
  void run(a.id, a.kind === 'nudge' ? 'bus.nudgeWorkflow' : 'bus.closeWorkflow', a.kind === 'nudge' ? { note: a.text || undefined } : { reason: a.text || undefined });
};

// Expired (B085): the app closed it after the team's idle days; distinct from completed and from closed by a person.
const stateLabel = (w: WorkflowSummary) => w.state === 'open' ? (w.undeclared ? 'Nobody is working it' : 'Open')
  : w.state === 'completed' ? 'Completed' : w.state === 'expired' ? 'Expired' : 'Closed';

onMounted(async () => {
  timer = setInterval(load, props.pollMs ?? 2000);
  await load();
  // Opened at a workflow (the Monitor, a #workflow= link): scroll it into view and pulse it once. Not among the newest
  // 50 (a full page came back): ask for the newest 200 and look again; still not there, say so.
  const has = () => props.focus != null && !!list.value?.some((w) => w.id === props.focus);
  if (props.focus != null && list.value && !has() && list.value.length >= limit.value) {
    limit.value = FocusLimit;
    await load();
  }
  if (props.focus != null && list.value && !has()) missing.value = props.focus;
  if (has()) {
    const id = props.focus!;
    flash.value = id;
    setTimeout(() => { if (flash.value === id) flash.value = null; }, 1800);
    await nextTick();
    document.querySelector(`[data-wf="${id}"]`)?.scrollIntoView?.({ block: 'center' });
  }
});
onBeforeUnmount(() => { if (timer) clearInterval(timer); });
</script>

<template>
  <Modal :title="`${team.name} · workflows`" wide @close="emit('close')">
    <p class="muted intro">
      A workflow is one piece of work and everything it led to: who was told what, their reports, and how it ended. If one is open
      and nobody is working it, nudge it: its Manager is told to pick it up.
    </p>
    <p v-if="error" class="error" data-test="workflows-error">{{ error }}</p>
    <p v-if="missing !== null" class="muted" data-test="workflow-not-found">
      Workflow #{{ missing }} was not found{{ (list?.length ?? 0) >= limit ? ` among this team's newest ${limit} workflows` : ' in this team' }}.
    </p>
    <LoadingNote v-if="!list && !error" text="Loading workflows…" />
    <p v-if="list && !list.length" class="muted" data-test="workflows-empty">No workflows yet. Tell the Manager something to start one.</p>
    <ol v-if="list?.length" class="wfs" data-test="workflows">
      <li v-for="w in list" :key="w.id" class="wf" :class="[w.state, { idle: w.undeclared, sel: selected === w.id, flash: flash === w.id }]"
        :aria-current="selected === w.id" :data-wf="w.id" :data-test="`workflow-${w.id}`" @click="selected = w.id">
        <div class="top">
          <span class="chip" data-test="workflow-state">{{ stateLabel(w) }}</span>
          <span class="title" :title="w.title">{{ w.title }}</span>
        </div>
        <div class="meta muted">
          #{{ w.id }} · {{ w.owner ? displayName(w.owner) : 'the Manager' }} · {{ w.messages }} message{{ w.messages === 1 ? '' : 's' }} · started {{ ago(w.startedAt) }}<template v-if="hasTime(w.time)"> (<OverallTime :time="w.time" :target="{ kind: 'workflow', id: w.id, title: w.title, team: team.name }" />)</template> ·
          <template v-if="w.state !== 'open' && w.endedAt">ended {{ ago(w.endedAt) }}</template><template v-else>last {{ ago(w.lastAt) }}</template>
          <template v-if="w.state === 'expired' && w.expiredAt"> · expired {{ ago(w.expiredAt) }}</template>
        </div>
        <div v-if="w.busy.length" class="busy">{{ w.busy.join(' · ') }}</div>
        <div v-if="w.ending" class="ending">{{ w.state === 'closed' ? 'Closed' : w.state === 'expired' ? 'Expired' : 'Delivered' }}: <CommitText :text="w.ending" :team="team.name" /></div>
        <div v-if="expiresSoon(w)" class="ending warn" data-test="workflow-expires">Expires {{ until(w.expiresAt!) }} if nothing happens in it; Extend resets the clock.</div>
        <div v-if="w.state === 'open'" class="actions">
          <template v-if="acting?.id === w.id">
            <input v-model="acting.text" class="grow" :placeholder="acting.kind === 'nudge' ? 'A note for the Manager (optional)' : 'Why (optional)'"
              data-test="workflow-text" @keydown.enter="confirmAct" />
            <button v-change class="primary" :disabled="busy === w.id" data-test="workflow-confirm" @click="confirmAct"><BusyLabel :busy="busy === w.id" :text="acting.kind === 'nudge' ? 'Nudging…' : 'Closing…'">{{ acting.kind === 'nudge' ? 'Nudge' : 'Close it' }}</BusyLabel></button>
            <button :disabled="busy === w.id" @click="acting = null">Cancel</button>
          </template>
          <template v-else>
            <button v-change :disabled="busy === w.id" data-test="workflow-nudge" title="Tell the Manager to pick this up" @click="acting = { id: w.id, kind: 'nudge', text: '' }">Nudge</button>
            <button v-if="w.busy.some((b) => b.endsWith('is running'))" v-change :disabled="busy === w.id" data-test="workflow-stop" title="Stop the runs working on it now"
              @click="run(w.id, 'bus.stopWorkflow', {})"><BusyLabel :busy="doing(w.id, 'bus.stopWorkflow')" text="Stopping…">Stop runs</BusyLabel></button>
            <button v-if="w.expiresAt" v-change :disabled="busy === w.id" data-test="workflow-extend" title="Reset its expiry clock"
              @click="run(w.id, WORKFLOW_EXTEND, {})"><BusyLabel :busy="doing(w.id, WORKFLOW_EXTEND)" text="Extending…">Extend</BusyLabel></button>
            <button v-change :disabled="busy === w.id" data-test="workflow-close" title="End it without declaring it complete" @click="acting = { id: w.id, kind: 'close', text: '' }">Close</button>
          </template>
        </div>
      </li>
    </ol>
  </Modal>
</template>

<style scoped>
.intro { margin: 0 0 10px; font-size: 13px; }
.wfs { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
.wf { border: 1px solid var(--line); border-left: 4px solid var(--state-running); border-radius: 8px; padding: 8px 10px; background: var(--surface); }
.wf.completed { border-left-color: var(--ok); opacity: .85; }
.wf.closed, .wf.expired { border-left-color: var(--muted); opacity: .8; }
.wf.expired { border-left-style: dashed; }
.warn { color: var(--warn); }
.wf.idle { border-left-color: var(--warn); background: var(--warn-bg); }
.top { display: flex; gap: 8px; align-items: baseline; }
.chip { font-size: 11px; font-weight: 700; border: 1px solid var(--line); border-radius: 10px; padding: 0 7px; white-space: nowrap; }
.wf.idle .chip { color: var(--warn); border-color: var(--warn-line); }
/* The workflow you are on (clicked here, or on the Monitor): an accent outline round the card, with a short pulse on arrival. */
.wf { cursor: default; }
.wf.sel { opacity: 1; box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 55%, transparent); background: color-mix(in srgb, var(--accent) 6%, var(--surface)); }
.wf.flash { animation: wf-flash 1.6s ease-out 1; }
@keyframes wf-flash { 0%, 30% { box-shadow: 0 0 0 5px color-mix(in srgb, var(--accent) 45%, transparent); } 100% { box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 55%, transparent); } }
@media (prefers-reduced-motion: reduce) { .wf.flash { animation: none; } }
.title { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.meta { font-size: 12px; margin-top: 2px; }
.busy { font-size: 12.5px; color: var(--ink-soft); margin-top: 4px; }
.ending { font-size: 12.5px; color: var(--ink-soft); margin-top: 4px; white-space: pre-wrap; overflow-wrap: anywhere; }
.actions { display: flex; gap: 6px; margin-top: 6px; align-items: center; }
.actions input { font: inherit; font-size: 13px; padding: 4px 7px; border: 1px solid var(--line); border-radius: 6px; }
.grow { flex: 1; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.muted { color: var(--muted); }
.error { color: var(--bad); }
</style>
