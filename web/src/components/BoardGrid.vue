<script setup lang="ts">
import type { ContainerSnapshot } from '../api/types';
import { useConsoleStore } from '../stores/console';
import { useDisplayStore } from '../stores/display';
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
defineProps<{ containers: ContainerSnapshot[] }>();

const board = useConsoleStore();
const display = useDisplayStore();
</script>

<template>
  <div class="board-grid" :style="display.cssVars" data-board-grid>
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
