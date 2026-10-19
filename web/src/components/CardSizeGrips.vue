<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useDisplayStore } from '../stores/display';
import { draggedSize, GripShiftStep, GripStep, widthClamped } from '../lib/boardSize';

/**
 * THE GRIPS ON A MEMBER'S CARD: the right edge sets the width, the bottom edge the height, the
 * bottom-right corner both. Every card has them and every card shares the one size, as every Kanban
 * lane shares one width - dragging any card resizes them all, live, through the display store's
 * `preview`, and the release writes the size once with `set`.
 *
 * Drawn inside the card's border, over its content, so a grip takes no space from the feed. The
 * size rules and the drag arithmetic are `lib/boardSize`'s; this only turns pointer and key events
 * into calls on them.
 */
type Axis = 'x' | 'y' | 'both';

const display = useDisplayStore();

const root = ref<HTMLElement | null>(null);

/** Pixels of the person's text size per size pixel: the size is kept in rem at 16px, so a drag
 *  follows the pointer whatever the text size (Themes) is. */
function rootPixels(): number {
  const parsed = Number.parseFloat(getComputedStyle(document.documentElement).fontSize);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 16;
}

/**
 * Whether the screen already holds this card narrower than its width (`min(var(--os-card-w), 100%)`):
 * then a width grip would move nothing, so it is not drawn and the corner moves the height only.
 */
const clamped = ref(false);

const dragging = ref(false);

function measure() {
  if (dragging.value) return;
  const card = root.value?.parentElement;
  if (!card) return;
  clamped.value = widthClamped(card.getBoundingClientRect().width, (display.width * rootPixels()) / 16);
}

let observer: ResizeObserver | null = null;

onMounted(() => {
  measure();
  window.addEventListener('resize', measure);
  const card = root.value?.parentElement;
  if (card && typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(() => measure());
    observer.observe(card);
  }
});

onBeforeUnmount(() => {
  window.removeEventListener('resize', measure);
  observer?.disconnect();
});

watch(() => display.width, () => void nextTick(measure));

/** When the last touch or pen press on a grip began, so a second one soon after is a double-tap. */
let lastTap = 0;
const DoubleTapWindow = 350;

/** A press on a grip is only a drag: captured, not a scroll, not a click on anything beneath. */
function startDrag(event: PointerEvent, axis: Axis) {
  if (event.pointerType === 'mouse' && event.button !== 0) return;
  event.preventDefault();
  event.stopPropagation();

  if (event.pointerType !== 'mouse') {
    const now = event.timeStamp || Date.now();
    if (now - lastTap < DoubleTapWindow) {
      lastTap = 0;
      display.resetCardSize();
      return;
    }
    lastTap = now;
  }

  const handle = event.currentTarget as HTMLElement;
  handle.setPointerCapture?.(event.pointerId);

  const moves: Axis = axis === 'both' && clamped.value ? 'y' : axis;
  const start = { width: display.width, height: display.height };
  const startX = event.clientX;
  const startY = event.clientY;
  const scale = 16 / rootPixels();
  dragging.value = true;

  const move = (e: PointerEvent) => {
    const dx = moves === 'y' ? 0 : (e.clientX - startX) * scale;
    const dy = moves === 'x' ? 0 : (e.clientY - startY) * scale;
    display.preview(draggedSize(start, dx, dy));
  };

  // Released or cancelled alike: the drag ends at the size it last applied, written once.
  const end = () => {
    handle.removeEventListener('pointermove', move);
    handle.removeEventListener('pointerup', end);
    handle.removeEventListener('pointercancel', end);
    handle.releasePointerCapture?.(event.pointerId);
    dragging.value = false;
    if (display.width !== start.width || display.height !== start.height) {
      display.set({ width: display.width, height: display.height });
    }
    measure();
  };

  handle.addEventListener('pointermove', move);
  handle.addEventListener('pointerup', end);
  handle.addEventListener('pointercancel', end);
}

/** The keyboard on a grip: the arrows of its own axis move the size a step (Shift a larger one), Home resets. */
function gripKey(event: KeyboardEvent, axis: 'x' | 'y') {
  if (event.key === 'Home') {
    event.preventDefault();
    display.resetCardSize();
    return;
  }

  const step = event.shiftKey ? GripShiftStep : GripStep;
  const delta =
    axis === 'x'
      ? event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0
      : event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0;
  if (delta === 0) return;

  event.preventDefault();
  display.set(draggedSize(display, axis === 'x' ? delta : 0, axis === 'y' ? delta : 0));
}

const title = 'Drag to resize every card; double-click to reset';
</script>

<template>
  <div ref="root" class="card-grips" data-test="card-grips">
    <div
      v-if="!clamped"
      class="card-grip card-grip-width"
      role="separator"
      aria-orientation="vertical"
      aria-label="Card width"
      :aria-valuenow="display.width"
      tabindex="0"
      :title="title"
      data-test="card-grip-width"
      @pointerdown="startDrag($event, 'x')"
      @keydown="gripKey($event, 'x')"
      @dblclick.stop="display.resetCardSize()"
      @click.stop
    />
    <div
      class="card-grip card-grip-height"
      role="separator"
      aria-orientation="horizontal"
      aria-label="Card height"
      :aria-valuenow="display.height"
      tabindex="0"
      :title="title"
      data-test="card-grip-height"
      @pointerdown="startDrag($event, 'y')"
      @keydown="gripKey($event, 'y')"
      @dblclick.stop="display.resetCardSize()"
      @click.stop
    />
    <!-- The corner moves both axes, and is the one grip a touch screen always shows. The keyboard
         has the two edges for its two axes, so the corner is not a second stop in the tab order. -->
    <div
      class="card-grip card-grip-corner"
      aria-hidden="true"
      :title="title"
      data-test="card-grip-corner"
      @pointerdown="startDrag($event, 'both')"
      @dblclick.stop="display.resetCardSize()"
      @click.stop
    />
  </div>
</template>

<style scoped>
/* Covers the card inside its border and catches nothing itself: only the grips take a press. */
.card-grips {
  position: absolute;
  inset: 0;
  pointer-events: none;
  z-index: 2;
}

/* `touch-action: none` on the grips ONLY, so the board and the feed still scroll by touch anywhere
   else on the card. */
.card-grip {
  position: absolute;
  pointer-events: auto;
  touch-action: none;
}

.card-grip:focus-visible {
  outline: none;
}

.card-grip-width {
  top: 0;
  right: 0;
  bottom: 0;
  width: 6px;
  cursor: col-resize;
}

.card-grip-height {
  left: 0;
  right: 0;
  bottom: 0;
  height: 6px;
  cursor: row-resize;
}

.card-grip-corner {
  right: 0;
  bottom: 0;
  width: 16px;
  height: 16px;
  cursor: nwse-resize;
}

/* THE HANDLE, drawn as the Kanban lane divider's line: 2px of the primary colour, on hover and focus. */
.card-grip-width::after,
.card-grip-height::after,
.card-grip-corner::after {
  content: '';
  position: absolute;
  border-radius: 1px;
  background: transparent;
}

.card-grip-width::after {
  top: 0;
  bottom: 0;
  right: 2px;
  width: 2px;
}

.card-grip-height::after {
  left: 0;
  right: 0;
  bottom: 2px;
  height: 2px;
}

.card-grip-corner::after {
  right: 2px;
  bottom: 2px;
  width: 10px;
  height: 10px;
  border-right: 2px solid transparent;
  border-bottom: 2px solid transparent;
}

.card-grip-width:hover::after,
.card-grip-width:focus-visible::after,
.card-grip-height:hover::after,
.card-grip-height:focus-visible::after {
  background: var(--q-primary);
}

.card-grip-corner:hover::after {
  border-right-color: var(--q-primary);
  border-bottom-color: var(--q-primary);
}

/* A TOUCH SCREEN has no hover: the corner is always drawn, a 24px target, and the edges take no
   touch (they stay for the keyboard) so a finger near a card's edge still scrolls. */
@media (pointer: coarse) {
  .card-grip-width,
  .card-grip-height {
    pointer-events: none;
  }

  .card-grip-corner {
    width: 24px;
    height: 24px;
  }

  .card-grip-corner::after {
    right: 4px;
    bottom: 4px;
    border-right-color: var(--os-ink-muted);
    border-bottom-color: var(--os-ink-muted);
  }
}
</style>
