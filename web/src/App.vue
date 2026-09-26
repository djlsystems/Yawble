<script setup lang="ts">
import { watch } from 'vue';
import { useQuasar } from 'quasar';
import { useDisplayStore } from './stores/display';
import { darkSetting } from './lib/theme';

// THE VIEWER'S THEME, applied here because this is the one component both the front door and the
// console render under. `immediate` is the hydrate half: the stored choice is on screen before the
// first route paints, rather than the default flashing first. The watch is the change half - the
// account menu writes the store, and this is what turns that into `body--dark`.
const $q = useQuasar();
const display = useDisplayStore();

watch(
  () => display.theme,
  (theme) => $q.dark.set(darkSetting(theme)),
  { immediate: true },
);
</script>

<template>
  <router-view />
</template>
