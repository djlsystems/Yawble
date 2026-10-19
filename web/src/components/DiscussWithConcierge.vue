<script setup lang="ts">
import type { DecisionAsk } from '../lib/conciergeDecision';
import { useConciergeAskStore } from '../stores/conciergeAsk';
import { useConsoleStore } from '../stores/console';

/**
 * "Discuss with Concierge", beside a decision a person is asked to make. A click hands the decision
 * to the Concierge panel (`stores/conciergeAsk`), which opens, and types one request naming it once
 * its session is ready. The caller decides whether there is a decision (`lib/conciergeDecision`);
 * this only draws the control.
 *
 * `inline` draws it as a span with the button role, for a place that is itself a button (a Kanban
 * card): a button inside a button is not valid HTML, and the click must not open the card as well.
 */
const props = defineProps<{ decision: DecisionAsk; inline?: boolean }>();

function discuss() {
  // The team's display name is read at the click, from the board, so a card that holds only the
  // team's id still names it; the id stands in when the board does not know the team.
  const team = useConsoleStore().teams.find((candidate) => candidate.id === props.decision.team);
  useConciergeAskStore().ask({ ...props.decision, teamName: team?.name || props.decision.teamName });
}
</script>

<template>
  <span
    v-if="inline"
    role="button"
    tabindex="0"
    class="discuss-with-concierge discuss-with-concierge--inline"
    data-test="discuss-with-concierge"
    @click.stop="discuss"
    @keydown.enter.stop.prevent="discuss"
    @keydown.space.stop.prevent="discuss"
  >
    <q-icon name="terminal" size="13px" aria-hidden="true" />
    Discuss with Concierge
  </span>
  <q-btn
    v-else
    flat
    dense
    no-caps
    size="sm"
    color="primary"
    icon="terminal"
    label="Discuss with Concierge"
    class="discuss-with-concierge"
    data-test="discuss-with-concierge"
    @click="discuss"
  >
    <q-tooltip>Ask the Concierge to read this decision, summarise it and recommend an option</q-tooltip>
  </q-btn>
</template>

<style scoped>
.discuss-with-concierge--inline {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 0.75rem;
  color: var(--q-primary);
  cursor: pointer;
  text-decoration: underline;
}
</style>
