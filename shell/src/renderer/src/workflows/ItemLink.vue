<script setup lang="ts">
import type { WorkflowItem } from '../../../shared/api';
import { openBacklogItem } from './page';

/**
 * B104 / B098: the backlog item a workflow came from, as a link that opens the Backlog with that item selected (an
 * archived one in the archive view). Plain text when the session may not read the backlog (`backlog: false`, the
 * core's flag); "—" when the workflow was not dispatched from the backlog (unless `dash` is false). `nested`: inside a
 * row that is itself a button (a team tile's workflow), a focusable span with role=link instead of a nested button.
 */
const props = withDefaults(defineProps<{ item: WorkflowItem | null | undefined; backlog?: boolean; dash?: boolean; nested?: boolean }>(),
  { backlog: true, dash: true, nested: false });
const open = () => { if (props.item) openBacklogItem(props.item.label, props.item.archived); };
const tip = (i: WorkflowItem) => `${i.label} ${i.title}${i.archived ? ' (archived)' : ''}: open it in the Backlog`;
</script>

<template>
  <template v-if="item && backlog">
    <span v-if="nested" class="item-link" role="link" tabindex="0" data-test="workflow-item-link" :data-label="item.label" :title="tip(item)"
      @click.stop.prevent="open" @keydown.enter.stop.prevent="open" @keydown.space.stop.prevent="open">{{ item.label }}</span>
    <button v-else type="button" class="item-link" data-test="workflow-item-link" :data-label="item.label" :title="tip(item)" @click.stop="open">{{ item.label }}</button>
  </template>
  <span v-else-if="item" class="item-plain" data-test="workflow-item-text" :title="item.title">{{ item.label }}</span>
  <span v-else-if="dash" class="item-none" data-test="workflow-item-none" title="Not dispatched from the backlog">—</span>
</template>

<style scoped>
.item-link { border: 0; background: transparent; padding: 0; color: var(--accent); font: inherit; font-family: ui-monospace, Consolas, monospace; font-size: 12px; cursor: pointer; }
.item-link:hover { text-decoration: underline; }
.item-link:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; border-radius: 3px; }
.item-plain { font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
.item-none { color: var(--muted); }
</style>
