<script setup lang="ts">
import { nextTick, ref, watch } from 'vue';
import type { ExplorerItem } from '../../lib/documentsExplorer';

/**
 * AN ITEM'S ICON AND NAME, or the inline rename box in its place. Shared by the three views so
 * renaming looks and behaves the same in each.
 *
 * The box opens with the name selected WITHOUT its extension, as Finder and Explorer do. Enter
 * commits, Escape cancels, and no key typed here reaches the view's keyboard map.
 */
const props = defineProps<{ item: ExplorerItem; renaming: boolean; error: string; iconSize?: string }>();
const emit = defineEmits<{ commit: [name: string]; cancel: [] }>();

const draft = ref(props.item.name);
const input = ref<HTMLInputElement | null>(null);

watch(
  () => props.renaming,
  async (renaming) => {
    if (!renaming) return;

    draft.value = props.item.name;
    await nextTick();
    const box = input.value;
    if (!box) return;

    box.focus();
    const dot = props.item.isFolder ? -1 : props.item.name.lastIndexOf('.');
    box.setSelectionRange(0, dot > 0 ? dot : props.item.name.length);
  },
  { immediate: true },
);

function onKeydown(event: KeyboardEvent) {
  event.stopPropagation();

  if (event.key === 'Enter') {
    event.preventDefault();
    emit('commit', draft.value);
  } else if (event.key === 'Escape') {
    event.preventDefault();
    emit('cancel');
  }
}
</script>

<template>
  <span class="documents-name row items-center no-wrap">
    <q-icon :name="item.icon" :size="iconSize ?? '18px'" class="documents-name-icon" :class="{ 'documents-name-gone': item.icon === 'folder_off' }" />
    <span v-if="!renaming" class="documents-name-text ellipsis">{{ item.name }}</span>
    <span v-else class="column documents-rename">
      <input
        ref="input"
        v-model="draft"
        class="documents-rename-input"
        aria-label="New name"
        @keydown="onKeydown"
        @click.stop
        @dblclick.stop
        @blur="emit('cancel')"
      />
      <span v-if="error" class="documents-rename-error text-negative" role="alert">{{ error }}</span>
    </span>
  </span>
</template>

<style scoped>
.documents-name {
  gap: 6px;
  min-width: 0;
}

.documents-name-icon {
  color: var(--os-ink-muted);
  flex: none;
}

.documents-name-gone {
  opacity: 0.6;
}

.documents-name-text {
  min-width: 0;
}

.documents-rename {
  min-width: 0;
  flex: 1;
}

.documents-rename-input {
  font: inherit;
  color: inherit;
  background: var(--os-surface);
  border: 1px solid var(--q-primary);
  border-radius: 3px;
  padding: 1px 4px;
  min-width: 8rem;
}

.documents-rename-error {
  font-size: 11px;
  white-space: normal;
}
</style>
