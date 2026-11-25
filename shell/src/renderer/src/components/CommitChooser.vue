<script setup lang="ts">
import { computed } from 'vue';
import type { CommitMatch } from '../../../shared/api';
import { openCommit } from '../commitView';
import { closeChoice, commitChoice, commitTitle, repoName } from '../commitResolve';
import Modal from './Modal.vue';

// An ID that names more than one commit (the same short ID in several repositories or teams): a small list of
// repository and commit title; picking one opens the Commit view on that one. Mounted once (App.vue).
const manyTeams = computed(() => new Set(commitChoice.value?.matches.map((m) => m.team) ?? []).size > 1);

function pick(m: CommitMatch) {
  const c = commitChoice.value;
  closeChoice();
  openCommit([m.team, ...(c?.teams ?? [])], m.fullId, m.repo);
}
</script>

<template>
  <Modal v-if="commitChoice" :title="`Which commit ${commitChoice.id}?`" @close="closeChoice">
    <div data-test="commit-chooser" :data-id="commitChoice.id">
      <p class="muted small">{{ commitChoice.matches.length }} commits start with {{ commitChoice.id }}. Pick one to open it.</p>
      <ul>
        <li v-for="m in commitChoice.matches" :key="`${m.team}/${m.repo}/${m.fullId}`">
          <button type="button" class="choice" data-test="commit-choice" :data-commit-full="m.fullId" :title="commitTitle(m)" @click="pick(m)">
            <span class="repo" data-test="commit-choice-repo"><template v-if="manyTeams">{{ m.team }} · </template>{{ repoName(m) }}</span>
            <span class="title" data-test="commit-choice-title">{{ m.title || '(no title)' }}</span>
            <code>{{ m.shortId ?? m.fullId.slice(0, 12) }}</code>
          </button>
        </li>
      </ul>
    </div>
  </Modal>
</template>

<style scoped>
ul { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.choice { width: 100%; display: grid; grid-template-columns: auto 1fr auto; gap: 10px; align-items: baseline; text-align: left; padding: 8px 10px; }
.repo { font-weight: 600; white-space: nowrap; }
.title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
code { color: var(--muted); }
</style>
