<script setup lang="ts">
import { computed } from 'vue';
import type { CommitRow } from '../../../shared/api';
import { moreText } from '../gitRange';
import { LOCAL_MARK, commitLocalOnly } from '../localMark';
import CommitLink from './CommitLink.vue';
import LocalMark from './LocalMark.vue';

// A list of commits (a count's range, what a merge lands, what a sync brought in, a branch's commits before a
// delete). Each ID opens the Commit view. hasOrigin: false says once that every commit is local only (no mark per row);
// absent (an older core) marks nothing. marks: an optional label per commit, keyed by sha (the delete preview's states).
const props = defineProps<{
  team: string; commits: CommitRow[]; total?: number; hasOrigin?: boolean; marks?: Record<string, { text: string; own: boolean }>; empty?: string;
}>();
const more = computed(() => moreText(props.commits.length, props.total ?? props.commits.length));
</script>

<template>
  <div class="commit-list" data-test="commit-list">
    <p v-if="!commits.length" class="muted small" data-test="commit-list-empty">{{ empty ?? 'No commits.' }}</p>
    <template v-else>
      <p v-if="hasOrigin === false" class="muted small" data-test="commit-list-no-origin">{{ LOCAL_MARK.noOrigin }}</p>
      <ul>
        <li v-for="c in commits" :key="c.sha" data-test="commit-list-row" :data-sha="c.sha">
          <CommitLink :team="team" :sha="c.sha" :length="7" />
          <span class="title">{{ c.subject || '(no message)' }}</span>
          <span v-if="marks?.[c.sha]" class="mark" :class="{ own: marks[c.sha].own }" data-test="commit-list-mark">{{ marks[c.sha].text }}</span>
          <LocalMark v-if="commitLocalOnly(c, hasOrigin === true)" />
          <span class="muted small who">{{ c.author?.name }}</span>
        </li>
      </ul>
    </template>
    <p v-if="more" class="muted small" data-test="commit-list-more">{{ more }}</p>
  </div>
</template>

<style scoped>
.commit-list { min-width: 0; }
ul { list-style: none; margin: 4px 0; padding: 0; display: flex; flex-direction: column; gap: 3px; max-height: 260px; overflow: auto; }
li { display: flex; flex-wrap: wrap; gap: 4px 8px; align-items: baseline; font-size: 12.5px; min-width: 0; }
.title { flex: 1 1 200px; min-width: 0; overflow-wrap: anywhere; }
.mark { font-size: 11px; padding: 0 5px; border-radius: 8px; background: var(--ok-bg); white-space: nowrap; }
.mark.own { background: var(--bad-bg); font-weight: 600; }
.who { white-space: nowrap; }
.small { font-size: 12px; }
p { margin: 4px 0; }
</style>
