<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { TIMELINE_GET, type Timeline, type TimelineBand, type YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { displayName } from '../faces/faces';
import Modal from '../components/Modal.vue';
import LoadingNote from '../components/LoadingNote.vue';
import {
  axisEnd, between, buildAxis, categoryLabel, categoryVar, clock, clockSec, closeTimeline, endedHowLabel, formatDuration,
  formatShort, isoMs, liveOverallMs, markerLabel, shares, TimeCategories, timelineTarget, useNow, type TimelineTarget,
} from './time';

// B086: the one Timeline dialog (mounted once in App.vue, like the Code dialog), opened by clicking an overall time on a
// card or a workflow row (time.ts openTimeline). It asks the core for timeline.get { card } | { workflow } and shows:
// the overall time created -> ended (or open) and how it ended; the planned / started line; the breakdown, each
// category's time and share, adding up; a "rebuilt from history" note; and the timeline itself - one row per card (a
// workflow: a summary row on top), bands by category, markers, long quiet stretches compressed behind a break mark.
// The core computes every time; this only lays them out. Read-only: a Visitor sees the same.
const props = defineProps<{ api?: YawbleApi }>();
const api = resolveApi(props.api);

/** An open item's timeline is asked for again this often (the shared 1 s clock drives it; no timer of its own). */
const RefreshMs = 30_000;

const data = ref<Timeline | null>(null);
const error = ref('');
let loadedAt = 0;
let loading = false;
let forTarget: TimelineTarget | null = null;

async function load() {
  const t = timelineTarget.value;
  if (!t || loading) return;
  loading = true;
  loadedAt = Date.now();
  try {
    const got = await api.call<Timeline>(TIMELINE_GET, t.kind === 'card' ? { card: t.id } : { workflow: t.id });
    if (timelineTarget.value !== t) return;
    data.value = got && typeof got === 'object' && got.time ? got : null;
    error.value = data.value ? '' : 'The core sent no timeline for it.';
  } catch (e) {
    if (timelineTarget.value !== t) return;
    const code = (e as { code?: string })?.code;
    error.value = code === 'method_not_found'
      ? 'This computer\'s Yawble is older and has no timeline yet. Update it to see where the time went.'
      : errorText(e);
  } finally { loading = false; }
}
watch(timelineTarget, (t) => {
  if (t === forTarget) return;
  forTarget = t;
  data.value = null; error.value = ''; picked.value = null;
  if (t) void load();
});
onMounted(() => { if (timelineTarget.value) { forTarget = timelineTarget.value; void load(); } });

const now = useNow();
watch(now, (n) => { if (data.value?.time.open && n - loadedAt >= RefreshMs) void load(); });

const target = computed(() => timelineTarget.value);
const time = computed(() => data.value?.time ?? null);
const isCard = computed(() => target.value?.kind === 'card');
const title = computed(() => {
  const t = target.value;
  if (!t) return '';
  return `${t.kind === 'card' ? `Card ${t.id}` : `Workflow #${t.id}`} · time`;
});
const overall = computed(() => (time.value ? liveOverallMs(time.value, now.value) : 0));

// The planned / started line.
const startLine = computed(() => {
  const t = time.value;
  if (!t) return [] as string[];
  const parts = [`${isCard.value ? 'planned' : 'first message'} ${clock(t.createdAt)}`];
  parts.push(t.startedAt ? `${isCard.value ? 'started' : 'first card started'} ${clock(t.startedAt, t.createdAt)}` : 'not started yet');
  if (!t.open && t.endedAt) parts.push(`${endedHowLabel(t.endedHow)} ${clock(t.endedAt, t.createdAt)}`);
  return parts;
});
const beforeStart = computed(() => (time.value?.startedAt ? between(time.value.createdAt, time.value.startedAt) : 0));

// The breakdown: the core's categories in merge order (one it does not know, after them), only those with time.
const breakdown = computed(() => {
  const b = (time.value?.breakdown ?? []).filter((x) => x && x.ms > 0);
  const order = (c: string) => { const i = TimeCategories.findIndex((x) => x.id === c); return i < 0 ? 99 : i; };
  const rows = [...b].sort((x, y) => order(x.category) - order(y.category));
  const pct = shares(rows.map((r) => r.ms));
  return rows.map((r, i) => ({ ...r, share: pct[i] }));
});
const breakdownTotal = computed(() => breakdown.value.reduce((a, r) => a + r.ms, 0));

// The timeline: rows, axis, markers.
interface Row { key: string; label: string; sub: string; bands: TimelineBand[]; summary: boolean }
const rows = computed<Row[]>(() => {
  const d = data.value;
  if (!d) return [];
  const out: Row[] = [];
  if (!isCard.value && d.summary?.length) out.push({ key: 'summary', label: 'All cards', sub: 'what the workflow was doing', bands: d.summary, summary: true });
  d.rows.forEach((r, i) => {
    const who = r.member ? displayName(r.member) : 'Unassigned';
    const label = isCard.value ? (r.member ? who : 'This card') : `${r.card != null ? `card ${r.card}` : 'card'} · ${who}`;
    out.push({ key: `r${i}`, label, sub: r.title ?? '', bands: r.bands ?? [], summary: false });
  });
  return out;
});
const allBands = computed(() => rows.value.flatMap((r) => r.bands));
const markers = computed(() => data.value?.markers ?? []);
const axis = computed(() => {
  const t = time.value;
  if (!t) return null;
  const start = isoMs(t.createdAt) ?? now.value;
  const end = axisEnd(t, allBands.value, markers.value, t.open ? now.value : 0);
  const edges: number[] = [];
  for (const b of allBands.value) { const s = isoMs(b.start), e = isoMs(b.end); if (s != null) edges.push(s); if (e != null) edges.push(e); }
  for (const m of markers.value) { const a = isoMs(m.at); if (a != null) edges.push(a); }
  return buildAxis(start, end, edges);
});
const bandEnd = (b: TimelineBand) => isoMs(b.end) ?? axis.value?.end ?? 0;
const pct = (f: number) => `${(Math.min(1, Math.max(0, f)) * 100).toFixed(3)}%`;
function bandStyle(b: TimelineBand) {
  const a = axis.value!;
  const s = a.at(isoMs(b.start) ?? a.start), e = a.at(bandEnd(b));
  return { left: pct(s), width: pct(Math.max(e - s, 0)), '--c': categoryVar(b.category) };
}
const bandLen = (b: TimelineBand) => Math.max(0, bandEnd(b) - (isoMs(b.start) ?? 0));
const bandSays = (b: TimelineBand) =>
  `${categoryLabel(b.category)}, ${clockSec(b.start)} to ${b.end ? clockSec(b.end) : 'now'}, ${formatDuration(bandLen(b))}`;
const markerSays = (m: { kind: string; at: string; card?: number | null }) =>
  `${markerLabel(m.kind)}${m.card != null && !isCard.value ? ` (card ${m.card})` : ''}, ${clockSec(m.at)}`;
const legend = computed(() => {
  const seen = new Set<string>([...allBands.value.map((b) => b.category), ...breakdown.value.map((b) => b.category)]);
  const known = TimeCategories.filter((c) => seen.has(c.id)).map((c) => ({ id: c.id as string, label: c.label, hint: c.hint }));
  const other = [...seen].filter((c) => !TimeCategories.some((k) => k.id === c)).map((c) => ({ id: c, label: c, hint: '' }));
  return [...known, ...other];
});

// What a hovered, focused or tapped band or marker says, below the axis (a tooltip that works by touch and keyboard).
const picked = ref<{ key: string; text: string } | null>(null);
const pick = (key: string, text: string) => { picked.value = { key, text }; };
const unpick = (key: string) => { if (picked.value?.key === key) picked.value = null; };
</script>

<template>
  <Modal v-if="target" :key="`${target.kind}:${target.id}`" :title="title" wide @close="closeTimeline">
    <div class="tl" data-test="timeline-dialog" :data-kind="target.kind" :data-id="target.id">
      <p v-if="target.title" class="tl-what" data-test="timeline-title">{{ target.title }}</p>
      <p v-if="error" class="error" data-test="timeline-error">{{ error }}</p>
      <LoadingNote v-else-if="!data" text="Loading the timeline…" />
      <template v-if="data && time">
        <section class="tl-overall" data-test="timeline-overall">
          <div class="big"><span class="muted">Overall</span> <strong data-test="timeline-overall-value">{{ formatDuration(overall) }}</strong></div>
          <div class="muted small" data-test="timeline-span">
            created {{ clock(time.createdAt) }} →
            <template v-if="time.open"><span class="open-tag" data-test="timeline-open">open</span>, still counting</template>
            <template v-else>{{ time.endedAt ? clock(time.endedAt, time.createdAt) : 'ended' }} ({{ endedHowLabel(time.endedHow) }})</template>
          </div>
          <div class="small" data-test="timeline-started">
            {{ startLine.join(' · ') }}<span v-if="beforeStart" class="muted" data-test="timeline-before-start"> · {{ formatShort(beforeStart) }} before it started</span>
          </div>
          <p v-if="time.rebuilt" class="note small" data-test="timeline-rebuilt">Rebuilt from history: this {{ isCard ? 'card' : 'workflow' }} is older than the timeline, so its times come from its trail lines and are close, not exact.</p>
        </section>

        <section class="tl-break">
          <h3>Where the time went</h3>
          <table v-if="breakdown.length" class="tl-table" data-test="timeline-breakdown">
            <thead><tr><th scope="col">Category</th><th scope="col" class="num">Time</th><th scope="col" class="num">Share</th></tr></thead>
            <tbody>
              <tr v-for="r in breakdown" :key="r.category" :data-category="r.category" data-test="timeline-breakdown-row">
                <td><span class="sw" :class="r.category" :style="{ '--c': categoryVar(r.category) }" aria-hidden="true" />{{ categoryLabel(r.category) }}</td>
                <td class="num">{{ formatDuration(r.ms) }}</td>
                <td class="num">{{ r.share }}%</td>
              </tr>
            </tbody>
            <tfoot><tr data-test="timeline-breakdown-total"><th scope="row">Total</th><td class="num">{{ formatDuration(breakdownTotal) }}</td><td class="num">100%</td></tr></tfoot>
          </table>
          <p v-else class="muted small">No time recorded yet.</p>
          <p v-if="time.open" class="muted small">As of {{ clockSec(new Date(loadedAt || Date.now()).toISOString()) }}; asked again every 30 seconds while it is open.</p>
        </section>

        <section v-if="axis && rows.length" class="tl-vis">
          <h3>Timeline</h3>
          <ul class="legend" data-test="timeline-legend">
            <li v-for="c in legend" :key="c.id" :title="c.hint"><span class="sw" :class="c.id" :style="{ '--c': categoryVar(c.id) }" aria-hidden="true" />{{ c.label }}</li>
          </ul>
          <div class="scroll" data-test="timeline-scroll">
            <div class="plot">
              <div class="lanes">
                <div v-for="r in rows" :key="r.key" class="lane" :class="{ summary: r.summary }" data-test="timeline-row">
                  <div class="lane-label"><strong>{{ r.label }}</strong><span v-if="r.sub" class="muted"> · {{ r.sub }}</span></div>
                  <div class="track" role="group" :aria-label="r.label">
                    <button v-for="(b, i) in r.bands" :key="i" type="button" class="band" :class="b.category" :style="bandStyle(b)"
                      :aria-label="bandSays(b)" :data-category="b.category" data-test="timeline-band"
                      @mouseenter="pick(`${r.key}:${i}`, `${r.label}: ${bandSays(b)}`)" @mouseleave="unpick(`${r.key}:${i}`)"
                      @focus="pick(`${r.key}:${i}`, `${r.label}: ${bandSays(b)}`)" @blur="unpick(`${r.key}:${i}`)"
                      @click="pick(`${r.key}:${i}`, `${r.label}: ${bandSays(b)}`)" />
                  </div>
                </div>
                <div class="marks" aria-hidden="true">
                  <span v-for="(br, i) in axis.breaks" :key="`b${i}`" class="brk" :style="{ left: pct(br.at) }" data-test="timeline-break" />
                  <span v-for="(m, i) in markers" :key="`m${i}`" class="mline" :class="m.kind" :style="{ left: pct(axis.at(isoMs(m.at) ?? axis.start)) }" />
                </div>
              </div>
              <div class="mrow">
                <button v-for="(m, i) in markers" :key="i" type="button" class="mk" :class="m.kind" :style="{ left: pct(axis.at(isoMs(m.at) ?? axis.start)) }"
                  :aria-label="markerSays(m)" :data-kind="m.kind" data-test="timeline-marker"
                  @mouseenter="pick(`m${i}`, markerSays(m))" @mouseleave="unpick(`m${i}`)" @focus="pick(`m${i}`, markerSays(m))" @blur="unpick(`m${i}`)"
                  @click="pick(`m${i}`, markerSays(m))" />
              </div>
              <div class="ticks small muted">
                <span class="t0">{{ clock(new Date(axis.start).toISOString()) }}</span>
                <span v-for="(br, i) in axis.breaks" :key="i" class="tb" :style="{ left: pct(br.at) }" :title="`${formatShort(br.ms)} with nothing changing, drawn shorter`">≈ {{ formatShort(br.ms) }}</span>
                <span class="t1">{{ time.open ? 'now' : clock(new Date(axis.end).toISOString(), axis.start) }}</span>
              </div>
            </div>
          </div>
          <p class="readout small" aria-live="polite" data-test="timeline-readout">{{ picked?.text ?? 'Point at, tap or tab to a band or a marker to read it.' }}</p>
          <details v-if="markers.length" class="events small" data-test="timeline-events">
            <summary>Events ({{ markers.length }})</summary>
            <ul><li v-for="(m, i) in markers" :key="i">{{ markerSays(m) }}</li></ul>
          </details>
        </section>
      </template>
    </div>
  </Modal>
</template>

<style scoped>
.tl { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
.tl-what { margin: 0; font-weight: 600; overflow-wrap: anywhere; }
h3 { margin: 0 0 6px; font-size: 13px; font-weight: 600; color: var(--ink-soft); }
.tl-overall { display: flex; flex-direction: column; gap: 3px; }
.big { font-size: 15px; }
.big strong { font-size: 20px; font-variant-numeric: tabular-nums; }
.small { font-size: 12px; }
.muted { color: var(--muted); }
.open-tag { color: var(--info); font-weight: 600; }
.note { margin: 4px 0 0; padding: 6px 8px; border-radius: 6px; background: var(--info-bg); color: var(--ink-soft); }
.error { color: var(--bad); }
.tl-table { border-collapse: collapse; font-size: 12.5px; min-width: min(360px, 100%); }
.tl-table th, .tl-table td { padding: 3px 10px 3px 0; text-align: left; border-bottom: 1px solid var(--line-soft); }
.tl-table thead th { font-weight: 600; color: var(--muted); font-size: 11.5px; }
.tl-table tfoot th, .tl-table tfoot td { font-weight: 600; border-bottom: 0; border-top: 1px solid var(--line); }
.num { text-align: right !important; font-variant-numeric: tabular-nums; white-space: nowrap; }
.sw { display: inline-block; width: 12px; height: 12px; border-radius: 3px; margin-right: 6px; vertical-align: -2px; background: var(--c); }
.legend { list-style: none; margin: 0 0 8px; padding: 0; display: flex; flex-wrap: wrap; gap: 4px 12px; font-size: 12px; }
/* Stalled: Agent working's hue, hatched, so it reads apart from it (also in the legend and the table). */
.sw.stalled, .band.stalled { background: repeating-linear-gradient(135deg, var(--c) 0 3px, color-mix(in srgb, var(--c) 25%, var(--surface)) 3px 6px); }
/* The axis scrolls inside the dialog on a narrow screen (375 px), never the page. */
.scroll { overflow-x: auto; overflow-y: hidden; max-width: 100%; padding-bottom: 4px; }
.plot { position: relative; min-width: 560px; }
.lanes { position: relative; display: flex; flex-direction: column; gap: 6px; }
.lane-label { font-size: 11.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin-bottom: 2px; }
.track { position: relative; height: 16px; border-radius: 3px; background: var(--surface-sunken); }
.lane.summary .track { height: 22px; }
.lane.summary { padding-bottom: 6px; border-bottom: 1px dashed var(--line); }
.band { position: absolute; top: 0; bottom: 0; min-width: 2px; padding: 0; border: 0; border-radius: 2px; background: var(--c); cursor: pointer; }
.band:hover, .band:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; z-index: 1; }
.marks { position: absolute; inset: 0; pointer-events: none; }
.mline { position: absolute; top: 0; bottom: 0; width: 0; border-left: 1px solid color-mix(in srgb, var(--tl-marker) 22%, transparent); }
.mline.reopen, .mline.watchdog-end, .mline.expired { border-left-style: dashed; }
/* A compressed stretch: a break mark (two slanted strokes) across the rows. */
.brk { position: absolute; top: -2px; bottom: -2px; width: 6px; margin-left: -3px; background: var(--surface); border-left: 1.5px solid var(--tl-break); border-right: 1.5px solid var(--tl-break); transform: skewX(-14deg); }
.mrow { position: relative; height: 14px; margin-top: 3px; }
.mk { position: absolute; top: 1px; width: 10px; height: 10px; margin-left: -5px; padding: 0; border: 1.5px solid var(--tl-marker); background: var(--surface); transform: rotate(45deg); cursor: pointer; }
.mk.planned, .mk.started { background: var(--tl-marker); }
.mk.decision-asked, .mk.decision-answered { border-color: var(--tl-waiting-on-you); }
.mk.decision-answered { background: var(--tl-waiting-on-you); }
.mk.watchdog-end, .mk.expired { border-color: var(--bad); }
.mk.handback { border-radius: 50%; }
.mk:hover, .mk:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
.ticks { position: relative; height: 16px; margin-top: 2px; font-variant-numeric: tabular-nums; }
.ticks .t0 { position: absolute; left: 0; }
.ticks .t1 { position: absolute; right: 0; }
.ticks .tb { position: absolute; transform: translateX(-50%); white-space: nowrap; color: var(--tl-break); }
.readout { margin: 6px 0 0; min-height: 1.4em; color: var(--ink-soft); }
.events ul { margin: 4px 0 0; padding-left: 18px; }
@media (prefers-reduced-motion: no-preference) {
  .band, .mk { transition: outline-color .12s ease; }
}
@media (max-width: 759.98px) {
  .track { height: 22px; }
  .lane.summary .track { height: 26px; }
  .mrow { height: 22px; }
  .mk { width: 14px; height: 14px; margin-left: -7px; top: 3px; }
}
</style>
