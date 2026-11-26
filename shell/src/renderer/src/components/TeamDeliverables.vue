<script setup lang="ts">
import { ref } from 'vue';
import { usePoll } from '../usePoll';
import { deliverableTime, type YawbleApi, type DeliverableSummary } from '../../../shared/api';
import LoadingNote from './LoadingNote.vue';
import OverallTime from '../timeline/OverallTime.vue';
import { hasTime } from '../timeline/time';
import { entryTime, latestDeliverables, TileDeliverablesShown, DeliverableIconPath, deliverableLook } from '../monitor/deliverables';
import ItemLink from '../deliverables/ItemLink.vue';
import { openDeliverablesPage, deliverableItems, type ItemsOf } from '../deliverables/page';

// A team's deliverables on its card in the Teams list: the 50 newest (TileDeliverablesShown) and every open one however old
// (bus.deliverables lists those), last updated first, scrolling inside the tile, each with its state (the Monitor's colours
// and icons; expired has its own), "#id title", its overall time (it opens the timeline) and the time of its last entry (an ended one: its recorded end, endedAt). A row opens the team's deliverables with that one selected.
const props = defineProps<{ teamId: number; api: YawbleApi; pollMs?: number }>();
const emit = defineEmits<{ open: [deliverable: number] }>();

const list = ref<DeliverableSummary[] | null>(null);
const failed = ref(false);

async function load() {
  try {
    const got = await props.api.call<DeliverableSummary[]>('bus.deliverables', { teamId: props.teamId, limit: TileDeliverablesShown });
    list.value = latestDeliverables(Array.isArray(got) ? got : [], Infinity);
    failed.value = false;
    void loadItems();
  } catch { failed.value = true; return false; /* keeps what it showed */ }
}

// Each deliverable's backlog item as a link; deliverables.list is asked again only when a new deliverable shows.
const items = ref<ItemsOf | null>(null);
const looked = new Set<number>();
async function loadItems() {
  const ids = (list.value ?? []).map((w) => w.id);
  if (!ids.some((id) => !looked.has(id))) return;
  for (const id of ids) looked.add(id);
  const got = await deliverableItems(props.api, { teamId: props.teamId, limit: TileDeliverablesShown });
  if (got) items.value = got;
}
const tip = (w: DeliverableSummary) =>
  `${deliverableLook(w).label} · #${w.id} ${w.title}${w.busy.length ? ` · ${w.busy.join(', ')}` : w.ending ? ` · ${w.ending}` : ''}`;

usePoll(load, Math.max(5000, props.pollMs ?? 5000));
</script>

<template>
  <div class="twf" data-test="team-deliverables">
    <div class="twf-head">Deliverables<button type="button" class="twf-all" data-test="team-deliverables-all" title="This team's deliverables on the Deliverables page" @click.stop="openDeliverablesPage({ teamId })">All deliverables →</button></div>
    <LoadingNote v-if="!list && !failed" class="tiny" text="Loading deliverables…" />
    <p v-else-if="!list" class="muted tiny" data-test="team-deliverables-error">The deliverables could not be loaded.</p>
    <p v-if="list && !list.length" class="muted tiny" data-test="team-deliverables-empty">No deliverables yet.</p>
    <div class="twf-list" data-test="team-deliverables-list">
    <button v-for="w in list ?? []" :key="w.id" type="button" class="twf-row" :class="deliverableLook(w).status"
      :data-id="w.id" :data-status="deliverableLook(w).status" :title="tip(w)" data-test="team-deliverable" @click="emit('open', w.id)">
      <svg class="twf-icon" viewBox="3 3 10 10" aria-hidden="true"><path :d="DeliverableIconPath[deliverableLook(w).icon]" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" /></svg>
      <span class="twf-title"><span class="twf-id">#{{ w.id }}</span> <ItemLink v-if="items?.items.get(w.id)" nested :item="items.items.get(w.id)" :backlog="items.backlog" :dash="false" /> {{ w.title }}<span v-if="w.state === 'expired'" class="twf-tag" data-test="team-deliverable-expired"> · expired</span></span>
      <OverallTime v-if="hasTime(w.time)" class="twf-overall" :time="w.time" :target="{ kind: 'deliverable', id: w.id, title: w.title }" />
      <span class="twf-time" data-test="team-deliverable-time">{{ entryTime(deliverableTime(w)) }}</span>
    </button>
    </div>
  </div>
</template>

<style scoped>
.twf { margin-top: 6px; padding-top: 6px; border-top: 1px solid var(--line); display: flex; flex-direction: column; gap: 1px; flex: 1 1 auto; min-height: 0; }
.twf-list { display: flex; flex-direction: column; gap: 1px; flex: 1 1 auto; min-height: 0; overflow-y: auto; }
.tiny { font-size: 11.5px; margin: 0; }
.twf-head { font-size: 10.5px; font-weight: 600; letter-spacing: .05em; text-transform: uppercase; color: var(--ink-soft); margin-bottom: 2px; }
.twf-row { --tone: var(--state-idle); display: grid; grid-template-columns: 16px 1fr auto auto; align-items: center; gap: 6px; width: 100%;
  border: 0; background: transparent; padding: 2px 4px; border-radius: 4px; text-align: left; font-size: 12px; cursor: pointer; }
.twf-row:hover { background: color-mix(in srgb, var(--tone) 8%, transparent); }
.twf-row.completed { --tone: var(--ok); }
.twf-row.working { --tone: var(--info); }
.twf-row.waiting { --tone: var(--warn); }
.twf-row.closed, .twf-row.expired { --tone: var(--state-idle); }
.twf-icon { color: var(--tone); width: 11px; height: 11px; justify-self: center; align-self: center; }
.twf-row.working .twf-icon { animation: twf-pulse 1.6s ease-in-out infinite; }
@keyframes twf-pulse { 50% { opacity: .35; } }
@media (prefers-reduced-motion: reduce) { .twf-row.working .twf-icon { animation: none; } }
.twf-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.twf-row.closed .twf-title, .twf-row.expired .twf-title { color: var(--ink-soft); }
.twf-tag { font-size: 11px; }
.twf-all { float: right; border: 0; background: transparent; padding: 0; color: var(--accent); font-size: 11px; text-transform: none; letter-spacing: 0; font-weight: 500; cursor: pointer; }
.twf-all:hover { text-decoration: underline; }
.twf-title :deep(.item-link), .twf-title :deep(.item-plain) { font-size: 11px; }
.twf-id { color: var(--ink-soft); font-variant-numeric: tabular-nums; }
.twf-time { color: var(--ink-soft); font-size: 11px; white-space: nowrap; font-variant-numeric: tabular-nums; }
/* A phone: each deliverable row is a 44 px tap target. */
@media (max-width: 759.98px) {
  .twf-row { min-height: 44px; font-size: 13px; }
}
</style>
