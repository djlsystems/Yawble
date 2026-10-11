<script setup lang="ts">
/**
 * WHAT A FIELD IS FOR, BEHIND AN INFO ICON: the explanation a form used to print under a field, as
 * a tooltip, so a form shows its fields and not a page of small text. The icon is focusable and
 * carries the text as its label, so a keyboard or a screen reader gets it without hovering.
 *
 * Only explanation goes here. What a person must see now - an error, Required, a key that is not
 * set, a warning - stays on the form, in words.
 */
defineProps<{ text?: string | null | undefined }>();
</script>

<template>
  <q-icon
    v-if="text"
    name="info"
    size="1.125rem"
    class="field-help"
    tabindex="0"
    role="img"
    :aria-label="text"
    data-help
    @click.stop.prevent
    @mousedown.stop.prevent
  >
    <q-tooltip class="field-help-tooltip" max-width="22rem" :delay="150">{{ text }}</q-tooltip>
  </q-icon>
</template>

<style scoped>
.field-help {
  color: var(--os-ink-faint);
  cursor: help;
}

.field-help:hover,
.field-help:focus-visible {
  color: var(--os-primary);
}
</style>

<style>
.field-help-tooltip {
  font-size: 0.8125rem;
  line-height: 1.4;
}
</style>
