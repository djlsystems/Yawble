<script setup lang="ts">
import { STEP_TYPES } from '../../scripts/flowchart';

// The flowchart's palette: one button per step type the checker accepts; a click adds a blank step of that type.
const emit = defineEmits<{ add: [type: string] }>();
</script>

<template>
  <div class="palette" data-test="flow-palette" aria-label="Add a step">
    <span class="muted small">Add a step</span>
    <button v-for="t in STEP_TYPES" :key="t.type" type="button" class="item" :data-kind="t.kind" :data-test="`palette-${t.type}`"
      :title="t.type" @click="emit('add', t.type)">{{ t.label }}</button>
  </div>
</template>

<style scoped>
.palette { display: flex; flex-direction: column; gap: 3px; width: 150px; flex: 0 0 auto; overflow: auto; }
.item { text-align: left; font-size: 12px; padding: 3px 8px; border: 1px solid var(--line); border-radius: 5px; background: var(--surface); color: var(--ink); }
.item:hover { background: var(--hover); }
.item[data-kind=integration] { border-left: 4px solid var(--pill-blue-ink); }
.item[data-kind=if], .item[data-kind=switch] { border-left: 4px solid var(--pill-yellow-ink); }
.item[data-kind=forEach] { border-left: 4px solid var(--pill-violet-ink); }
.item[data-kind=set] { border-left: 4px solid var(--pill-info-ink); }
.item[data-kind=file] { border-left: 4px solid var(--chip-claude-ink); }
.item[data-kind=end] { border-left: 4px solid var(--pill-green-ink); }
.item[data-kind=other] { border-left: 4px solid var(--pill-neutral-ink); }
.small { font-size: 11.5px; }
</style>
