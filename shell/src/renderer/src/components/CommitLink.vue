<script setup lang="ts">
import { computed } from 'vue';
import { isCommitId, openCommit } from '../commitView';

// A commit ID the app shows, as a link that opens the Commit view. Not an ID (empty, "—"): plain text.
const props = withDefaults(defineProps<{ team: string | string[]; sha: string | null | undefined; length?: number }>(), { length: 10 });
const ok = computed(() => !!props.sha && isCommitId(props.sha));
const shown = computed(() => (props.sha ? props.sha.slice(0, props.length) : '—'));
</script>

<template>
  <button v-if="ok" type="button" class="commit-link" data-test="commit-link" :data-sha="sha" :title="`Open commit ${sha}`"
    @click.stop="openCommit(team, sha!)"><code>{{ shown }}</code></button>
  <code v-else>{{ shown }}</code>
</template>

<style scoped>
.commit-link { all: unset; cursor: pointer; color: var(--accent); border-radius: 3px; }
.commit-link code { color: inherit; }
.commit-link:hover code { text-decoration: underline; }
.commit-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
</style>
