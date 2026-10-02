<script setup lang="ts">
import { inject, onBeforeUnmount, ref, watch } from 'vue';
import { listDocuments } from '../../api/documents';
import type { DocumentsFolder, DocumentsFolderKey } from '../../api/types';
import { folderState, itemKey, type DocumentsLocation, type DropTarget } from '../../lib/documentsExplorer';
import { documentsDragKey } from '../../lib/useDocumentsDrag';

/**
 * THE WHOLE HIERARCHY: every team's folder at the top (live, gone and superseded, each marked),
 * and the folders inside them, loaded as they are opened.
 *
 * Roots come from `GET /api/documents`; children from listing a folder, folders only, with an
 * expander only when the folder has something in it. In `picker` mode (Move to…) it chooses a
 * destination instead of navigating, and a gone or superseded folder is shown but cannot be
 * chosen: nothing can be added to it.
 *
 * Each node is a drop target when a Documents drag is provided; resting on a closed one for
 * 800 ms opens it, so a deep folder can be reached mid-drag.
 */
const props = defineProps<{
  folders: DocumentsFolder[];
  location: DocumentsLocation;
  picker?: boolean;
}>();

const emit = defineEmits<{
  open: [location: DocumentsLocation];
  menu: [target: DropTarget, event: MouseEvent];
}>();

interface TreeNode {
  key: string;
  label: string;
  icon: string;
  caption?: string;
  folder: DocumentsFolderKey;
  path: string;
  lazy: boolean;
  disabled?: boolean;
  children?: TreeNode[];
}

/** How long a dragged item rests on a closed folder before it opens. */
const HoverExpandMs = 800;

const drag = props.picker ? null : inject(documentsDragKey, null);

const nodes = ref<TreeNode[]>([]);
const expanded = ref<string[]>([]);
const failure = ref('');

function rootNodes(folders: DocumentsFolder[]): TreeNode[] {
  return folders.map((folder) => {
    const state = folderState(folder);

    return {
      key: itemKey(folder.folder, ''),
      label: folder.label,
      icon: state === 'live' ? 'folder_shared' : 'folder_off',
      ...(state === 'retired' ? { caption: 'superseded' } : state === 'gone' ? { caption: 'gone' } : {}),
      folder: folder.folder,
      path: '',
      lazy: folder.entries > 0,
      // NOTHING CAN BE ADDED to a gone or superseded folder, so a picker cannot choose one.
      ...(props.picker && state !== 'live' ? { disabled: true } : {}),
    };
  });
}

watch(
  () => props.folders,
  (folders) => {
    nodes.value = rootNodes(folders);
    expanded.value = [];
    void expandTo(props.location);
  },
  { immediate: true },
);

function find(key: string, list: TreeNode[] = nodes.value): TreeNode | null {
  for (const node of list) {
    if (node.key === key) return node;
    const below = node.children ? find(key, node.children) : null;
    if (below) return below;
  }

  return null;
}

/** The folders inside one folder, as nodes. */
async function childrenOf(node: TreeNode): Promise<TreeNode[]> {
  const entries = await listDocuments(node.folder, node.path);

  return entries
    .filter((entry) => entry.isFolder)
    .map((entry) => ({
      key: itemKey(node.folder, entry.path),
      label: entry.name,
      icon: 'folder',
      folder: node.folder,
      path: entry.path,
      lazy: entry.children > 0,
      ...(node.disabled ? { disabled: true } : {}),
    }));
}

/** Loads a node's children once, then opens it. */
async function open(node: TreeNode) {
  if (node.lazy && !node.children) {
    try {
      node.children = await childrenOf(node);
      node.lazy = false;
    } catch (error) {
      failure.value = (error as Error).message;
      return;
    }
  }

  if (node.children && node.children.length > 0 && !expanded.value.includes(node.key)) {
    expanded.value = [...expanded.value, node.key];
  }
}

/** Q-tree asks for a closed node's children when the person opens it. */
async function onLazyLoad({ node, done, fail }: { node: TreeNode; done: (children: TreeNode[]) => void; fail: () => void }) {
  try {
    const children = await childrenOf(node);
    node.lazy = false;
    done(children);
  } catch (error) {
    failure.value = (error as Error).message;
    fail();
  }
}

/** Opens every folder on the way to a location, so the tree shows where the person is. */
async function expandTo(location: DocumentsLocation) {
  if (location.folder === null) return;

  const ancestors: string[] = [''];
  const parts = location.path.split('/').filter(Boolean);
  for (let index = 1; index < parts.length; index++) ancestors.push(parts.slice(0, index).join('/'));
  if (location.path) ancestors.push(location.path);

  for (const path of ancestors) {
    const node = find(itemKey(location.folder, path));
    if (!node) return;
    if (path !== location.path) await open(node);
  }
}

watch(
  () => [props.location.folder, props.location.path] as const,
  () => void expandTo(props.location),
);

const selectedKey = () =>
  props.location.folder === null ? null : itemKey(props.location.folder, props.location.path);

function onSelect(key: string | null) {
  if (key === null) return;

  const node = find(key);
  if (!node || node.disabled) return;

  emit('open', { folder: node.folder, path: node.path });
}

// ---- drop targets -------------------------------------------------------------------------

let hoverTimer: ReturnType<typeof setTimeout> | null = null;
let hoverKey: string | null = null;

function stopHover() {
  if (hoverTimer) clearTimeout(hoverTimer);
  hoverTimer = null;
  hoverKey = null;
}

const targetOf = (node: TreeNode): DropTarget => ({ folder: node.folder, path: node.path });

function onDragEnter(event: DragEvent, node: TreeNode) {
  if (!drag) return;

  drag.enter(event, targetOf(node));

  if (hoverKey !== node.key) {
    stopHover();
    hoverKey = node.key;
    if (!expanded.value.includes(node.key) && (node.lazy || (node.children?.length ?? 0) > 0)) {
      hoverTimer = setTimeout(() => {
        hoverTimer = null;
        void open(node);
      }, HoverExpandMs);
    }
  }
}

function onDragLeave(node: TreeNode) {
  if (!drag) return;

  drag.leave(targetOf(node));
  if (hoverKey === node.key) stopHover();
}

function onDrop(event: DragEvent, node: TreeNode) {
  stopHover();
  if (drag) void drag.drop(event, targetOf(node));
}

function dropClass(node: TreeNode) {
  const state = drag ? drag.stateOf(targetOf(node)) : null;

  return { 'documents-drop-ok': state === 'ok', 'documents-drop-refused': state === 'refused' };
}

watch(
  () => drag?.active.value,
  (active) => {
    if (!active) stopHover();
  },
);

onBeforeUnmount(stopHover);


</script>

<template>
  <div class="documents-tree">
    <div v-if="failure" class="text-negative text-caption q-pa-sm">{{ failure }}</div>
    <q-tree
      v-model:expanded="expanded"
      :nodes="nodes"
      node-key="key"
      :selected="selectedKey()"
      no-transition
      dense
      no-nodes-label="No team documents yet"
      @update:selected="onSelect"
      @lazy-load="onLazyLoad"
    >
      <template #default-header="{ node }">
        <div
          class="documents-tree-node row items-center no-wrap"
          :class="dropClass(node)"
          :data-node="node.key"
          :aria-disabled="node.disabled ? 'true' : undefined"
          @dragenter="onDragEnter($event, node)"
          @dragover="onDragEnter($event, node)"
          @dragleave="onDragLeave(node)"
          @drop="onDrop($event, node)"
          @contextmenu.prevent.stop="!picker && emit('menu', targetOf(node), $event)"
        >
          <q-icon :name="node.icon" size="18px" class="q-mr-xs documents-tree-icon" />
          <span class="ellipsis">{{ node.label }}</span>
          <span v-if="node.caption" class="os-text-muted q-ml-xs text-caption">({{ node.caption }})</span>
          <q-tooltip v-if="node.disabled">Nothing can be added to it</q-tooltip>
        </div>
      </template>
    </q-tree>
  </div>
</template>

<style scoped>
.documents-tree {
  overflow: auto;
  padding: 4px;
}

.documents-tree-node {
  min-width: 0;
  padding: 1px 4px;
  border-radius: 3px;
}

.documents-tree-icon {
  color: var(--os-ink-muted);
}
</style>
