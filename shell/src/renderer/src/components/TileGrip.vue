<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import { teamTilesStore, type GripStore } from '../gripStore';

// The resize controls of a team tile, three kinds: the corner grip (both axes, inside the tile), the width handle (the
// gap to the right of the tile) and the height handle (the gap below it), like split-pane dividers. Dragging any of
// them (or the arrow keys on the focused one) changes the one shared size, so every tile follows, live; the size is
// kept on release. Double-click goes back to the default.
const props = withDefaults(defineProps<{ axis?: 'both' | 'w' | 'h'; store?: GripStore }>(), { axis: 'both', store: () => teamTilesStore });
const st = computed(() => props.store);
const dragging = ref(false);
const doW = computed(() => props.axis !== 'h');
const doH = computed(() => props.axis !== 'w');
const cursor = computed(() => (props.axis === 'w' ? 'col-resize' : props.axis === 'h' ? 'row-resize' : 'nwse-resize'));
const STEP = 16;
const BIG = 64;

let drag: { x: number; y: number; w: number; h: number; el: HTMLElement | null; id: number } | null = null;
function onMove(e: PointerEvent): void {
  if (drag) st.value.preview({ w: doW.value ? drag.w + (e.clientX - drag.x) : drag.w, h: doH.value ? drag.h + (e.clientY - drag.y) : drag.h });
}
function stop(): { w: number; h: number } | null {
  if (!drag) return null;
  const d = drag;
  drag = null;
  window.removeEventListener('pointermove', onMove);
  window.removeEventListener('pointerup', onUp);
  window.removeEventListener('pointercancel', onCancel);
  document.body.classList.remove('resizing-tiles', 'resizing-tiles-w', 'resizing-tiles-h');
  dragging.value = false;
  try { d.el?.releasePointerCapture?.(d.id); } catch { /* not captured */ }
  return d;
}
function onUp(): void { if (stop()) st.value.commit(); }
/** A cancelled drag (the pointer was taken away) never sticks: the size goes back to what it was. */
function onCancel(): void {
  const d = stop();
  if (d) st.value.preview({ w: d.w, h: d.h });
}
function startDrag(e: PointerEvent): void {
  if (e.button !== 0) return;
  e.preventDefault();
  e.stopPropagation();
  const el = e.currentTarget as HTMLElement | null;
  try { el?.setPointerCapture?.(e.pointerId); } catch { /* the window listeners still work */ }
  // From the size the tile really has (the content area may be narrower than the stored width): no dead zone, no jump.
  drag = { x: e.clientX, y: e.clientY, w: st.value.width.value, h: st.value.size.value.h, el, id: e.pointerId };
  window.addEventListener('pointermove', onMove);
  window.addEventListener('pointerup', onUp);
  window.addEventListener('pointercancel', onCancel);
  document.body.classList.add('resizing-tiles', ...(props.axis === 'w' ? ['resizing-tiles-w'] : props.axis === 'h' ? ['resizing-tiles-h'] : []));
  dragging.value = true;
}
function onKey(e: KeyboardEvent): void {
  const step = e.shiftKey ? BIG : STEP;
  const d = ({ ArrowRight: [step, 0], ArrowLeft: [-step, 0], ArrowDown: [0, step], ArrowUp: [0, -step] } as Record<string, number[]>)[e.key];
  if (!d || (d[0] !== 0 && !doW.value) || (d[1] !== 0 && !doH.value)) return;
  e.preventDefault();
  e.stopPropagation();
  st.value.set({ w: st.value.width.value + d[0], h: st.value.size.value.h + d[1] });
}
onBeforeUnmount(onCancel);
</script>

<template>
  <div class="tile-grip" :class="[`axis-${axis}`, { dragging }]" tabindex="0" role="slider" :aria-label="axis === 'both' ? `Resize all ${store.noun}` : `Resize all ${store.noun}: ${axis === 'w' ? 'width' : 'height'}`"
    :aria-orientation="axis === 'h' ? 'vertical' : 'horizontal'"
    :aria-valuenow="axis === 'h' ? store.size.value.h : store.width.value" :aria-valuemin="axis === 'h' ? store.limits.minH : store.limits.minW" :aria-valuemax="axis === 'h' ? store.limits.maxH : store.limits.maxW"
    :aria-valuetext="axis === 'w' ? `${store.width.value} pixels wide` : axis === 'h' ? `${store.size.value.h} pixels high` : `${store.width.value} by ${store.size.value.h} pixels`"
    :title="`Drag to resize all ${store.noun === 'team tiles' ? 'tiles' : store.noun} · double-click to reset`"
    :data-test="axis === 'both' ? `${store.testId}-grip` : `${store.testId}-handle-${axis}`" :style="{ cursor }" @pointerdown="startDrag" @keydown="onKey" @dblclick.stop="st.reset()" @click.stop>
    <svg v-if="axis === 'both'" width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true">
      <path d="M11 4 4 11 M11 8 8 11" />
    </svg>
  </div>
</template>

<style scoped>
.tile-grip { touch-action: none; }
/* The corner grip: shown when the corner zone (not just the tile) is hovered. */
.tile-grip.axis-both { position: absolute; right: 3px; bottom: 3px; width: 22px; height: 22px; display: grid; place-items: center;
  color: var(--muted); opacity: 0; border-radius: 4px; z-index: 2; }
.tile-grip.axis-both:hover, .tile-grip.axis-both:focus-visible, .tile-grip.axis-both.dragging { opacity: 1; }
:global(.team-card:hover) .tile-grip.axis-both { opacity: .55; }
:global(.team-card:hover) .tile-grip.axis-both:hover { opacity: 1; }
/* The gap handles: the whole 14px gap is the hit area, a 2px accent line centred in it shows on hover, focus and drag. */
.tile-grip.axis-w, .tile-grip.axis-h { position: absolute; z-index: 1; }
.tile-grip.axis-w { top: 0; bottom: 0; right: -14px; width: 14px; }
.tile-grip.axis-h { left: 0; right: 0; bottom: -14px; height: 14px; }
.tile-grip.axis-w::before, .tile-grip.axis-h::before { content: ''; position: absolute; background: var(--accent); border-radius: 1px; opacity: 0; pointer-events: none; }
.tile-grip.axis-w::before { top: 0; bottom: 0; left: 6px; width: 2px; }
.tile-grip.axis-h::before { left: 0; right: 0; top: 6px; height: 2px; }
.tile-grip.axis-w:hover::before, .tile-grip.axis-h:hover::before, .tile-grip.axis-w:focus-visible::before, .tile-grip.axis-h:focus-visible::before,
.tile-grip.axis-w.dragging::before, .tile-grip.axis-h.dragging::before { opacity: 1; }
.tile-grip.axis-w:focus-visible, .tile-grip.axis-h:focus-visible { outline: none; }
.tile-grip.axis-both:focus-visible { outline: 2px solid var(--accent); }
:global(body.resizing-tiles) { cursor: nwse-resize; user-select: none; }
:global(body.resizing-tiles-w) { cursor: col-resize; }
:global(body.resizing-tiles-h) { cursor: row-resize; }
</style>
