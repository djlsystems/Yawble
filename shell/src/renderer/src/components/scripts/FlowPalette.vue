<script setup lang="ts">
import { PALETTE } from '../../scripts/flowchart';

// The flowchart's palette: the blocks in groups (Flow, Data, Do); a click adds a blank step of that block's type.
// The Start node is the trigger, so it is not on the palette.
const emit = defineEmits<{ add: [type: string] }>();
</script>

<template>
  <div class="palette" data-test="flow-palette" aria-label="Blocks">
    <span class="muted small">Blocks</span>
    <div v-for="g in PALETTE" :key="g.group" class="group" role="group" :aria-label="g.group" :data-test="`palette-group-${g.group.toLowerCase()}`">
      <span class="muted tiny">{{ g.group }}</span>
      <button v-for="b in g.blocks" :key="b.key" type="button" class="item" :data-kind="b.kind" :data-test="`palette-${b.key}`"
        :title="b.type" @click="emit('add', b.type)">{{ b.label }}</button>
    </div>
  </div>
</template>

<style scoped>
.palette { display: flex; flex-direction: column; gap: 8px; width: 150px; flex: 0 0 auto; overflow: auto; }
.group { display: flex; flex-direction: column; gap: 3px; }
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
.tiny { font-size: 10.5px; text-transform: uppercase; letter-spacing: .04em; }
</style>
