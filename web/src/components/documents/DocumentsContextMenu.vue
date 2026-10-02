<script setup lang="ts">
import { ref } from 'vue';
import type { DocumentsAction, DocumentsSortColumn, MenuEntry } from '../../lib/documentsExplorer';
import type { DocumentsView } from '../../lib/documentsPrefs';

/**
 * THE CONTEXT MENU, opened where the person right-clicked, long-pressed or pressed ⋮. What it
 * offers is `menuFor`'s answer, passed in; this only renders it. A refused entry stays on screen,
 * disabled, with its reason.
 */
defineProps<{
  entries: MenuEntry[];
  view: DocumentsView;
  sortColumn: DocumentsSortColumn;
  atRoot: boolean;
}>();

const emit = defineEmits<{
  pick: [action: DocumentsAction];
  view: [view: DocumentsView];
  sort: [column: DocumentsSortColumn];
}>();

const menu = ref<{ show: (event?: Event) => void; hide: () => void } | null>(null);

const Views: { view: DocumentsView; label: string; icon: string }[] = [
  { view: 'details', label: 'Details', icon: 'view_list' },
  { view: 'list', label: 'List', icon: 'view_headline' },
  { view: 'tiles', label: 'Tiles', icon: 'grid_view' },
];

const Sorts: { column: DocumentsSortColumn; label: string }[] = [
  { column: 'name', label: 'Name' },
  { column: 'kind', label: 'Kind' },
  { column: 'size', label: 'Size' },
  { column: 'modified', label: 'Modified' },
];

defineExpose({
  show: (event?: Event) => menu.value?.show(event),
  hide: () => menu.value?.hide(),
});
</script>

<template>
  <q-menu ref="menu" no-parent-event touch-position class="context-menu documents-menu">
    <q-list dense class="documents-menu-list">
      <template v-for="(entry, index) in entries" :key="index">
        <q-separator v-if="entry.kind === 'separator'" />
        <q-item
          v-else-if="entry.kind === 'action'"
          v-close-popup
          clickable
          :disable="!!entry.disabled"
          :data-action="entry.action"
          @click="emit('pick', entry.action)"
        >
          <q-item-section avatar><q-icon :name="entry.icon" size="18px" /></q-item-section>
          <q-item-section>
            <q-item-label>{{ entry.label }}</q-item-label>
            <q-item-label v-if="entry.disabled" caption>{{ entry.disabled }}</q-item-label>
          </q-item-section>
          <q-item-section v-if="entry.shortcut" side class="text-caption">{{ entry.shortcut }}</q-item-section>
        </q-item>
        <q-item v-else clickable :data-submenu="entry.id">
          <q-item-section avatar><q-icon :name="entry.icon" size="18px" /></q-item-section>
          <q-item-section>{{ entry.label }}</q-item-section>
          <q-item-section side><q-icon name="chevron_right" size="18px" /></q-item-section>
          <q-menu anchor="top end" self="top start" class="documents-menu">
            <q-list v-if="entry.id === 'view'" dense>
              <q-item v-for="choice in Views" :key="choice.view" v-close-popup clickable :active="view === choice.view" :data-view="choice.view" @click="emit('view', choice.view)">
                <q-item-section avatar><q-icon :name="choice.icon" size="18px" /></q-item-section>
                <q-item-section>{{ choice.label }}</q-item-section>
              </q-item>
            </q-list>
            <q-list v-else dense>
              <q-item v-for="choice in Sorts" :key="choice.column" v-close-popup clickable :active="sortColumn === choice.column" :data-sort="choice.column" @click="emit('sort', choice.column)">
                <q-item-section>{{ choice.label }}</q-item-section>
              </q-item>
              <q-item v-if="atRoot" v-close-popup clickable :active="sortColumn === 'team'" data-sort="team" @click="emit('sort', 'team')">
                <q-item-section>Team</q-item-section>
              </q-item>
            </q-list>
          </q-menu>
        </q-item>
      </template>
    </q-list>
  </q-menu>
</template>

<style scoped>
.documents-menu-list {
  min-width: 14rem;
}
</style>
