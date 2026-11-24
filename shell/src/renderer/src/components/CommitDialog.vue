<script setup lang="ts">
import type { YawbleApi } from '../../../shared/api';
import { resolveApi } from '../api';
import { closeCommit, commitTarget } from '../commitView';
import Modal from './Modal.vue';
import CommitView from './CommitView.vue';

// The Commit view's dialog, mounted once (App.vue); commitTarget says which commit, and a new one replaces it (its
// key), so only one is ever open. Escape and the backdrop close it (Modal).
const props = defineProps<{ api?: YawbleApi }>();
const api = resolveApi(props.api);
</script>

<template>
  <Modal v-if="commitTarget" :key="`${commitTarget.teams.join(',')}/${commitTarget.repo ?? ''}/${commitTarget.sha}`" :title="`Commit ${commitTarget.sha.slice(0, 10)}`" wide @close="closeCommit">
    <div data-test="commit-dialog" :data-sha="commitTarget.sha">
      <CommitView :api="api" :teams="commitTarget.teams" :sha="commitTarget.sha" :repo="commitTarget.repo" @done="closeCommit" />
    </div>
  </Modal>
</template>
