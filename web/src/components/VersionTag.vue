<script setup lang="ts">
import { computed } from 'vue';
import { bundleBuild, buildDetail, releaseLabel, type BuildInfo } from '../lib/buildInfo';

/**
 * The release, small and muted - only the dated tag, `v2026.09.24.1`, which is the same number the
 * operator CLI carries - with the full version, commit and build time on hover.
 * Used in the top bar beside the logo and on the login card below it. The bundle's own build
 * unless told otherwise.
 */
const props = withDefaults(defineProps<{ info?: BuildInfo }>(), { info: () => bundleBuild });

const label = computed(() => releaseLabel(props.info.version));
const detail = computed(() => buildDetail(props.info));
</script>

<template>
  <span class="version-tag" tabindex="0" :aria-label="`${label}. ${detail}`">
    {{ label }}
    <q-tooltip anchor="bottom middle" self="top middle" :offset="[0, 6]">{{ detail }}</q-tooltip>
  </span>
</template>

<style scoped>
/* Muted ink from the theme's own tokens, so it reads in light and dark alike. Mono, like every other
   number that identifies a build here. It may shrink and ellipsise:
   on a phone-width bar the logo, the live chip and the account button come first. */
.version-tag {
  display: inline-block;
  min-width: 0;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: 'IBM Plex Mono', monospace;
  font-size: 0.72rem;
  line-height: 1.2;
  letter-spacing: 0;
  color: var(--os-ink-muted);
  cursor: default;
}
</style>
