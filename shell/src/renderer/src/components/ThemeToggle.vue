<script setup lang="ts">
import { computed } from 'vue';
import { theme as pageTheme, toggleLabel, type ThemeController } from '../theme';

// The title bar's theme button, right of the Cloud Link indicator (the same size and look). It shows the theme one
// click gives: a sun in the dark theme ("Switch to light theme"), a moon in the light one ("Switch to dark theme").
const props = defineProps<{ theme?: ThemeController }>();
const t = props.theme ?? pageTheme;
const label = computed(() => toggleLabel(t.state.current));
</script>

<template>
  <button class="theme-toggle" type="button" data-test="theme-toggle" :data-theme-shown="t.state.current" :title="label" :aria-label="label" @click="t.toggle()">
    <svg v-if="t.state.current === 'dark'" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"
      stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" data-test="theme-icon-sun">
      <path d="M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4" />
    </svg>
    <svg v-else width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"
      stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" data-test="theme-icon-moon">
      <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z" />
    </svg>
  </button>
</template>

<style scoped>
/* As the Cloud Link indicator (App.vue .cloud-ind). */
.theme-toggle { display: inline-flex; align-items: center; padding: 6px 8px; border: 0; border-radius: 8px; background: transparent; color: var(--muted);
  -webkit-app-region: no-drag; }
.theme-toggle:hover { background: var(--hover); color: var(--ink); }
.theme-toggle svg { display: block; flex: none; }
</style>
