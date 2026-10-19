<script setup lang="ts">
import { onUnmounted, ref, watchPostEffect } from 'vue';
import type { ContainerSnapshot } from '../api/types';
import { useConsoleStore } from '../stores/console';
import { useDisplayStore } from '../stores/display';
import { memberKey, useHighlightStore } from '../stores/highlight';
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
 */
const props = defineProps<{ containers: ContainerSnapshot[] }>();

const board = useConsoleStore();
const display = useDisplayStore();

/**
 * A MEMBER THE ADDRESS ASKED FOR (`member=` beside `team=`) lands here once its team's cards are
 * drawn: scrolled into view and flashed once. A member this board has not got says so in one line,
 * with no flash. Run after each render, because the cards may only exist once the team list lands.
 */
const highlight = useHighlightStore();
const gridEl = ref<HTMLElement | null>(null);

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

  const key = memberKey(found.team, found.id);
  const element = [...(gridEl.value?.querySelectorAll<HTMLElement>('[data-member-card]') ?? [])]
    .find((node) => node.dataset.memberCard === key) ?? null;
  highlight.land(key, element);
});

// The notice is about this landing; leaving the board leaves it behind.
onUnmounted(() => highlight.dismissNotice());
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
  <div ref="gridEl" class="board-grid" :style="display.cssVars" data-board-grid>
    <ContainerCard
      v-for="container in containers"
      :key="container.id"
      :snapshot="container"
      :feed="board.activityFor(container.team, container.id)"
    />
  </div>
</template>

<style scoped>
.board-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  align-items: flex-start;
}
</style>
