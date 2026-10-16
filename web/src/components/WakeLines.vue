<script setup lang="ts">
import type { PendingWake } from '../api/types';
import { wakeLine } from '../lib/wakes';

/**
 * A MEMBER'S PENDING WAKES, one line each - "wakes at 9:41 PM: <note>" on the reader's clock - with
 * a person's Cancel. Drawn on the member's card and on the Kanban card, so the two say it the same.
 *
 * PRESENTATIONAL: Cancel is an event, and whoever holds the card makes the call. Cancel is a span
 * with a button's role rather than a button because the Kanban card is a button, and one may not
 * hold another; the click stops here so it does not open the card's panel as well.
 */
defineProps<{
  wakes: readonly PendingWake[];
  /** The wake whose cancel is in flight, or null. */
  cancelling?: number | null;
}>();

const emit = defineEmits<{ cancel: [wake: PendingWake] }>();
</script>

<template>
  <div class="wake-lines">
    <div v-for="wake in wakes" :key="wake.id" class="wake-line" :data-wake-id="wake.id">
      <q-icon name="alarm" size="14px" class="wake-line-icon" />
      <span class="wake-line-text">{{ wakeLine(wake) }}</span>
      <span
        role="button"
        tabindex="0"
        class="wake-line-cancel"
        :aria-disabled="cancelling === wake.id ? 'true' : undefined"
        :aria-label="`Cancel the wake ${wakeLine(wake)}`"
        data-action="cancel-wake"
        @click.stop="cancelling === wake.id || emit('cancel', wake)"
        @keydown.enter.stop.prevent="cancelling === wake.id || emit('cancel', wake)"
        @keydown.space.stop.prevent="cancelling === wake.id || emit('cancel', wake)"
      >
        {{ cancelling === wake.id ? 'Cancelling…' : 'Cancel' }}
        <q-tooltip>Cancel this wake: the member is not woken for it.</q-tooltip>
      </span>
    </div>
  </div>
</template>

<style scoped>
.wake-lines {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.wake-line {
  display: flex;
  align-items: baseline;
  gap: 4px;
  font-size: 0.75rem;
  color: var(--q-primary);
}

.wake-line-icon {
  flex: 0 0 auto;
  align-self: center;
}

.wake-line-text {
  flex: 1 1 auto;
  min-width: 0;
  /* A note is one line of a member's words; it wraps inside the card rather than widening it. */
  overflow-wrap: anywhere;
}

.wake-line-cancel {
  flex: 0 0 auto;
  cursor: pointer;
  text-decoration: underline;
}

.wake-line-cancel[aria-disabled='true'] {
  cursor: default;
  opacity: 0.6;
}
</style>
