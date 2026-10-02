<script setup lang="ts">
import { computed, inject } from 'vue';
import { documentsDragKey } from '../../lib/useDocumentsDrag';

/**
 * THE HINT BESIDE THE POINTER while dragging: what a drop here would do ("Move 3 items to
 * reports", "Copy …", "Upload 2 files to …"), or why it would not, with a no-entry icon. A live
 * region, so a screen reader hears the same sentence.
 */
const drag = inject(documentsDragKey, null);

const showing = computed(() => !!drag && drag.active.value && drag.hint.value !== '');
const refused = computed(() => !!drag && !drag.verdict.value.ok);
const style = computed(() =>
  drag ? { left: `${drag.point.value.x + 16}px`, top: `${drag.point.value.y + 16}px` } : {},
);
</script>

<template>
  <div role="status" aria-live="polite" class="documents-drop-hint-region">
    <div v-if="showing" class="documents-drop-hint row items-center no-wrap" :class="{ 'documents-drop-hint-refused': refused }" :style="style">
      <q-icon :name="refused ? 'block' : drag?.mode.value === 'copy' ? 'add' : 'drive_file_move'" size="16px" class="q-mr-xs" />
      <span>{{ drag?.hint.value }}</span>
    </div>
  </div>
</template>

<style scoped>
.documents-drop-hint-region {
  position: fixed;
  left: 0;
  top: 0;
  width: 0;
  height: 0;
  z-index: 9000;
  pointer-events: none;
}

.documents-drop-hint {
  position: fixed;
  max-width: 22rem;
  padding: 4px 10px;
  border-radius: 4px;
  background: var(--os-surface);
  color: var(--os-ink);
  border: 1px solid var(--os-rule-strong);
  font-size: 12px;
  box-shadow: 0 2px 6px rgba(0, 0, 0, 0.2);
}

.documents-drop-hint-refused {
  border-color: var(--q-negative);
  color: var(--q-negative);
}
</style>
