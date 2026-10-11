<script setup lang="ts">
import { onBeforeUnmount, watch } from 'vue';
import { useQuasar } from 'quasar';
import { useAppearanceStore } from './stores/appearance';
import { useSessionStore } from './stores/session';
import { apply } from './lib/appearance';

// THE PERSON'S APPEARANCE, applied here because this is the one component both the front door and
// the console render under. `immediate` paints this browser's copy before the first route does,
// rather than the default flashing first; the server's answer replaces it once someone is signed in.
const $q = useQuasar();
const appearance = useAppearanceStore();
const session = useSessionStore();

// Match system follows the computer's setting, live.
const media = typeof window !== 'undefined' && window.matchMedia
  ? window.matchMedia('(prefers-color-scheme: dark)')
  : null;
const followSystem = () => (appearance.systemDark = media?.matches ?? false);
followSystem();
media?.addEventListener('change', followSystem);
onBeforeUnmount(() => media?.removeEventListener('change', followSystem));

watch(
  () => appearance.resolved,
  (resolved) => {
    $q.dark.set(resolved.dark);
    apply(resolved);
  },
  { immediate: true, deep: true },
);

watch(
  () => session.user?.id,
  (id) => {
    if (id) void appearance.load().catch(() => undefined);
  },
  { immediate: true },
);
</script>

<template>
  <router-view />
</template>
