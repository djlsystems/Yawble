<script setup lang="ts">
import { computed, onUnmounted, provide, ref, watch, watchPostEffect } from 'vue';
import type { ContainerSnapshot } from '../api/types';
import { useConsoleStore } from '../stores/console';
import { useDisplayStore } from '../stores/display';
import { useBoardOrderStore } from '../stores/boardOrder';
import { memberKey, useHighlightStore } from '../stores/highlight';
import { BoardArrangerKey, dropBefore, type CardBox } from '../lib/boardOrder';
import ContainerCard from './ContainerCard.vue';

/**
 * THE BOARD'S MEMBER CARDS, as fixed-size windows that WRAP rather than a responsive column count.
 *
 * A responsive column count decides a card's width by the viewport and its height by whichever
 * sibling has the longest feed: a busy Manager sets the height of the whole row, leaving its quiet
 * colleagues as whitespace. Here each card is the size this viewer chose and the row simply wraps.
 *
 * The CSS variables come from the display store and are read through the cascade, so changing one -
 * from the Board display dialog, or by dragging any card's grip - re-lays every card out with
 * nothing per-card to keep in step.
 *
 * THE CARDS ARE LAID IN THIS VIEWER'S ORDER (`stores/boardOrder`, one list of member ids per team,
 * in this browser only): a card is carried to another place by the handle at its head, live, and
 * the arrangement is written once when it is let go. The grid wraps that order into rows and
 * columns as before, so a place is an order and the card keeps the board's one size.
 */
const props = defineProps<{ containers: ContainerSnapshot[] }>();

const board = useConsoleStore();
const display = useDisplayStore();
const order = useBoardOrderStore();

/** The team shown: every card on a board is one team's. */
const team = computed(() => props.containers[0]?.team ?? null);
watch(team, (shown) => { if (shown) order.load(shown); }, { immediate: true });

const arranged = computed(() =>
  team.value ? order.arranged(team.value, props.containers) : props.containers,
);
const roster = computed(() => props.containers.map((container) => container.id));

/**
 * A MEMBER THE ADDRESS ASKED FOR (`member=` beside `team=`) lands here once its team's cards are
 * drawn: scrolled into view and flashed once. A member this board has not got says so in one line,
 * with no flash. Run after each render, because the cards may only exist once the team list lands.
 */
const highlight = useHighlightStore();
const gridEl = ref<HTMLElement | null>(null);

function cardElement(container: ContainerSnapshot): HTMLElement | null {
  const key = memberKey(container.team, container.id);
  return [...(gridEl.value?.querySelectorAll<HTMLElement>('[data-member-card]') ?? [])]
    .find((node) => node.dataset.memberCard === key) ?? null;
}

watchPostEffect(() => {
  const asked = highlight.member;
  if (!asked || props.containers.length === 0) return;
  if (props.containers.some((container) => container.team !== asked.team)) return;

  const found = props.containers.find(
    (container) => container.id === asked.member || container.name === asked.member,
  );
  if (!found) {
    highlight.miss(`${asked.member} is not on this board.`);
    return;
  }

  highlight.land(memberKey(found.team, found.id), cardElement(found));
});

// The notice is about this landing; leaving the board leaves it behind.
onUnmounted(() => highlight.dismissNotice());

/**
 * THE DRAG. The card pressed follows the pointer (translated, over the others); where it would land
 * is decided by `dropBefore` over the OTHER cards' boxes as laid out now, and drawn as the Kanban
 * divider's 2px primary line in the gutter before that card (or after the last). Letting go places
 * it there and writes the arrangement once; Escape or a cancelled pointer puts it back and writes
 * nothing.
 */
const dragging = ref<string | null>(null);
const dragShift = ref({ x: 0, y: 0 });
const indicator = ref<{ left: number; top: number; height: number } | null>(null);

/** The gutter between cards, as `.board-grid` sets it; the indicator is centred in it. */
const Gap = 16;

function boxes(except: string): CardBox[] {
  const found: CardBox[] = [];
  for (const container of arranged.value) {
    if (container.id === except) continue;
    const element = cardElement(container);
    if (!element) continue;
    const rect = element.getBoundingClientRect();
    found.push({ id: container.id, left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom });
  }
  return found;
}

function indicatorFor(target: string | null, others: CardBox[]): { left: number; top: number; height: number } | null {
  const grid = gridEl.value?.getBoundingClientRect();
  if (!grid || others.length === 0) return null;
  const before = target === null ? null : others.find((box) => box.id === target) ?? null;
  const last = others[others.length - 1]!;
  const box = before ?? last;
  const left = before ? box.left - Gap / 2 - 1 : box.right + Gap / 2 - 1;
  return { left: left - grid.left, top: box.top - grid.top, height: box.bottom - box.top };
}

function start(event: PointerEvent, member: string) {
  const shown = team.value;
  if (event.button !== 0 || !shown) return;
  event.preventDefault();
  event.stopPropagation();

  const handle = event.currentTarget as HTMLElement;
  handle.setPointerCapture?.(event.pointerId);

  const startX = event.clientX;
  const startY = event.clientY;
  let target: string | null | undefined;
  dragging.value = member;
  dragShift.value = { x: 0, y: 0 };
  indicator.value = null;

  const move = (e: PointerEvent) => {
    dragShift.value = { x: e.clientX - startX, y: e.clientY - startY };
    const others = boxes(member);
    target = dropBefore(others, e.clientX, e.clientY);
    indicator.value = indicatorFor(target, others);
  };

  const finish = () => {
    handle.removeEventListener('pointermove', move);
    handle.removeEventListener('pointerup', drop);
    handle.removeEventListener('pointercancel', cancel);
    window.removeEventListener('keydown', onKey, true);
    handle.releasePointerCapture?.(event.pointerId);
    dragging.value = null;
    dragShift.value = { x: 0, y: 0 };
    indicator.value = null;
  };

  const drop = () => {
    const landing = target;
    finish();
    if (landing !== undefined) order.place(shown, roster.value, member, landing);
  };

  const cancel = () => finish();

  const onKey = (e: KeyboardEvent) => {
    if (e.key !== 'Escape') return;
    e.preventDefault();
    e.stopPropagation();
    cancel();
  };

  handle.addEventListener('pointermove', move);
  handle.addEventListener('pointerup', drop);
  handle.addEventListener('pointercancel', cancel);
  window.addEventListener('keydown', onKey, true);
}

function move(member: string, delta: number) {
  if (team.value) order.move(team.value, roster.value, member, delta);
}

provide(BoardArrangerKey, { start, move });
</script>

<template>
  <q-banner
    v-if="highlight.notice"
    dense
    rounded
    class="q-mb-sm"
    data-highlight-notice
  >
    {{ highlight.notice }}
    <template #action>
      <q-btn flat dense no-caps label="Dismiss" @click="highlight.dismissNotice()" />
    </template>
  </q-banner>
  <div ref="gridEl" class="board-grid" :class="{ 'board-grid--arranging': dragging !== null }" :style="display.cssVars" data-board-grid>
    <ContainerCard
      v-for="container in arranged"
      :key="container.id"
      :snapshot="container"
      :feed="board.activityFor(container.team, container.id)"
      :carried="dragging === container.id ? dragShift : null"
    />
    <div
      v-if="indicator"
      class="board-drop-indicator"
      :style="{ left: `${indicator.left}px`, top: `${indicator.top}px`, height: `${indicator.height}px` }"
      data-test="board-drop-indicator"
      aria-hidden="true"
    />
  </div>
</template>

<style scoped>
/* THE GUTTER between cards is one token, because the cards' size grips sit IN it (`CardSizeGrips`):
   the grip between two cards is centred in this gap, as the Kanban's lane divider is in its 12px.
   `position: relative` hosts the drop indicator while a card is carried. */
.board-grid {
  --os-card-gap: 16px;
  position: relative;
  display: flex;
  flex-wrap: wrap;
  gap: var(--os-card-gap);
  align-items: flex-start;
}

/* While a card is carried nothing else on the board takes the pointer, so no card lights up under
   the one in flight and the drop is decided by the boxes alone. */
.board-grid--arranging :deep(.container-window:not(.board-card--dragging)) {
  pointer-events: none;
}

.board-grid :deep(.board-card--dragging) {
  z-index: 5;
  opacity: 0.85;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.25);
  pointer-events: none;
}

/* Where the carried card lands: the Kanban divider's line, centred in the gutter. */
.board-drop-indicator {
  position: absolute;
  width: 2px;
  border-radius: 1px;
  background: var(--q-primary);
  pointer-events: none;
  z-index: 6;
}
</style>
