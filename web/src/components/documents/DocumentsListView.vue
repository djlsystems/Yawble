<script setup lang="ts">
import DocumentsName from './DocumentsName.vue';
import { useItemEvents, type DocumentsViewEmits, type DocumentsViewProps } from './itemEvents';

/**
 * LIST: compact rows, an icon and a name, flowing into as many columns as the pane is wide.
 */
const props = defineProps<DocumentsViewProps>();
const emit = defineEmits<DocumentsViewEmits>();
const { attrs, on } = useItemEvents(props, emit);
</script>

<template>
  <div class="documents-list" role="listbox" aria-multiselectable="true" aria-label="Documents">
    <div
      v-for="item in items"
      :key="item.key"
      class="documents-list-item row items-center no-wrap"
      role="option"
      v-bind="attrs(item)"
      v-on="on(item)"
    >
      <DocumentsName
        class="col"
        :item="item"
        :renaming="renaming === item.key"
        :error="renaming === item.key ? renameError : ''"
        @commit="emit('rename', item, $event)"
        @cancel="emit('renameCancel')"
      />
      <q-btn v-if="phone" flat dense round size="sm" icon="more_vert" aria-label="More" @click.stop="emit('menu', item, $event as MouseEvent)" />
    </div>
  </div>
</template>

<style scoped>
.documents-list {
  column-width: 16rem;
  column-gap: 12px;
  padding: 4px 8px;
}

.documents-list-item {
  break-inside: avoid;
  padding: 2px 6px;
  border-radius: 3px;
  cursor: default;
  user-select: none;
}
</style>
