<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { YawbleApi, WorkflowSummary } from '../../../shared/api';
import LoadingNote from './LoadingNote.vue';
import { entryTime, latestWorkflows, workflowLook, WorkflowsShown } from '../monitor/workflows';

// A team's recent workflows on its card in the Teams list: the five newest, last updated first, each with its state
// (the Monitor's colours and icons), "#id title" and the time of its last entry. A row opens the team's workflows
// with that one selected.
const props = defineProps<{ teamId: number; api: YawbleApi; pollMs?: number }>();
const emit = defineEmits<{ open: [workflow: number] }>();

const list = ref<WorkflowSummary[] | null>(null);
const failed = ref(false);
const Glyph = { completed: '✓', working: '▶', waiting: '❚❚', closed: '✕' } as const;
let timer: ReturnType<typeof setInterval> | null = null;

async function load() {
  try {
    const got = await props.api.call<WorkflowSummary[]>('bus.workflows', { teamId: props.teamId, limit: WorkflowsShown });
    list.value = latestWorkflows(Array.isArray(got) ? got : []);
    failed.value = false;
  } catch { failed.value = true; /* keeps what it showed */ }
}
const tip = (w: WorkflowSummary) =>
  `${workflowLook(w).label} · #${w.id} ${w.title}${w.busy.length ? ` · ${w.busy.join(', ')}` : w.ending ? ` · ${w.ending}` : ''}`;

onMounted(() => { void load(); timer = setInterval(load, Math.max(5000, props.pollMs ?? 5000)); });
onBeforeUnmount(() => { if (timer) clearInterval(timer); });
</script>

<template>
  <div class="twf" data-test="team-workflows">
    <div class="twf-head">Recent workflows</div>
    <LoadingNote v-if="!list && !failed" class="tiny" text="Loading workflows…" />
    <p v-else-if="!list" class="muted tiny" data-test="team-workflows-error">The workflows could not be loaded.</p>
    <p v-if="list && !list.length" class="muted tiny" data-test="team-workflows-empty">No workflows yet.</p>
    <div class="twf-list" data-test="team-workflows-list">
    <button v-for="w in list ?? []" :key="w.id" type="button" class="twf-row" :class="workflowLook(w).status"
      :data-id="w.id" :data-status="workflowLook(w).status" :title="tip(w)" data-test="team-workflow" @click="emit('open', w.id)">
      <span class="twf-icon" aria-hidden="true">{{ Glyph[workflowLook(w).status] }}</span>
      <span class="twf-title"><span class="twf-id">#{{ w.id }}</span> {{ w.title }}</span>
      <span class="twf-time" data-test="team-workflow-time">{{ entryTime(w.lastAt) }}</span>
    </button>
    </div>
  </div>
</template>

<style scoped>
.twf { margin-top: 6px; padding-top: 6px; border-top: 1px solid var(--line); display: flex; flex-direction: column; gap: 1px; flex: 1 1 auto; min-height: 0; }
.twf-list { display: flex; flex-direction: column; gap: 1px; flex: 1 1 auto; min-height: 0; overflow-y: auto; }
.tiny { font-size: 11.5px; margin: 0; }
.twf-head { font-size: 10.5px; font-weight: 600; letter-spacing: .05em; text-transform: uppercase; color: var(--ink-soft, #64748b); margin-bottom: 2px; }
.twf-row { --tone: var(--state-idle); display: grid; grid-template-columns: 16px 1fr auto; align-items: center; gap: 6px; width: 100%;
  border: 0; background: transparent; padding: 2px 4px; border-radius: 4px; text-align: left; font-size: 12px; cursor: pointer; }
.twf-row:hover { background: color-mix(in srgb, var(--tone) 8%, transparent); }
.twf-row.completed { --tone: var(--ok, #1d6b2f); }
.twf-row.working { --tone: #0891b2; }
.twf-row.waiting { --tone: var(--warn, #b26a00); }
.twf-row.closed { --tone: var(--state-idle, #8a847c); }
.twf-icon { color: var(--tone); font-size: 11px; font-weight: 700; text-align: center; }
.twf-row.working .twf-icon { animation: twf-pulse 1.6s ease-in-out infinite; }
@keyframes twf-pulse { 50% { opacity: .35; } }
@media (prefers-reduced-motion: reduce) { .twf-row.working .twf-icon { animation: none; } }
.twf-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.twf-row.closed .twf-title { color: var(--ink-soft, #64748b); }
.twf-id { color: var(--ink-soft, #64748b); font-variant-numeric: tabular-nums; }
.twf-time { color: var(--ink-soft, #64748b); font-size: 11px; white-space: nowrap; font-variant-numeric: tabular-nums; }
</style>
