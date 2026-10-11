<script setup lang="ts" generic="T extends string">
/**
 * THE ONE TAB STRIP every dialog uses, so tabs look and overflow the same everywhere.
 *
 * - Left-aligned at every width: `:breakpoint="0"`, or Quasar stretches each tab across the strip
 *   once it is under 600px wide, and two tabs read as two halves.
 * - A strip wider than its dialog shows an arrow at each end (`outside-arrows`), on touch screens
 *   too (`mobile-arrows`), so a tab past the edge is never simply cut off with nothing saying
 *   there is more. Each dialog is sized so its tabs fit at its usual width; the arrows are for a
 *   narrow window or a phone.
 * - Labels as written (`no-caps`), muted, the open tab in the primary colour with its underline.
 *
 * Extra classes and `data-` attributes on `<DialogTabs>` land on the strip. Pinned by
 * `__tests__/dialog-tabs.spec.ts`, which also refuses a bare `<q-tabs>` anywhere else.
 */
const model = defineModel<T>({ required: true });
</script>

<template>
  <q-tabs
    v-model="model"
    dense
    no-caps
    align="left"
    :breakpoint="0"
    outside-arrows
    mobile-arrows
    active-color="primary"
    indicator-color="primary"
    class="os-text-muted"
  >
    <slot />
  </q-tabs>
</template>
