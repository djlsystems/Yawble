<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { HostCheck, YawbleApi } from '../../../shared/api';

// The core's startup probe: on the main window, so a person sees at once what this computer lacks
// (Linux: the AppArmor profile, bwrap, slirp4netns) and the command that fixes it. Nothing shows when every check
// passes. data-state says when the probe has been read, for the installed-app check.
const props = defineProps<{ api: YawbleApi }>();
const failed = ref<HostCheck[]>([]);
const state = ref<'pending' | 'read' | 'unavailable'>('pending');

onMounted(async () => {
  try {
    failed.value = ((await props.api.getInfo()).hostChecks ?? []).filter((c) => !c.ok);
    state.value = 'read';
  } catch {
    // The header already says when the core is unavailable.
    state.value = 'unavailable';
  }
});
</script>

<template>
  <div data-test="host-checks-probe" :data-state="state">
    <section v-if="failed.length" class="host-checks" data-test="host-checks">
      <h2>This computer is not ready to run agents</h2>
      <ul>
        <li v-for="c in failed" :key="c.id" :data-test="`host-check-${c.id}`">
          {{ c.message }} <strong v-if="c.fix">{{ c.fix }}</strong>
        </li>
      </ul>
    </section>
  </div>
</template>

<style scoped>
.host-checks { border: 1px solid var(--bad-line); background: var(--bad-bg); color: var(--ink); padding: 4px 18px; margin: 0; }
.host-checks h2 { font-size: 15px; margin: 8px 0 4px; color: var(--bad); }
.host-checks ul { margin: 4px 0 8px; }
</style>
