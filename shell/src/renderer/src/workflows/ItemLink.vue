<script setup lang="ts">
import type { WorkflowItem } from '../../../shared/api';
import { highlight, openBacklogItem } from './page';

/**
 * B104 / B098: the backlog item a workflow came from, as a link that opens the Backlog with that item selected (an
 * archived one in the archive view). Plain text when the session may not read the backlog (`backlog: false`, the
 * core's flag); "—" when the workflow was not dispatched from the backlog (unless `dash` is false). `nested`: inside a
 * row that is itself a button (a team tile's workflow), a focusable span with role=link instead of a nested button.
 * `q`: the Workflows page's text filter; the part of the label it matches is marked, as in the page's other cells.
 */
const props = withDefaults(defineProps<{ item: WorkflowItem | null | undefined; backlog?: boolean; dash?: boolean; nested?: boolean; q?: string }>(),
  { backlog: true, dash: true, nested: false, q: '' });
const open = () => { if (props.item) openBacklogItem(props.item.label, props.item.archived); };
const tip = (i: WorkflowItem) => `${i.label} ${i.title}${i.archived ? ' (archived)' : ''}: open it in the Backlog`;
</script>

<template>
  <template v-if="item && backlog">
    <span v-if="nested" class="item-link" role="link" tabindex="0" data-test="workflow-item-link" :data-label="item.label" :title="tip(item)"
      @click.stop.prevent="open" @keydown.enter.stop.prevent="open" @keydown.space.stop.prevent="open"><template v-for="(p, i) in highlight(item.label, q)" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></span>
    <button v-else type="button" class="item-link" data-test="workflow-item-link" :data-label="item.label" :title="tip(item)" @click.stop="open"><template v-for="(p, i) in highlight(item.label, q)" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></button>
  </template>
  <span v-else-if="item" class="item-plain" data-test="workflow-item-text" :title="item.title"><template v-for="(p, i) in highlight(item.label, q)" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></span>
  <span v-else-if="dash" class="item-none" data-test="workflow-item-none" title="Not dispatched from the backlog">—</span>
</template>

<style scoped>
.item-link { border: 0; background: transparent; padding: 0; color: var(--accent); font: inherit; font-family: ui-monospace, Consolas, monospace; font-size: 12px; cursor: pointer; }
.item-link:hover { text-decoration: underline; }
.item-link:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; border-radius: 3px; }
.item-plain { font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
.item-none { color: var(--muted); }
mark { background: color-mix(in srgb, var(--pill-yellow-bg) 85%, transparent); color: inherit; border-radius: 2px; padding: 0 1px; box-shadow: 0 0 0 1px color-mix(in srgb, var(--pill-yellow-ink) 25%, transparent); }
</style>
