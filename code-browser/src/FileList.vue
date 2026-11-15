<script setup lang="ts">
import { computed } from 'vue';
import type { CodeEntry } from './types';
import { editedAgo, formatSize, onlyChanged as filterChanged, shownMarker, sortEntries } from './markers';

// One folder's listing: folders first, then files; name, size and a quiet "edited 2 min ago"; the marker dot of what
// the agent is working on. Hidden entries (may hold secrets) and links are listed greyed and cannot be opened.
const props = defineProps<{ entries: CodeEntry[]; now: number; onlyChanged: boolean; selected: string | null }>();
const emit = defineEmits<{ open: [entry: CodeEntry] }>();

const shown = computed(() => {
  const sorted = sortEntries(props.entries);
  return props.onlyChanged ? filterChanged(sorted, props.now) : sorted;
});
const markerTitle = { live: 'Being edited now', recent: 'Edited recently', branch: 'Changed on the branch' } as const;
const openable = (e: CodeEntry) => !e.hidden && e.kind !== 'link';
</script>

<template>
  <ul class="cb-list" data-test="code-list">
    <li v-for="e in shown" :key="e.path" class="cb-entry" :class="[e.kind, { hidden: e.hidden, selected: selected === e.path }]"
        :data-test="`code-entry-${e.path}`" :data-kind="e.kind">
      <button type="button" class="cb-open" :disabled="!openable(e)"
          :title="e.hidden ? 'Hidden: may hold secrets' : e.kind === 'link' ? 'A link: not followed' : e.path"
          @click="openable(e) && emit('open', e)">
        <span class="cb-dot" :class="shownMarker(e, now) ?? 'none'" :data-marker="shownMarker(e, now) ?? ''"
            :title="shownMarker(e, now) ? markerTitle[shownMarker(e, now)!] : undefined" aria-hidden="true" />
        <span class="cb-icon" aria-hidden="true">{{ e.kind === 'dir' ? '📁' : e.kind === 'link' ? '↪' : '📄' }}</span>
        <span class="cb-name">{{ e.name }}</span>
        <span v-if="e.hidden" class="cb-note" data-test="code-hidden">hidden: may hold secrets</span>
        <span v-else class="cb-note">{{ e.kind === 'link' ? 'link' : '' }}</span>
        <span class="cb-size">{{ e.kind === 'file' ? formatSize(e.size) : '' }}</span>
        <span class="cb-ago" data-test="code-ago">{{ e.hidden ? '' : editedAgo(e.mtime, now) }}</span>
      </button>
    </li>
    <li v-if="!shown.length" class="cb-empty muted" data-test="code-list-empty">{{ onlyChanged ? 'No changed files here.' : 'This folder is empty.' }}</li>
  </ul>
</template>

<style scoped>
.cb-list { list-style: none; margin: 0; padding: 0; }
.cb-open { all: unset; box-sizing: border-box; width: 100%; display: grid; grid-template-columns: 12px 18px minmax(0, 1fr) auto auto auto; gap: 6px; align-items: center;
  padding: 3px 8px; font-size: 13px; cursor: pointer; border-radius: 4px; }
.cb-open:hover:not(:disabled) { background: var(--chip); }
.cb-open:disabled { cursor: default; }
.cb-entry.selected .cb-open { background: color-mix(in srgb, var(--accent) 10%, transparent); }
.cb-entry.hidden, .cb-entry.link { color: var(--muted); }
.cb-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.cb-note { font-size: 11.5px; font-style: italic; white-space: nowrap; }
.cb-size, .cb-ago { font-size: 11.5px; color: var(--muted); white-space: nowrap; text-align: right; }
.cb-ago { min-width: 92px; }
.cb-empty { padding: 8px; font-size: 13px; }
.cb-dot { width: 8px; height: 8px; border-radius: 50%; box-sizing: border-box; }
.cb-dot.live { background: var(--state-running); animation: cb-pulse 1.4s ease-in-out infinite; }
.cb-dot.recent { background: var(--state-waiting); }
.cb-dot.branch { border: 1.5px solid var(--ok); }
@keyframes cb-pulse { 0%, 100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--state-running) 55%, transparent); } 50% { box-shadow: 0 0 0 4px transparent; opacity: .6; } }
@media (prefers-reduced-motion: reduce) { .cb-dot.live { animation: none; } }
</style>
