<script setup lang="ts">
import { inject } from 'vue';
import { BoardArrangerKey } from '../lib/boardOrder';

/**
 * THE HANDLE THAT MOVES A MEMBER'S CARD on the board: at the head of the card, before its name. A
 * press and drag on it carries the card to another place in the grid (the board decides where it
 * lands, `lib/boardOrder`'s `dropBefore`); the arrow keys on it move the card a place, Home and End
 * to the ends. The card's size is the board's one size and does not change with its place.
 *
 * A handle rather than the whole card, because the card is full of things that take a press - the
 * name, the eye, the feed that scrolls - and a drag that began on any of them would fight each. The
 * board provides the arranger; a card mounted with none around it shows no handle.
 *
 * The handle is the member's avatar (the default slot): what a person grabs is the member's own
 * face. With no board the avatar is still shown, as a plain span that moves nothing.
 */
const props = defineProps<{ member: string; name: string }>();

const board = inject(BoardArrangerKey, null);

function onKey(event: KeyboardEvent) {
  if (!board) return;
  const delta =
    event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1
      : event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1
        : event.key === 'Home' ? Number.NEGATIVE_INFINITY
          : event.key === 'End' ? Number.POSITIVE_INFINITY
            : 0;
  if (delta === 0) return;
  event.preventDefault();
  board.move(props.member, delta);
}
</script>

<template>
  <button
    v-if="board"
    type="button"
    class="card-arrange-handle"
    :aria-label="`Move ${name}'s card`"
    title="Drag to move this card; the arrow keys move it a place, Home and End to the ends"
    data-test="card-arrange-handle"
    @pointerdown="board.start($event, member)"
    @keydown="onKey"
    @click.stop
  >
    <slot />
  </button>
  <span v-else class="card-arrange-still"><slot /></span>
</template>

<style scoped>
/* `touch-action: none` so a finger on the handle drags the card and does not scroll the page; the
   rest of the card still scrolls by touch. */
.card-arrange-handle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  margin-left: -4px;
  padding: 0;
  border: 0;
  border-radius: 50%;
  background: none;
  color: var(--os-ink-muted);
  cursor: grab;
  touch-action: none;
  flex: 0 0 auto;
}

.card-arrange-handle:hover,
.card-arrange-handle:focus-visible {
  color: var(--q-primary);
  background: color-mix(in srgb, var(--q-primary) 12%, transparent);
  outline: none;
}

.card-arrange-still {
  display: inline-flex;
  flex: 0 0 auto;
  margin-left: -4px;
}

.card-arrange-handle:active {
  cursor: grabbing;
}
</style>
