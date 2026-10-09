<script setup lang="ts">
/**
 * THE FILTER BOX: the dense, outlined, clearable search input the tiled dialogs put above their
 * tiles (Plugins, Agents, Marketplace). It only holds the text; each dialog matches its own tiles with
 * `lib/filterWords`. Cleared reads as '' - never null - so a dialog's matcher never sees null.
 *
 * Attributes fall through to the input, so each dialog keeps its own `data-*` marker, class,
 * placeholder and aria-label on the one input.
 */
const text = defineModel<string>({ required: true });
</script>

<template>
  <q-input
    :model-value="text"
    dense
    outlined
    clearable
    @update:model-value="(value) => (text = String(value ?? ''))"
  >
    <template #prepend><q-icon name="search" /></template>
  </q-input>
</template>
