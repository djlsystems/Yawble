<script setup lang="ts">
import { ref } from 'vue';
import type { TeamSummary, YawbleApi } from '../../../../shared/api';
import Modal from '../Modal.vue';
import RunsTab from './RunsTab.vue';
import SchemasTab from './SchemasTab.vue';
import ScriptsLibrary from './ScriptsLibrary.vue';

// The Scripts place: three tabs, Scripts (the library and the editor), Schemas
// and Runs. `page`: drawn as the app's Scripts place (inline, filling it), as Logs is; otherwise a dialog.
const props = defineProps<{ api: YawbleApi; teams: readonly TeamSummary[]; page?: boolean; tab?: 'scripts' | 'schemas' | 'runs' }>();
const emit = defineEmits<{ close: [] }>();
const tab = ref<'scripts' | 'schemas' | 'runs'>(props.tab ?? 'scripts');
const tabs = [
  { id: 'scripts', label: 'Scripts' }, { id: 'schemas', label: 'Schemas' }, { id: 'runs', label: 'Runs' },
] as const;
</script>

<template>
  <component :is="page ? 'section' : Modal" v-bind="page ? { class: 'scripts-page', 'aria-label': 'Scripts' } : { title: 'Scripts', full: true }" @close="emit('close')">
    <div class="scripts" :class="{ page }" data-test="scripts">
      <div class="tabs" role="tablist" aria-label="Scripts">
        <button v-for="t in tabs" :key="t.id" type="button" role="tab" :aria-selected="tab === t.id" :class="{ on: tab === t.id }"
          :data-test="`scripts-tab-${t.id}`" @click="tab = t.id">{{ t.label }}</button>
      </div>
      <div class="panel" role="tabpanel">
        <ScriptsLibrary v-if="tab === 'scripts'" :api="api" :teams="teams" />
        <SchemasTab v-else-if="tab === 'schemas'" :api="api" />
        <RunsTab v-else :api="api" />
      </div>
    </div>
  </component>
</template>

<style scoped>
.scripts-page { flex: 1 1 0; height: 100%; min-height: 0; display: flex; flex-direction: column; }
.scripts { display: flex; flex-direction: column; gap: 10px; min-height: 0; flex: 1 1 0; }
.scripts.page { padding: 12px 16px; overflow: auto; }
.tabs { display: flex; gap: 2px; border-bottom: 1px solid var(--line); flex: none; }
.tabs button { border: 0; border-bottom: 2px solid transparent; border-radius: 0; background: transparent; padding: 6px 12px; color: var(--muted); }
.tabs button.on { color: var(--ink); border-bottom-color: var(--accent); }
.panel { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; }
</style>
