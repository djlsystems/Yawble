<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import IndexPage from './IndexPage.vue';
import SolutionsLauncher from '../components/SolutionsLauncher.vue';
import SolutionPanel from '../components/SolutionPanel.vue';
import { LauncherPath, panelPath } from '../lib/solutionPanel';

/**
 * THE SOLUTIONS SCREENS AS ADDRESSES: `#/solutions` is the Console with the launcher open over it,
 * `#/solutions/<team>` the same with that solution's control panel open. The ribbon button, a tile's
 * Manage, the board header's "Manage solution" and a link the Concierge gives all land here, and a
 * reload stays where it was.
 *
 * Closing either screen leaves for the plain Console; the panel's back arrow and a finished
 * uninstall go to the launcher.
 */
const route = useRoute();
const router = useRouter();

const team = computed(() => {
  const value = route.params.team;
  return (Array.isArray(value) ? value[0] : value) ?? '';
});

const launcherOpen = ref(false);
const panelOpen = ref(false);

/** Which screen the address names. Opened AFTER mount, so each screen's `watch(open)` sees its edge. */
function show() {
  launcherOpen.value = team.value === '';
  panelOpen.value = team.value !== '';
}

onMounted(show);
watch(team, show);

let leaving = false;

async function go(path: string) {
  leaving = true;
  await router.push(path);
  leaving = false;
}

watch([launcherOpen, panelOpen], ([launcher, panel]) => {
  if (!launcher && !panel && !leaving) void router.replace('/console');
});
</script>

<template>
  <IndexPage />
  <SolutionsLauncher v-model="launcherOpen" @manage="(next: string) => go(panelPath(next))" />
  <SolutionPanel v-if="team" v-model="panelOpen" :team="team" @launcher="go(LauncherPath)" />
</template>
