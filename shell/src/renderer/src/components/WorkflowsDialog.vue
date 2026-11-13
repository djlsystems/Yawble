<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import type { YawbleApi, TeamSummary, WorkflowSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { ago } from '../board/cards';
import { displayName } from '../faces/faces';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/**
 * A team's workflows, newest activity first: open ones (and who is working them, or that nobody is), completed and
 * closed ones. On an open one the person can nudge it (its Manager is told to pick it up), stop what runs in it, or
 * close it. Everything is also on the bus, so the Board and the Monitor follow.
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

async function load() {
  try {
    list.value = await api.call<WorkflowSummary[]>('bus.workflows', { teamId: props.team.id, limit: 50 });
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
  } catch (e) { error.value = errorText(e); } finally { busy.value = null; busyMethod.value = null; }
}

const confirmAct = () => {
  const a = acting.value!;
  void run(a.id, a.kind === 'nudge' ? 'bus.nudgeWorkflow' : 'bus.closeWorkflow', a.kind === 'nudge' ? { note: a.text || undefined } : { reason: a.text || undefined });
};

const stateLabel = (w: WorkflowSummary) => w.state === 'open' ? (w.undeclared ? 'Nobody is working it' : 'Open') : w.state === 'completed' ? 'Completed' : 'Closed';

onMounted(async () => {
  timer = setInterval(load, props.pollMs ?? 2000);
  await load();
  // Opened at a workflow (from the Monitor): scroll it into view and pulse it once.
  if (props.focus != null && list.value?.some((w) => w.id === props.focus)) {
    const id = props.focus;
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
          #{{ w.id }} · {{ w.owner ? displayName(w.owner) : 'the Manager' }} · {{ w.messages }} message{{ w.messages === 1 ? '' : 's' }} · started {{ ago(w.startedAt) }} ·
          last {{ ago(w.lastAt) }}
        </div>
        <div v-if="w.busy.length" class="busy">{{ w.busy.join(' · ') }}</div>
        <div v-if="w.ending" class="ending">{{ w.state === 'closed' ? 'Closed' : 'Delivered' }}: {{ w.ending }}</div>
        <div v-if="w.state === 'open'" class="actions">
          <template v-if="acting?.id === w.id">
            <input v-model="acting.text" class="grow" :placeholder="acting.kind === 'nudge' ? 'A note for the Manager (optional)' : 'Why (optional)'"
              data-test="workflow-text" @keydown.enter="confirmAct" />
            <button class="primary" :disabled="busy === w.id" data-test="workflow-confirm" @click="confirmAct"><BusyLabel :busy="busy === w.id" :text="acting.kind === 'nudge' ? 'Nudging…' : 'Closing…'">{{ acting.kind === 'nudge' ? 'Nudge' : 'Close it' }}</BusyLabel></button>
            <button :disabled="busy === w.id" @click="acting = null">Cancel</button>
          </template>
          <template v-else>
            <button :disabled="busy === w.id" data-test="workflow-nudge" title="Tell the Manager to pick this up" @click="acting = { id: w.id, kind: 'nudge', text: '' }">Nudge</button>
            <button v-if="w.busy.some((b) => b.endsWith('is running'))" :disabled="busy === w.id" data-test="workflow-stop" title="Stop the runs working on it now"
              @click="run(w.id, 'bus.stopWorkflow', {})"><BusyLabel :busy="doing(w.id, 'bus.stopWorkflow')" text="Stopping…">Stop runs</BusyLabel></button>
            <button :disabled="busy === w.id" data-test="workflow-close" title="End it without declaring it complete" @click="acting = { id: w.id, kind: 'close', text: '' }">Close</button>
          </template>
        </div>
      </li>
    </ol>
  </Modal>
</template>

<style scoped>
.intro { margin: 0 0 10px; font-size: 13px; }
.wfs { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
.wf { border: 1px solid var(--line); border-left: 4px solid var(--state-running, #2457c5); border-radius: 8px; padding: 8px 10px; background: #fff; }
.wf.completed { border-left-color: var(--ok); opacity: .85; }
.wf.closed { border-left-color: var(--muted); opacity: .8; }
.wf.idle { border-left-color: var(--warn); background: #fffaf0; }
.top { display: flex; gap: 8px; align-items: baseline; }
.chip { font-size: 11px; font-weight: 700; border: 1px solid var(--line); border-radius: 10px; padding: 0 7px; white-space: nowrap; }
.wf.idle .chip { color: var(--warn); border-color: #e9d29a; }
/* The workflow you are on (clicked here, or on the Monitor): an accent outline round the card, with a short pulse on arrival. */
.wf { cursor: default; }
.wf.sel { opacity: 1; box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 55%, transparent); background: color-mix(in srgb, var(--accent) 6%, #fff); }
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
.primary { background: var(--accent); color: #fff; border-color: var(--accent); }
.muted { color: var(--muted); }
.error { color: var(--bad); }
</style>
