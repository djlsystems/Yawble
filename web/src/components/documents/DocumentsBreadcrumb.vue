<script setup lang="ts">
import { inject } from 'vue';
import { documentsDragKey } from '../../lib/useDocumentsDrag';
import type { DropTarget } from '../../lib/documentsExplorer';

/**
 * A BREADCRUMB: one button per level, root first, chevrons between. Shared by the Documents
 * explorer and `FileBrowser` (the Host picker), which is why it takes plain crumbs and emits where
 * to go rather than knowing either surface.
 *
 * A crumb that carries a `target` is a DROP TARGET when a Documents drag is provided. Under
 * `FileBrowser` nothing provides one, the inject is null, and the crumbs are plain buttons.
 */
export interface BreadcrumbCrumb {
  label: string;
  path: string;
  /** A mark beside the label: a gone or superseded team's folder. */
  icon?: string;
  note?: string;
  target?: DropTarget;
}

defineProps<{ crumbs: BreadcrumbCrumb[] }>();
const emit = defineEmits<{ go: [crumb: BreadcrumbCrumb] }>();

const drag = inject(documentsDragKey, null);

function dropClass(crumb: BreadcrumbCrumb) {
  const state = drag && crumb.target ? drag.stateOf(crumb.target) : null;

  return { 'documents-drop-ok': state === 'ok', 'documents-drop-refused': state === 'refused' };
}

function onEnter(event: DragEvent, crumb: BreadcrumbCrumb) {
  if (drag && crumb.target) drag.enter(event, crumb.target);
}

function onLeave(crumb: BreadcrumbCrumb) {
  if (drag && crumb.target) drag.leave(crumb.target);
}

function onDrop(event: DragEvent, crumb: BreadcrumbCrumb) {
  if (drag && crumb.target) void drag.drop(event, crumb.target);
}
</script>

<template>
  <nav class="row items-center no-wrap q-gutter-xs text-caption breadcrumb" aria-label="Path">
    <template v-for="(crumb, index) in crumbs" :key="`${index}:${crumb.path}`">
      <q-icon v-if="index > 0" name="chevron_right" size="14px" class="os-text-muted" />
      <q-btn
        flat
        dense
        no-caps
        size="sm"
        class="breadcrumb-crumb"
        :class="dropClass(crumb)"
        :data-crumb="crumb.path"
        :aria-current="index === crumbs.length - 1 ? 'location' : undefined"
        @click="emit('go', crumb)"
        @dragenter="onEnter($event, crumb)"
        @dragover="onEnter($event, crumb)"
        @dragleave="onLeave(crumb)"
        @drop="onDrop($event, crumb)"
      >
        <q-icon v-if="crumb.icon" :name="crumb.icon" size="14px" class="q-mr-xs" />
        <span class="ellipsis">{{ crumb.label }}</span>
        <span v-if="crumb.note" class="os-text-muted q-ml-xs">({{ crumb.note }})</span>
      </q-btn>
    </template>
  </nav>
</template>

<style scoped>
.breadcrumb {
  min-width: 0;
  overflow: hidden;
}

.breadcrumb-crumb {
  max-width: 14rem;
}
</style>
