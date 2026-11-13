<script setup lang="ts">
// The one "action in progress" label, inside a button whose click awaits the core: while `busy` it shows the shared
// spinner (.btn-spinner, styles.css) and the present-participle text ("Saving…"); otherwise the button's own label
// (the slot). An icon-only button passes no text: the spinner takes the icon's place. The button itself is disabled
// by its owner while busy (useBusy's run() also ignores a second click).
defineProps<{ busy: boolean; text?: string }>();
</script>

<template>
  <template v-if="busy"><span class="btn-spinner" :class="{ solo: !text }" aria-hidden="true" data-test="btn-spinner"></span>{{ text ?? '' }}</template>
  <slot v-else />
</template>
