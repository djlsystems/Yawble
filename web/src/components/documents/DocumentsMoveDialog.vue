<script setup lang="ts">
import { ref, watch } from 'vue';
import type { DocumentsFolder } from '../../api/types';
import { RootLocation, type DocumentsLocation, type DropTarget } from '../../lib/documentsExplorer';
import DocumentsTree from './DocumentsTree.vue';

/**
 * MOVE TO…: a folder picker over the same tree. A gone or superseded team's folder is shown and
 * cannot be chosen - nothing can be added to it.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{
  folders: DocumentsFolder[];
  count: number;
  start: DocumentsLocation;
}>();

const emit = defineEmits<{ move: [to: DropTarget] }>();

const picked = ref<DocumentsLocation>(RootLocation);

watch(open, (showing) => {
  if (showing) picked.value = props.start;
});

function move() {
  if (picked.value.folder === null) return;

  emit('move', { folder: picked.value.folder, path: picked.value.path });
  open.value = false;
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-md documents-move">
      <q-card-section>
        <div class="os-dialog-title">Move {{ count }} item{{ count === 1 ? '' : 's' }} to…</div>
      </q-card-section>
      <q-card-section class="q-pt-none documents-move-tree">
        <DocumentsTree :folders="folders" :location="picked" picker @open="picked = $event" />
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="open = false" />
        <q-btn unelevated no-caps color="primary" label="Move" data-move="confirm" :disable="picked.folder === null" @click="move" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.documents-move-tree {
  max-height: 50vh;
  overflow: auto;
}
</style>
