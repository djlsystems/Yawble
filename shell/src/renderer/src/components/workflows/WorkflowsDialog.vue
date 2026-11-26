<script setup lang="ts">
import { ref } from 'vue';
import type { TeamSummary, YawbleApi } from '../../../../shared/api';
import Modal from '../Modal.vue';
import RunsTab from './RunsTab.vue';
import SchemasTab from './SchemasTab.vue';
import WorkflowsLibrary from './WorkflowsLibrary.vue';

// The Workflows place: three tabs, Workflows (the library and the editor), Schemas
// and Runs. `page`: drawn as the app's Workflows place (inline, filling it), as Logs is; otherwise a dialog.
const props = defineProps<{ api: YawbleApi; teams: readonly TeamSummary[]; page?: boolean; tab?: 'workflows' | 'schemas' | 'runs' }>();
const emit = defineEmits<{ close: [] }>();
const tab = ref<'workflows' | 'schemas' | 'runs'>(props.tab ?? 'workflows');
const tabs = [
  { id: 'workflows', label: 'Workflows' }, { id: 'schemas', label: 'Schemas' }, { id: 'runs', label: 'Runs' },
] as const;
</script>

<template>
  <component :is="page ? 'section' : Modal" v-bind="page ? { class: 'workflows-page', 'aria-label': 'Workflows' } : { title: 'Workflows', full: true }" @close="emit('close')">
    <div class="workflows" :class="{ page }" data-test="workflows">
      <div class="tabs" role="tablist" aria-label="Workflows">
        <button v-for="t in tabs" :key="t.id" type="button" role="tab" :aria-selected="tab === t.id" :class="{ on: tab === t.id }"
          :data-test="`workflows-tab-${t.id}`" @click="tab = t.id">{{ t.label }}</button>
      </div>
      <div class="panel" role="tabpanel">
        <WorkflowsLibrary v-if="tab === 'workflows'" :api="api" :teams="teams" />
        <SchemasTab v-else-if="tab === 'schemas'" :api="api" />
        <RunsTab v-else :api="api" />
      </div>
    </div>
  </component>
</template>

<style scoped>
.workflows-page { flex: 1 1 0; height: 100%; min-height: 0; display: flex; flex-direction: column; }
.workflows { display: flex; flex-direction: column; gap: 10px; min-height: 0; flex: 1 1 0; }
.workflows.page { padding: 12px 16px; overflow: auto; }
.tabs { display: flex; gap: 2px; border-bottom: 1px solid var(--line); flex: none; }
.tabs button { border: 0; border-bottom: 2px solid transparent; border-radius: 0; background: transparent; padding: 6px 12px; color: var(--muted); }
.tabs button.on { color: var(--ink); border-bottom-color: var(--accent); }
.panel { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; }
</style>
