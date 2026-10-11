<script setup lang="ts">
import { computed } from 'vue';
import { vResizableColumns } from '../../lib/resizableColumns';
import { ariaSort } from '../../lib/tableSort';
import { modifiedText, sizeText, type DocumentsSortColumn } from '../../lib/documentsExplorer';
import DocumentsName from './DocumentsName.vue';
import { useItemEvents, type DocumentsViewEmits, type DocumentsViewProps } from './itemEvents';

/**
 * DETAILS: a table with sortable, resizable columns - Name, Kind, Size, Modified, and Team at the
 * root. The headers are the Teams table's pattern: `aria-sort` on the cell, a button inside it,
 * the arrow only on the column sorted by. At phone width only Name and Size are shown.
 */
const props = defineProps<DocumentsViewProps>();
const emit = defineEmits<DocumentsViewEmits>();
const { attrs, on } = useItemEvents(props, emit);

const columns = computed(() => {
  const all: { key: DocumentsSortColumn; label: string }[] = [
    { key: 'name', label: 'Name' },
    ...(props.atRoot ? [{ key: 'team' as const, label: 'Team' }] : [{ key: 'kind' as const, label: 'Kind' }]),
    { key: 'size', label: 'Size' },
    { key: 'modified', label: 'Modified' },
  ];

  return props.narrow ? all.filter((column) => column.key === 'name' || column.key === 'size') : all;
});
</script>

<template>
  <q-markup-table v-resizable-columns="'documents'" flat dense separator="none" class="documents-details">
    <thead>
      <tr>
        <th
          v-for="column in columns"
          :key="column.key"
          :data-col="column.key"
          class="text-left"
          :aria-sort="ariaSort(sort, column.key)"
        >
          <q-btn flat dense no-caps class="documents-sort" :aria-label="`Sort by ${column.label}`" @click="emit('sort', column.key)">
            {{ column.label }}
            <q-icon
              v-if="sort.column === column.key"
              :name="sort.descending ? 'arrow_downward' : 'arrow_upward'"
              size="14px"
            />
          </q-btn>
        </th>
        <th v-if="narrow" class="documents-more-col" aria-label="Actions" />
      </tr>
    </thead>
    <tbody>
      <tr v-for="item in items" :key="item.key" class="documents-row" v-bind="attrs(item)" v-on="on(item)">
        <td class="documents-cell-name">
          <DocumentsName
            :item="item"
            :renaming="renaming === item.key"
            :error="renaming === item.key ? renameError : ''"
            @commit="emit('rename', item, $event)"
            @cancel="emit('renameCancel')"
          />
        </td>
        <!-- Kind, or at the root whether the team is live, gone or superseded. -->
        <td v-if="!narrow" class="os-text-muted">{{ item.kind }}</td>
        <td class="os-text-muted">{{ sizeText(item) }}</td>
        <td v-if="!narrow" class="os-text-muted">{{ modifiedText(item.modifiedAt) }}</td>
        <td v-if="narrow" class="documents-more-col">
          <q-btn flat dense round size="sm" icon="more_vert" aria-label="More" @click.stop="emit('menu', item, $event as MouseEvent)" />
        </td>
      </tr>
    </tbody>
  </q-markup-table>
</template>

<style scoped>
/* NOT A SCROLLER OF ITS OWN. Quasar's table scrolls sideways inside its wrapper, whose scrollbar is
   under the last row - out of sight in a long folder, so a narrow window just cut the last column
   off. The pane scrolls both ways instead, its scrollbar at the bottom of what is visible. */
.documents-details {
  background: transparent;
  overflow: visible;
}

.documents-row {
  cursor: default;
  user-select: none;
}

.documents-cell-name {
  max-width: 28rem;
}

.documents-sort {
  font-weight: 500;
}

.documents-more-col {
  width: 40px;
}
</style>
