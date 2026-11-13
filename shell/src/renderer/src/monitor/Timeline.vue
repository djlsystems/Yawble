<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { spaced, type FeedRow } from './model';
import { Palette } from './scene';
import { StepLook, type StepKind, type StepRow } from './steps';
import { axisTicks, timeOf, toneOfRow } from './timeline';

/**
 * THE TIMELINE across the bottom of the Monitor: from `from` to now, a tick for each bus message in its tone (the
 * ones that need the person or end something stand taller), and under them the members' steps, a column of each
 * moment's count (file changes in their own colour). The right edge is now. Hovering says what happened there;
 * clicking, or dragging and letting go, asks to replay from that moment (`seek`). In a replay the playhead shows
 * how far it has got.
 */
const props = defineProps<{ from: number; to: number; rows: readonly FeedRow[]; steps: readonly StepRow[]; playhead: number | null; live: boolean }>();
const emit = defineEmits<{ seek: [at: number] }>();

const box = ref<HTMLDivElement | null>(null);
const canvas = ref<HTMLCanvasElement | null>(null);
const cursor = ref<number | null>(null); // the pointer's x, CSS px
const dragging = ref(false);
let observer: ResizeObserver | null = null;
let width = 0;
const Height = 58;
const Lane = { rows: [6, 26], steps: [30, 44], axis: 47 } as const;
const reduced = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;

const xOf = (t: number) => ((t - props.from) / Math.max(1, props.to - props.from)) * width;
const tOf = (x: number) => props.from + (Math.min(width, Math.max(0, x)) / Math.max(1, width)) * (props.to - props.from);
const clock = (t: number, seconds = false) => new Date(t).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: seconds ? '2-digit' : undefined });

/** How tall a message's tick stands: what needs the person or ends something stands tallest. */
function tickHeight(row: FeedRow): number {
  switch (row.kind) {
    case 'decision': case 'failed': case 'blocked': case 'done': return 20;
    case 'completed': case 'handback': case 'instruction': return 14;
    default: return 8;
  }
}

function draw(): void {
  const cv = canvas.value;
  const c = cv?.getContext('2d');
  if (!cv || !c || !width) return;
  const dpr = window.devicePixelRatio || 1;
  if (cv.width !== Math.round(width * dpr)) { cv.width = Math.round(width * dpr); cv.height = Math.round(Height * dpr); }
  c.setTransform(dpr, 0, 0, dpr, 0, 0);
  c.clearRect(0, 0, width, Height);

  // The replayed stretch, from the playhead to now, is shaded: the wall has not got there yet.
  if (props.playhead !== null) {
    const px = xOf(props.playhead);
    c.fillStyle = 'rgba(148, 163, 184, 0.07)';
    c.fillRect(px, 0, width - px, Lane.axis);
  }
  // The lanes' baselines.
  c.fillStyle = 'rgba(148, 163, 184, 0.12)';
  c.fillRect(0, Lane.rows[1], width, 1);
  c.fillRect(0, Lane.steps[1], width, 1);

  // The members' steps: a column per 2 px, its height by the count, file changes over the rest.
  const cols = Math.ceil(width / 2);
  const all = new Uint16Array(cols), edits = new Uint16Array(cols);
  for (const s of props.steps) {
    const i = Math.floor(xOf(timeOf(s)) / 2);
    if (i < 0 || i >= cols) continue;
    all[i]!++;
    if (s.kind === 'edit' || s.kind === 'create') edits[i]!++;
  }
  const h = (n: number) => Math.min(Lane.steps[1] - Lane.steps[0], 3 + Math.log2(1 + n) * 3.2);
  for (let i = 0; i < cols; i++) {
    if (!all[i]) continue;
    c.fillStyle = 'rgba(147, 197, 253, 0.45)';
    c.fillRect(i * 2, Lane.steps[1] - h(all[i]!), 1.6, h(all[i]!));
    if (edits[i]) {
      c.fillStyle = StepLook.edit.color;
      c.fillRect(i * 2, Lane.steps[1] - h(edits[i]!), 1.6, h(edits[i]!));
    }
  }

  // The messages: a tick each, in its tone; the quiet ones first so the loud ones stand over them.
  const sorted = [...props.rows].sort((a, b) => tickHeight(a) - tickHeight(b));
  for (const r of sorted) {
    const x = xOf(timeOf(r));
    if (x < -2 || x > width + 2) continue;
    const th = tickHeight(r);
    c.fillStyle = Palette[toneOfRow(r)];
    c.globalAlpha = r.kind === 'progress' ? 0.6 : 0.95;
    c.fillRect(Math.round(x) - 1, Lane.rows[1] - th, 2, th);
  }
  c.globalAlpha = 1;

  // The axis: round times along the bottom.
  c.font = '10px system-ui, sans-serif';
  c.textBaseline = 'top';
  c.textAlign = 'center';
  for (const t of axisTicks(props.from, props.to, width)) {
    const x = xOf(t);
    if (x < 18 || x > width - 34) continue;
    c.fillStyle = 'rgba(148, 163, 184, 0.35)';
    c.fillRect(Math.round(x), Lane.axis - 3, 1, 3);
    c.fillStyle = '#64748b';
    c.fillText(clock(t), x, Lane.axis + 1);
  }

  // Now: the right edge, glowing while live.
  c.fillStyle = props.live ? '#f43f5e' : '#64748b';
  c.save();
  if (props.live && !reduced) { c.shadowColor = '#f43f5e'; c.shadowBlur = 8; }
  c.fillRect(width - 2, 0, 2, Lane.axis);
  c.restore();
  c.textAlign = 'right';
  c.fillStyle = props.live ? '#fda4af' : '#94a3b8';
  c.fillText('now', width - 4, Lane.axis + 1);

  // The playhead, and the pointer.
  if (props.playhead !== null) {
    const x = xOf(props.playhead);
    c.fillStyle = '#e2e8f0';
    c.fillRect(Math.round(x) - 1, 0, 2, Lane.axis);
    c.beginPath(); c.moveTo(x - 5, 0); c.lineTo(x + 5, 0); c.lineTo(x, 6); c.closePath(); c.fill();
  }
  if (cursor.value !== null) {
    c.fillStyle = dragging.value ? '#e2e8f0' : 'rgba(226, 232, 240, 0.5)';
    c.fillRect(Math.round(cursor.value), 0, 1, Lane.axis);
  }
}

/** What happened near the pointer: the messages there, and each member's steps there summed by kind. */
const near = computed(() => {
  if (cursor.value === null || !width) return null;
  const at = tOf(cursor.value);
  const reach = (6 / width) * (props.to - props.from);
  const rows = props.rows.filter((r) => Math.abs(timeOf(r) - at) <= reach).slice(-5);
  const byMember = new Map<string, Map<StepKind, number>>();
  for (const s of props.steps) {
    if (Math.abs(timeOf(s) - at) > reach) continue;
    const who = spaced(s.member);
    let kinds = byMember.get(who);
    if (!kinds) byMember.set(who, (kinds = new Map()));
    kinds.set(s.kind, (kinds.get(s.kind) ?? 0) + 1);
  }
  const steps = [...byMember].slice(0, 4).map(([who, kinds]) => ({
    who, what: [...kinds].sort((a, b) => b[1] - a[1]).slice(0, 4).map(([k, n]) => `${n} ${StepLook[k].done}`).join(', '),
  }));
  const left = Math.min(Math.max(cursor.value, 172), width - 172);
  return { at, rows, steps, left };
});

const words: Record<FeedRow['kind'], string> = {
  instruction: 'told', completed: 'finished a run', failed: 'run failed', handback: 'handed back', progress: 'progress',
  blocked: 'is blocked', decision: 'needs you', done: 'finished the work',
};
const rowWords = (r: FeedRow) => r.kind === 'instruction'
  ? `${r.from === 'person' ? 'You' : r.from === 'host' ? 'Schedule' : spaced(r.from)} told ${spaced(r.member ?? r.team)}`
  : `${spaced(r.member ?? r.from)} ${words[r.kind]}`;

function local(e: PointerEvent): number {
  return e.clientX - (box.value?.getBoundingClientRect().left ?? 0);
}
function down(e: PointerEvent) {
  (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
  dragging.value = true;
  cursor.value = local(e);
}
function move(e: PointerEvent) { cursor.value = Math.min(width, Math.max(0, local(e))); }
function up(e: PointerEvent) {
  if (!dragging.value) return;
  dragging.value = false;
  emit('seek', tOf(local(e)));
}
function leave() { if (!dragging.value) cursor.value = null; }
function key(e: KeyboardEvent) {
  // The keyboard: Enter replays from the start of the window, the arrows from a tenth earlier or later.
  const at = props.playhead ?? props.to;
  const step = (props.to - props.from) / 10;
  if (e.key === 'Enter' || e.key === ' ') emit('seek', props.from);
  else if (e.key === 'ArrowLeft') emit('seek', Math.max(props.from, at - step));
  else if (e.key === 'ArrowRight' && props.playhead !== null) emit('seek', Math.min(props.to, at + step));
  else return;
  e.preventDefault();
}

watch(() => [props.from, props.to, props.rows, props.steps, props.playhead, props.live, cursor.value, dragging.value], draw);
onMounted(() => {
  observer = new ResizeObserver(() => { width = box.value?.clientWidth ?? 0; draw(); });
  if (box.value) { observer.observe(box.value); width = box.value.clientWidth; }
  draw();
});
onBeforeUnmount(() => observer?.disconnect());
</script>

<template>
  <div
    ref="box" class="timeline" role="slider" tabindex="0" aria-label="Replay from a moment"
    :aria-valuemin="from" :aria-valuemax="to" :aria-valuenow="playhead ?? to" data-test="monitor-timeline"
    @pointerdown="down" @pointermove="move" @pointerup="up" @pointerleave="leave" @keydown="key"
  >
    <canvas ref="canvas" :style="{ height: `${Height}px` }"></canvas>
    <div v-if="near" class="tip" :style="{ left: `${near.left}px` }" data-test="monitor-timeline-tip">
      <strong>{{ clock(near.at, true) }}</strong><span class="dim"> · click to replay from here</span>
      <div v-for="r in near.rows" :key="r.seq" class="row">
        <i :style="{ background: Palette[toneOfRow(r)] }" /><span class="what">{{ rowWords(r) }}<span v-if="r.text" class="dim">: {{ r.text }}</span></span>
      </div>
      <div v-for="s in near.steps" :key="s.who" class="row"><i class="steps" /><span class="what">{{ s.who }} <span class="dim">{{ s.what }}</span></span></div>
      <div v-if="!near.rows.length && !near.steps.length" class="dim">Nothing here.</div>
    </div>
  </div>
</template>

<style scoped>
.timeline { position: relative; cursor: pointer; user-select: none; touch-action: none; outline: none; }
.timeline:focus-visible { box-shadow: 0 0 0 2px #38bdf8; border-radius: 4px; }
canvas { display: block; width: 100%; }
.tip { position: absolute; bottom: calc(100% + 8px); transform: translateX(-50%); width: 340px; pointer-events: none; z-index: 2;
  background: rgba(12, 19, 34, .96); border: 1px solid #334155; border-radius: 8px; padding: 6px 10px; font-size: 11.5px; color: #e2e8f0; }
.row { margin-top: 3px; display: flex; align-items: center; gap: 6px; min-width: 0; }
.row i { flex: none; width: 7px; height: 7px; border-radius: 2px; }
.what { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.row i.steps { background: rgba(147, 197, 253, .7); }
.dim { color: #94a3b8; }
</style>
