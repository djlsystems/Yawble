<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import IndexPage from './IndexPage.vue';
import SolutionWizard from '../components/SolutionWizard.vue';

/**
 * THE INSTALL DEEP LINK, `#/solutions/install?folder=<absolute path>`: the Console, with the solution
 * wizard open over it and filled in with that folder.
 *
 * The check is sent with `from: 'link'`, so the Host also refuses a folder outside the instance's
 * documents and every team's folder; that refusal is the wizard's first screen and nothing else
 * happens. NOTHING IS INSTALLED BY OPENING THIS: the person reviews and presses Install. Closing the
 * wizard leaves the link for the plain Console, so a reload does not open it again. Another link
 * followed while the wizard is open reopens it on that link's folder.
 */
const route = useRoute();
const router = useRouter();

const folder = computed(() => {
  const value = route.query.folder;
  return (Array.isArray(value) ? value[0] : value) ?? '';
});

const open = ref(false);

// Opened AFTER mount, so the wizard's `watch(open)` sees the opening edge and checks the folder.
onMounted(() => {
  open.value = true;
});

// A SECOND LINK IN THE SAME TAB is the same route with a new query: the page is reused, so the
// wizard is closed and opened again on the new folder, which it checks from the start.
let reopening = false;

watch(folder, async (next, previous) => {
  if (next === previous || !open.value) return;
  reopening = true;
  open.value = false;
  await nextTick();
  open.value = true;
  reopening = false;
});

watch(open, (showing) => {
  if (!showing && !reopening) void router.replace('/console');
});
</script>

<template>
  <IndexPage />
  <SolutionWizard v-model="open" :folder="folder" from="link" />
</template>
