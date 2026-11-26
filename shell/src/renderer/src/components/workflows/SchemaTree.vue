<script setup lang="ts">
import type { TreeField } from '../../workflows/schemaModel';

// A schema's fields as a read-only tree: each field's name, type, required, description and enum values; a field that
// refers to another schema (or is a list of one) links to it; an inline object's fields are nested under it.
defineProps<{ fields: TreeField[]; depth?: number }>();
const emit = defineEmits<{ open: [schema: string] }>();
</script>

<template>
  <ul class="tree" :class="{ nested: (depth ?? 0) > 0 }" data-test="schema-tree">
    <li v-for="f in fields" :key="f.name" data-test="schema-tree-field" :data-name="f.name">
      <div class="line">
        <span class="mono name">{{ f.name }}</span>
        <span class="mono type" data-test="schema-tree-type">{{ f.type }}{{ f.nullable ? ' or null' : '' }}</span>
        <span v-if="f.required" class="chip req" data-test="schema-tree-required">required</span>
        <button v-if="f.ref" type="button" class="link" data-test="schema-tree-ref" :title="`Open ${f.ref}`" @click="emit('open', f.ref!)">→ {{ f.ref }}</button>
      </div>
      <p v-if="f.description" class="desc" data-test="schema-tree-description">{{ f.description }}</p>
      <p v-if="f.enum?.length" class="enum" data-test="schema-tree-enum">One of: <span v-for="(v, i) in f.enum" :key="v" class="mono">{{ v }}{{ i < f.enum.length - 1 ? ', ' : '' }}</span></p>
      <SchemaTree v-if="f.children?.length" :fields="f.children" :depth="(depth ?? 0) + 1" @open="(s) => emit('open', s)" />
    </li>
  </ul>
</template>

<style scoped>
.tree { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; font-size: 13px; }
.tree.nested { border-left: 1px solid var(--line-soft); margin: 4px 0 0 6px; padding-left: 10px; }
.line { display: flex; flex-wrap: wrap; align-items: baseline; gap: 8px; }
.name { font-weight: 600; }
.type { color: var(--accent); }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
.chip { font-size: 11px; background: var(--chip); color: var(--ink-soft); border-radius: 10px; padding: 0 7px; }
.link { border: 0; background: transparent; color: var(--accent); cursor: pointer; padding: 0; font: inherit; font-size: 12px; }
.link:hover { text-decoration: underline; }
.desc, .enum { margin: 1px 0 0; color: var(--ink-soft); font-size: 12px; }
</style>
