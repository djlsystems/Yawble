<script setup lang="ts">
import { sizeText } from '../../lib/documentsExplorer';
import DocumentsName from './DocumentsName.vue';
import { useItemEvents, type DocumentsViewEmits, type DocumentsViewProps } from './itemEvents';

/**
 * TILES: a large icon, the name, then the size or how many things are in a folder. The grid is the
 * shared one (`os-tiles` / `os-tile`, css/tiles.scss), as Agents and Plugins lay theirs out.
 */
const props = defineProps<DocumentsViewProps>();
const emit = defineEmits<DocumentsViewEmits>();
const { attrs, on } = useItemEvents(props, emit);
</script>

<template>
  <div class="os-tiles documents-tiles" role="listbox" aria-multiselectable="true" aria-label="Documents">
    <div
      v-for="item in items"
      :key="item.key"
      class="os-tile documents-tile"
      role="option"
      v-bind="attrs(item)"
      v-on="on(item)"
    >
      <div class="os-tile-head documents-tile-head">
        <q-icon :name="item.icon" size="40px" class="documents-tile-icon" />
        <q-space />
        <q-btn v-if="narrow" flat dense round size="sm" icon="more_vert" aria-label="More" @click.stop="emit('menu', item, $event as MouseEvent)" />
      </div>
      <div v-if="renaming === item.key" class="os-tile-line">
        <DocumentsName :item="item" renaming :error="renameError" @commit="emit('rename', item, $event)" @cancel="emit('renameCancel')" />
      </div>
      <div v-else class="os-tile-line documents-tile-name ellipsis-2-lines">{{ item.name }}</div>
      <div class="os-tile-line os-text-muted">{{ sizeText(item) }}</div>
    </div>
  </div>
</template>

<style scoped>
.documents-tiles {
  --os-tile-min: 9rem;
  padding: 8px;
}

.documents-tile {
  cursor: default;
  user-select: none;
}

.documents-tile-icon {
  color: var(--os-ink-muted);
}

.documents-tile-name {
  font-weight: 500;
}
</style>
