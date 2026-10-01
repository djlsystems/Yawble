<script setup lang="ts">
import type { Outcome } from '../api/outcomes';
import { linkedWorkflowsText, when } from '../lib/outcomes';

/**
 * NEEDS YOU'S PROPOSED OUTCOMES: each outcome a Manager proposed and only a person can confirm,
 * merge or reject. One entry per outcome - its name, who proposed it (`createdBy`, the proposing
 * team's Manager), when, and how many workflows link it (the route's `figures.workflows.total`;
 * nothing here counts) - and a click opens Manage outcomes on it, where the actions are.
 *
 * EVERYTHING IS TEXT. The name is a Manager's words and is bound with mustaches, never `v-html`.
 */
defineProps<{ outcomes: Outcome[] }>();

defineEmits<{ open: [outcomeId: string] }>();
</script>

<template>
  <div v-if="outcomes.length" class="k-proposed" data-needs-you-outcomes>
    <button
      v-for="outcome in outcomes"
      :key="outcome.id"
      type="button"
      class="k-proposed-entry"
      :data-needs-you-outcome="outcome.id"
      :aria-label="`Proposed outcome ${outcome.name}: open in Manage outcomes`"
      @click="$emit('open', outcome.id)"
    >
      <span class="k-proposed-kind">Proposed outcome</span>
      <span class="k-proposed-name" data-proposed-name>{{ outcome.name }}</span>
      <span class="k-proposed-meta" data-proposed-by>Proposed by {{ outcome.createdBy }}</span>
      <span class="k-proposed-meta" data-proposed-at>{{ when(outcome.createdAt) }}</span>
      <span class="k-proposed-meta" data-proposed-links>{{ linkedWorkflowsText(outcome.figures.workflows.total) }}</span>
    </button>
  </div>
</template>

<style scoped>
.k-proposed {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 8px;
}

.k-proposed-entry {
  display: flex;
  flex-direction: column;
  gap: 2px;
  text-align: left;
  font: inherit;
  color: inherit;
  background: var(--os-surface);
  border: 1px solid var(--os-rule);
  border-left: 3px solid var(--os-warn-ink);
  border-radius: 6px;
  padding: 8px;
  cursor: pointer;
}

.k-proposed-entry:focus-visible {
  outline: 2px solid var(--q-primary);
  outline-offset: 2px;
}

.k-proposed-kind {
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--os-warn-ink);
}

.k-proposed-name {
  font-weight: 600;
  overflow-wrap: anywhere;
}

.k-proposed-meta {
  font-size: 12px;
  color: var(--os-ink-muted);
  overflow-wrap: anywhere;
}
</style>
