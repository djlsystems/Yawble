<script setup lang="ts">
import { computed } from 'vue';
import type { WorkflowItem } from '../api/types';
import { asTeamId } from '../api/types';
import { itemLabel } from '../lib/backlog';
import { workflowNumberTitle } from '../lib/workflowNumber';
import { useWorkflowNavStore } from '../stores/workflowNav';

/**
 * A WORKFLOW NAMED BY ITS WORK: the item's chip, then the words, then the number.
 *
 * The words are the item's title, else the subject of the instruction that began it; with neither,
 * the number stands alone, as it always did. Nothing is estimated and the number is never dropped:
 * it is what agents quote. Text interpolation only, never markup - a title is a person's typing.
 *
 * The chip opens the Backlog dialog on the item; the number, given the team, opens that team's
 * Workflows dialog. Both stop the click, so a row that toggles on a click does not toggle too.
 * A place whose number already does something else (the feed's Follow) passes it in the `number`
 * slot, which must still show the number.
 */
const props = defineProps<{
  number: number;
  item?: WorkflowItem | null | undefined;
  subject?: string | null | undefined;
  /** The team the workflow is in. Without it the number is not a way in. */
  team?: string | null | undefined;
  /** A word before the number when it stands alone (`workflow` reads `workflow #45`). */
  noun?: string | undefined;
}>();

const nav = useWorkflowNavStore();

const words = computed(() => {
  const title = props.item?.title?.trim();
  if (title) return title;
  const subject = props.subject?.trim();
  return subject ? subject : null;
});

const alone = computed(() => !props.item && words.value === null);

const numberText = computed(() =>
  alone.value && props.noun ? `${props.noun} #${props.number}` : `#${props.number}`,
);

function openItem() {
  if (props.item) nav.openItem(props.item.id);
}

function openWorkflows() {
  if (props.team) nav.openWorkflows(asTeamId(props.team));
}
</script>

<template>
  <span class="workflow-name" data-workflow-name>
    <button
      v-if="item"
      type="button"
      class="k-card-item workflow-name-item"
      :title="item.title"
      data-workflow-item
      @click.stop="openItem"
    >{{ itemLabel(item.id) }}</button>
    <span v-if="words !== null" class="workflow-name-words" :title="words" data-workflow-words>{{ words }}</span>
    <!-- THE NUMBER, or what the place puts in its stead that still shows it (the feed's Follow). -->
    <slot name="number">
      <button
        v-if="team"
        type="button"
        class="workflow-name-number"
        :class="{ 'workflow-name-muted': !alone }"
        :title="workflowNumberTitle(number)"
        data-workflow-number
        @click.stop="openWorkflows"
      >{{ numberText }}</button>
      <span
        v-else
        class="workflow-name-number"
        :class="{ 'workflow-name-muted': !alone }"
        :title="workflowNumberTitle(number)"
        data-workflow-number
      >{{ numberText }}</span>
    </slot>
  </span>
</template>

<style scoped>
/* ONE LINE, whatever the words: the words give way with an ellipsis, the chip and number never. */
.workflow-name {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
  max-width: 100%;
  vertical-align: baseline;
}

/* The Kanban card's own item style, as a button. */
.k-card-item {
  font-size: 0.625rem;
  font-weight: 600;
  letter-spacing: 0.04em;
  color: var(--os-ink-muted);
}

.workflow-name-item {
  flex: 0 0 auto;
  padding: 0 4px;
  border: 1px solid var(--os-rule-strong);
  border-radius: 4px;
  background: transparent;
  font-family: inherit;
  line-height: 14px;
  cursor: pointer;
}

.workflow-name-words {
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.workflow-name-number {
  flex: 0 0 auto;
  padding: 0;
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  white-space: nowrap;
}

button.workflow-name-number {
  cursor: pointer;
}

button.workflow-name-number:hover,
.workflow-name-item:hover {
  text-decoration: underline;
}

.workflow-name-muted {
  color: var(--os-ink-muted);
}
</style>
